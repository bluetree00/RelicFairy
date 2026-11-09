#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 11-06 사용자 「용량 · 그래픽 최적화 중 인게임에 문제없는 건 바로」 — 화면 · 동작이 그대로인 것만(점검 보고서 구현설계/RelicFairy_용량·그래픽최적화_점검_20261106.md).
/// ① Addressables 번들 중복 → 공용 그룹 하나(Analyze 「중복 번들 의존」 고치기) · ② 실제 4096 텍스처(중간보스 황소 · 물 노멀) 최대 2048 ·
/// ③ 충돌 메시 미리 굽기 · ④ 「성능 우선」 렌더러 SSAO 절반 해상도(균형과 같게). 결과 Temp/optimize1106.txt · 로그 「[OPT1106]」.
/// </summary>
public static class Optimize1106Editor
{
    private const string Root = "RelicFairy/Tools/11-06 최적화/";
    private const string Out  = "Temp/optimize1106.txt";
    private const int    TexMax = 2048;

    [MenuItem(Root + "1 번들 중복 정리 (공용 그룹)")]
    private static void Dedupe()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null) { Write("Addressables 설정 없음"); return; }
        if (settings.FindGroup("Duplicate Asset Isolation") != null) { Write("이미 공용 그룹이 있다 — 건너뜀"); return; }
        var rule = new CheckBundleDupeDependencies();
        var results = rule.RefreshAnalysis(settings);
        int issues = results.Count(r => r.severity != MessageType.None);
        rule.FixIssues(settings);
        var g = settings.FindGroup("Duplicate Asset Isolation");
        AssetDatabase.SaveAssets();
        Write($"번들 중복 정리 — 분석 결과 {issues}줄 · 공용 그룹 항목 {(g != null ? g.entries.Count : 0)}개");
    }

    [MenuItem(Root + "2 4096 텍스처 → 2048 (황소 · 물 노멀)")]
    private static void Textures()
    {
        var paths = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/RelicFairy/_Imported/Zectorlab/BullTank", "Assets/RelicFairy/_Imported/Rhinox Studio" })
                                 .Select(AssetDatabase.GUIDToAssetPath).ToList();
        var sb = new StringBuilder();
        int n = 0;
        foreach (var p in paths)
        {
            if (AssetImporter.GetAtPath(p) is not TextureImporter ti) continue;
            ti.GetSourceTextureWidthAndHeight(out int w, out int h);
            if (Mathf.Max(w, h) <= TexMax) continue;
            bool changed = false;
            if (ti.maxTextureSize > TexMax) { ti.maxTextureSize = TexMax; changed = true; }
            foreach (var plat in new[] { "Standalone", "DefaultTexturePlatform" })
            {
                var s = ti.GetPlatformTextureSettings(plat);
                if (s.overridden && s.maxTextureSize > TexMax) { s.maxTextureSize = TexMax; ti.SetPlatformTextureSettings(s); changed = true; }
            }
            if (!changed) continue;
            ti.SaveAndReimport();
            n++;
            sb.AppendLine($"  {w}x{h} → {TexMax}  {p}");
        }
        Write($"텍스처 최대 {TexMax} — {n}장\n{sb}");
    }

    [MenuItem(Root + "3 충돌 메시 미리 굽기 켜기")]
    private static void PrebakeCollision()
    {
        bool before = PlayerSettings.bakeCollisionMeshes;
        PlayerSettings.bakeCollisionMeshes = true;
        AssetDatabase.SaveAssets();
        Write($"충돌 메시 미리 굽기 {before} → {PlayerSettings.bakeCollisionMeshes}");
    }

    [MenuItem(Root + "4 성능 우선 SSAO 절반 해상도")]
    private static void PerformantSsao()
    {
        const string path = "Assets/Settings/URP-Performant-Renderer.asset";
        var sb = new StringBuilder();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (o is not ScriptableRendererFeature f || !f.GetType().Name.Contains("ScreenSpaceAmbientOcclusion")) continue;
            var so = new SerializedObject(f);
            var prop = so.FindProperty("m_Settings.Downsample");
            if (prop == null) { sb.AppendLine("  Downsample 속성 못 찾음"); continue; }
            sb.AppendLine($"  {f.name} Downsample {prop.boolValue} → True");
            prop.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(f);
        }
        AssetDatabase.SaveAssets();
        Write("성능 우선 SSAO\n" + sb);
    }

    [MenuItem(Root + "0 모두 (1~4)")]
    private static void All()
    {
        File.WriteAllText(Out, "");
        Dedupe(); Textures(); PrebakeCollision(); PerformantSsao();
        Debug.Log("[OPT1106] 끝");
    }

    private static void Write(string s)
    {
        File.AppendAllText(Out, s + "\n");
        Debug.Log("[OPT1106] " + s);
    }
}
#endif
