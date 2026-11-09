#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 11-09 출시 전 결함 고치기(사용자 「개선 내용은 바로 진행」) — Defect1109ProbeEditor 진단 결과.
/// ① 모르드레드 「빈 깡통」: 움직이는 몸 메시 SK_DarkKnight2_Body는 서브메시 2(몸 · 머리)인데 재질 1칸 → 머리가 안 그려져 투구 안이 비었다.
///    머리 텍스처가 프로젝트에 없어 어두운 DK_Head 재질로 채운다(투구 안 그림자). 망토(Armor_Cloth)도 서브메시 2 · 재질 1 → 같은 재질로 채운다.
///    대상: DeathKnightBoss.prefab · Arena_Boss_Ch3.prefab(풀린 사본). Game_Intro 씬은 MCP로 따로.
/// ② VolumetricBlood2(플레이어 SwordHitImpact) 분홍 — 재질이 오류 셰이더로 잡힘(UberParticles 셰이더 자체는 정상) → 다시 가져오고, 그래도면 셰이더 재연결.
/// ③ BirchTrunk(Ch1 보스방 · 보스 길) 빈 텍스처 — 모델 내장 재질(텍스처 없음)을 쓴다. 텍스처 있는 재질은 깃 밖 _ThirdParty에만 있어
///    텍스처 · 재질을 _Imported로 복사하고, 내장 재질을 쓰는 프리팹 칸만 새 재질로 바꾼다.
/// 결과: Temp/fix_1109.txt · 로그 「[Fix1109] 끝」. 미리보기: Temp/mordred_{태그}.png.
/// </summary>
public static class Fix1109Editor
{
    private const string Root   = "RelicFairy/Debug/11-09 결함 고치기/";
    private const string DkDir  = "Assets/RelicFairy/Characters/Monster/Monster/DeathKnightBoss/Art/Materials/";
    private const string RhxSrc = "Assets/_ThirdParty/Rhinox Studio/RPG - Stylized Fantasy Environment URP/";
    private const string RhxDst = "Assets/RelicFairy/_Imported/Rhinox Studio/RPG - Stylized Fantasy Environment URP/";
    private const string BirchFbx = RhxDst + "Models/Birch Trees/Birch_Tree_Group.fbx";

    [MenuItem(Root + "1 모르드레드 미리보기 — 고치기 전")] private static void ShotBefore() => Shot("before");
    [MenuItem(Root + "3 모르드레드 미리보기 — 고친 뒤")]  private static void ShotAfter()  => Shot("after");

    [MenuItem(Root + "2 고치기 (모르드레드 · 분홍 이펙트 · 자작나무)")]
    private static void FixAll()
    {
        var sb = new StringBuilder("11-09 결함 고치기\n");
        FixMordred(sb);
        FixBlood(sb);
        FixBirch(sb);
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("Temp");
        File.WriteAllText("Temp/fix_1109.txt", sb.ToString());
        Debug.Log("[Fix1109] 끝\n" + sb);
    }

    // ── ① 모르드레드 ──
    private static void FixMordred(StringBuilder sb)
    {
        string headPath = DkDir + "DK_Head.mat";
        var head = AssetDatabase.LoadAssetAtPath<Material>(headPath);
        if (head == null)
        {
            var body = AssetDatabase.LoadAssetAtPath<Material>(DkDir + "DK_Body.mat");
            head = new Material(body.shader) { name = "DK_Head" };
            head.SetColor("_BaseColor", new Color(0.035f, 0.03f, 0.045f, 1f));   // 투구 안 그림자 — 머리 텍스처가 없어 텍스처 없이 어둡게
            head.SetFloat("_Smoothness", 0.2f);
            head.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(head, headPath);
            sb.AppendLine($"① DK_Head 재질 만듦 — {headPath}");
        }
        foreach (var path in new[] { "Assets/RelicFairy/Characters/Monster/Monster/DeathKnightBoss/Prefab/DeathKnightBoss.prefab",
                                     "Assets/RelicFairy/Systems/Stage/MapGen/Prefabs/Arena_Boss_Ch3.prefab" })
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            int changed = 0;
            try
            {
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mesh = smr.sharedMesh;
                    if (mesh == null || smr.transform.parent == null || smr.transform.parent.name != "DarkKnight2") continue;
                    var mats = smr.sharedMaterials;
                    if (mats.Length >= mesh.subMeshCount) continue;
                    var fixedMats = new Material[mesh.subMeshCount];
                    for (int i = 0; i < fixedMats.Length; i++)
                        fixedMats[i] = i < mats.Length ? mats[i] : (smr.name == "SK_DarkKnight2_Body" ? head : mats[0]);
                    smr.sharedMaterials = fixedMats;
                    changed++;
                    sb.AppendLine($"  {Path.GetFileNameWithoutExtension(path)} › {smr.name}: [{string.Join(", ", mats.Select(m => m ? m.name : "null"))}] → [{string.Join(", ", fixedMats.Select(m => m ? m.name : "null"))}]");
                }
                if (changed > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            sb.AppendLine($"① {Path.GetFileName(path)} 고친 렌더러 {changed}");
        }
    }

    // ── ② VolumetricBlood2 ──
    private static void FixBlood(StringBuilder sb)
    {
        const string matPath = "Assets/RelicFairy/_Imported/EffectSource/MasterStylizedProjectiles/Projectiles/Arrow/Materials/VolumetricBlood2.mat";
        const string sgPath  = "Assets/RelicFairy/_Imported/EffectSource/MasterStylizedProjectiles/Shaders/UberParticles.shadergraph";
        AssetDatabase.ImportAsset(matPath, ImportAssetOptions.ForceUpdate);
        var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        string before = m.shader ? m.shader.name : "null";
        if (m.shader == null || m.shader.name == "Hidden/InternalErrorShader")
        {
            var sg = AssetDatabase.LoadAssetAtPath<Shader>(sgPath);
            if (sg != null) { m.shader = sg; EditorUtility.SetDirty(m); }
        }
        sb.AppendLine($"② VolumetricBlood2 셰이더 {before} → {(m.shader ? m.shader.name : "null")} · 지원 {(m.shader && m.shader.isSupported)}");
        // 같은 셰이더 그래프를 쓰는 다른 재질 상태
        int ok = 0, bad = 0;
        string sgGuid = AssetDatabase.AssetPathToGUID(sgPath);
        foreach (var g in AssetDatabase.FindAssets("t:Material", new[] { "Assets/RelicFairy/_Imported/EffectSource/MasterStylizedProjectiles" }))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (!File.ReadAllText(p).Contains(sgGuid)) continue;
            var mm = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mm.shader != null && mm.shader.name != "Hidden/InternalErrorShader") ok++; else { bad++; sb.AppendLine($"    오류 셰이더 {p}"); }
        }
        sb.AppendLine($"  UberParticles 재질 정상 {ok} · 오류 {bad}");
    }

    // ── ③ BirchTrunk ──
    private static void FixBirch(StringBuilder sb)
    {
        string texDir = RhxDst + "Textures/Pine and Commons/";
        string baseTex = texDir + "BirchTrunk_basecolor.png", normTex = texDir + "BirchTrunk_normal.png";
        if (!File.Exists(baseTex)) AssetDatabase.CopyAsset(RhxSrc + "Textures/Pine and Commons/BirchTrunk_basecolor.png", baseTex);
        if (!File.Exists(normTex)) AssetDatabase.CopyAsset(RhxSrc + "Textures/Pine and Commons/BirchTrunk_normal.png", normTex);
        if (AssetImporter.GetAtPath(normTex) is TextureImporter ni && ni.textureType != TextureImporterType.NormalMap) { ni.textureType = TextureImporterType.NormalMap; ni.SaveAndReimport(); }

        string matPath = RhxDst + "Materials/BirchTrunk.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            AssetDatabase.CopyAsset(RhxSrc + "Materials/Trees/Barks/BirchTrunk.mat", matPath);
            mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        }
        var bt = AssetDatabase.LoadAssetAtPath<Texture2D>(baseTex); var nt = AssetDatabase.LoadAssetAtPath<Texture2D>(normTex);
        mat.SetTexture("_BaseMap", bt); mat.SetTexture("_MainTex", bt); mat.SetTexture("_BumpMap", nt);
        mat.EnableKeyword("_NORMALMAP");
        EditorUtility.SetDirty(mat);
        sb.AppendLine($"③ BirchTrunk 재질 {matPath} · 텍스처 {AssetDatabase.GetAssetPath(bt)} · {AssetDatabase.GetAssetPath(nt)}");

        // 모델 내장 BirchTrunk(텍스처 없음)를 쓰는 프리팹 칸만 바꾼다
        var embedded = AssetDatabase.LoadAllAssetsAtPath(BirchFbx).OfType<Material>().FirstOrDefault(x => x.name == "BirchTrunk");
        if (embedded == null) { sb.AppendLine("  모델 내장 BirchTrunk 없음"); return; }
        string fbxGuid = AssetDatabase.AssetPathToGUID(BirchFbx);
        foreach (var g in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/RelicFairy" }))
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (!File.ReadAllText(p).Contains(fbxGuid)) continue;
            var root = PrefabUtility.LoadPrefabContents(p);
            int n = 0;
            try
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials; bool hit = false;
                    for (int i = 0; i < mats.Length; i++) if (mats[i] == embedded) { mats[i] = mat; hit = true; n++; }
                    if (hit) r.sharedMaterials = mats;
                }
                if (n > 0) PrefabUtility.SaveAsPrefabAsset(root, p);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            sb.AppendLine($"  {p} — 바꾼 칸 {n}");
        }
    }

    // ── 미리보기 ──
    private static void Shot(string tag)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/RelicFairy/Characters/Monster/Monster/DeathKnightBoss/Prefab/DeathKnightBoss.prefab");
        var pru = new PreviewRenderUtility();
        try
        {
            pru.camera.fieldOfView = 28f; pru.camera.nearClipPlane = 0.01f; pru.camera.farClipPlane = 100f;
            pru.camera.clearFlags = CameraClearFlags.SolidColor; pru.camera.backgroundColor = new Color(0.32f, 0.34f, 0.40f);
            pru.lights[0].intensity = 1.3f; pru.lights[0].transform.rotation = Quaternion.Euler(25f, 160f, 0f);
            pru.lights[1].intensity = 0.7f; pru.lights[1].transform.rotation = Quaternion.Euler(-10f, 20f, 0f);
            pru.ambientColor = new Color(0.35f, 0.35f, 0.4f);
            var go = pru.InstantiatePrefabInScene(prefab);
            var headBone = go.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "head");
            Vector3 target = headBone ? headBone.position : go.transform.position + Vector3.up * 1.8f;
            var shots = new (string name, Vector3 offset, float fov)[]
            {
                ("face",  go.transform.forward * 1.3f + Vector3.up * 0.05f, 24f),
                ("high",  go.transform.forward * 2.6f + Vector3.up * 2.4f, 30f),   // 게임처럼 위에서
                ("full",  go.transform.forward * 5.5f + Vector3.up * 0.2f + Vector3.down * 0.0f, 32f),
            };
            foreach (var s in shots)
            {
                pru.camera.fieldOfView = s.fov;
                Vector3 look = s.name == "full" ? target + Vector3.down * 0.9f : target;
                pru.camera.transform.position = look + s.offset;
                pru.camera.transform.LookAt(look);
                pru.BeginStaticPreview(new Rect(0, 0, 900, 900));
                pru.Render(true);
                var tex = pru.EndStaticPreview();
                Directory.CreateDirectory("Temp");
                File.WriteAllBytes($"Temp/mordred_{tag}_{s.name}.png", tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }
            Debug.Log($"[Fix1109] 미리보기 {tag} — 머리 뼈 {(headBone ? "찾음" : "없음")} · Temp/mordred_{tag}_*.png");
        }
        finally { pru.Cleanup(); }
    }
}
#endif
