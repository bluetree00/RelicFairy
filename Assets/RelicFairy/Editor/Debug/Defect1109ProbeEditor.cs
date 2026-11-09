#if UNITY_EDITOR
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 11-09 출시 전 결함 진단(플레이 없이) — 번들 무결성 검사가 찾은 원래 있던 결함 2종 + 사용자 「모르드레드 빈 깡통」.
/// ① VolumetricBlood2(SwordHitImpact) 분홍 — 셰이더 오류 메시지 · 재질 텍스처 · 쓰는 프리팹
/// ② BirchTrunk(Arena/Road_Boss_Ch1) 빈 텍스처 — 렌더러 경로 · 재질 출처(.mat / 모델 내장) · 텍스처
/// ③ 모르드레드 — DeathKnightBoss · Arena_Boss_Ch3 안 사본의 스키닝 메시마다 서브메시 수 ↔ 재질 칸 수 · 켜짐 · 뼈대
/// 결과: Temp/defect_1109.txt · 로그 「[Defect1109] 끝」.
/// </summary>
public static class Defect1109ProbeEditor
{
    private const string Out = "Temp/defect_1109.txt";

    [MenuItem("RelicFairy/Debug/11-09 결함 진단 (플레이 없이)")]
    private static void Run()
    {
        var sb = new StringBuilder("11-09 결함 진단\n");

        // ① VolumetricBlood2
        sb.AppendLine("① VolumetricBlood2");
        foreach (var g in AssetDatabase.FindAssets("VolumetricBlood2 t:Material"))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) continue;
            sb.AppendLine($"  재질 {p} · 셰이더 {(m.shader != null ? m.shader.name : "null")} · 지원 {(m.shader != null && m.shader.isSupported)}");
            if (m.shader != null)
                foreach (var msg in ShaderUtil.GetShaderMessages(m.shader).Take(6))
                    sb.AppendLine($"    셰이더 메시지 [{msg.severity}] {msg.message} ({msg.file}:{msg.line})");
            foreach (var t in m.GetTexturePropertyNames())
                if (m.GetTexture(t) != null) sb.AppendLine($"    텍스처 {t} = {AssetDatabase.GetAssetPath(m.GetTexture(t))}");
            foreach (var kw in m.shaderKeywords) sb.Append($"    키워드 {kw}\n");
            sb.AppendLine($"    렌더 큐 {m.renderQueue}");
            foreach (var dep in AssetDatabase.FindAssets("t:Prefab").Select(AssetDatabase.GUIDToAssetPath)
                                             .Where(pp => pp.Contains("SwordHitImpact") || AssetDatabase.GetDependencies(pp, false).Contains(p)).Take(10))
                sb.AppendLine($"    쓰는 프리팹 {dep}");
        }
        foreach (var g in AssetDatabase.FindAssets("UberParticles"))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            var sh = AssetDatabase.LoadAssetAtPath<Shader>(p);
            if (sh == null) continue;
            sb.AppendLine($"  셰이더 {p} · 이름 {sh.name} · 지원 {sh.isSupported} · 오류 {ShaderUtil.ShaderHasError(sh)}");
            foreach (var msg in ShaderUtil.GetShaderMessages(sh).Take(6)) sb.AppendLine($"    [{msg.severity}] {msg.message}");
        }

        // ② BirchTrunk
        sb.AppendLine("② BirchTrunk");
        foreach (var path in new[] { "Arena_Boss_Ch1", "Road_Boss_Ch1" })
        {
            string pp = AssetDatabase.FindAssets(path + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == path);
            var root = pp != null ? AssetDatabase.LoadAssetAtPath<GameObject>(pp) : null;
            if (root == null) { sb.AppendLine($"  {path} 없음"); continue; }
            int n = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                for (int i = 0; i < r.sharedMaterials.Length; i++)
                {
                    var m = r.sharedMaterials[i];
                    if (m == null || m.name != "BirchTrunk") continue;
                    if (n++ < 4)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        sb.AppendLine($"  {path} › {HierPath(r.transform, root.transform)} [{i}] 재질 출처 {AssetDatabase.GetAssetPath(m)} · 메시 {(mf && mf.sharedMesh ? AssetDatabase.GetAssetPath(mf.sharedMesh) : "-")} · 셰이더 {m.shader.name}");
                        foreach (var t in m.GetTexturePropertyNames()) sb.AppendLine($"      {t} = {(m.GetTexture(t) ? AssetDatabase.GetAssetPath(m.GetTexture(t)) : "비어 있음")}");
                    }
                }
            sb.AppendLine($"  {path} BirchTrunk 칸 {n}개");
        }
        foreach (var g in AssetDatabase.FindAssets("BirchTrunk"))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) continue;
            sb.Append($"  같은 이름 재질 {p} · {m.shader.name} ·");
            foreach (var t in m.GetTexturePropertyNames()) if (m.GetTexture(t)) sb.Append($" {t}");
            sb.AppendLine();
        }

        // ③ 모르드레드
        sb.AppendLine("③ 모르드레드");
        foreach (var name in new[] { "DeathKnightBoss", "Arena_Boss_Ch3" })
        {
            string pp = AssetDatabase.FindAssets(name + " t:Prefab").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == name);
            var root = pp != null ? AssetDatabase.LoadAssetAtPath<GameObject>(pp) : null;
            if (root == null) { sb.AppendLine($"  {name} 없음"); continue; }
            sb.AppendLine($"  [{name}] {pp}");
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (name == "Arena_Boss_Ch3" && !HierPath(smr.transform, root.transform).ToLower().Contains("knight") && !HierPath(smr.transform, root.transform).Contains("DK")) continue;
                var anim = smr.GetComponentInParent<Animator>(true);
                sb.AppendLine($"    {HierPath(smr.transform, root.transform)} · 켜짐 {smr.enabled && smr.gameObject.activeInHierarchy} · 메시 {(mesh ? mesh.name : "null")} 서브메시 {(mesh ? mesh.subMeshCount : 0)} · 재질 {smr.sharedMaterials.Length}개 [{string.Join(", ", smr.sharedMaterials.Select(m => m ? m.name : "null"))}] · 뼈 루트 {(smr.rootBone ? smr.rootBone.name : "null")} · 애니메이터 {(anim ? HierPath(anim.transform, root.transform) : "없음")}");
            }
        }

        Directory.CreateDirectory("Temp");
        File.WriteAllText(Out, sb.ToString());
        Debug.Log("[Defect1109] 끝\n" + sb.ToString(0, Mathf.Min(sb.Length, 2500)));
    }

    private static string HierPath(Transform t, Transform root)
    {
        var s = t.name;
        for (var p = t.parent; p != null && p != root.parent; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
#endif
