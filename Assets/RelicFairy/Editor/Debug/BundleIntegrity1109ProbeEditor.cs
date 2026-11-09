#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

/// <summary>
/// 11-09 번들 무결성 — 23차(번들 중복 공용 그룹 정리 · 황소 텍스처 2048) 뒤 「다른 곳에서 쓸 때 빠지는 게 없나」를 실제 번들로 확인한다.
/// ① 「준비」: Addressables 플레이 모드를 「기존 빌드 사용(Use Existing Build)」으로 바꾼다(원래 값은 EditorPrefs에 적어 두고 「되돌리기」로 복원).
/// ② 플레이 중 「검사」: 공용 그룹 · 기본 데이터 그룹을 뺀 모든 항목 중 프리팹 · 재질을 번들에서 하나씩 불러
///    불러오기 실패 · 오류(분홍) 셰이더 · 원본엔 있는데 번들 쪽에서 비어 버린 텍스처 · TMP 글꼴 없음을 센다.
/// 결과: Temp/bundle_integrity_1109.txt · 로그 「[BundleIntegrity1109] 끝」.
/// </summary>
public static class BundleIntegrity1109ProbeEditor
{
    private const string Root    = "RelicFairy/Debug/11-09 번들 무결성/";
    private const string PrefKey = "RF.BundleIntegrity1109.PrevPlayModeIndex";
    private const string Out     = "Temp/bundle_integrity_1109.txt";

    [MenuItem(Root + "1 준비 — 플레이 모드 = 기존 빌드 사용")]
    private static void Prepare()
    {
        var s = AddressableAssetSettingsDefaultObject.Settings;
        int idx = s.DataBuilders.FindIndex(b => b is BuildScriptPackedPlayMode);
        if (idx < 0) { Debug.LogError("[BundleIntegrity1109] BuildScriptPackedPlayMode 없음"); return; }
        if (!EditorPrefs.HasKey(PrefKey)) EditorPrefs.SetInt(PrefKey, s.ActivePlayModeDataBuilderIndex);   // 이미 바꿔 둔 상태에서 다시 눌러도 원래 값을 덮지 않는다
        EditorPrefs.SetBool(AddressablesPlayModeFixEditor.AllowPackedPlayModeKey, true);                     // 도메인 리로드 때 FastMode로 되돌리는 것을 잠시 멈춤
        s.ActivePlayModeDataBuilderIndex = idx;
        Debug.Log($"[BundleIntegrity1109] 플레이 모드 → {s.ActivePlayModeDataBuilder.Name}");
    }

    [MenuItem(Root + "3 되돌리기 — 플레이 모드 원래대로")]
    private static void Restore()
    {
        var s = AddressableAssetSettingsDefaultObject.Settings;
        if (EditorPrefs.HasKey(PrefKey)) { s.ActivePlayModeDataBuilderIndex = EditorPrefs.GetInt(PrefKey); EditorPrefs.DeleteKey(PrefKey); }
        EditorPrefs.DeleteKey(AddressablesPlayModeFixEditor.AllowPackedPlayModeKey);
        Debug.Log($"[BundleIntegrity1109] 플레이 모드 → {s.ActivePlayModeDataBuilder.Name}");
    }

    [MenuItem(Root + "2 검사 (플레이 중)")]
    private static void Run()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[BundleIntegrity1109] 플레이 모드에서"); return; }
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        var sb = new StringBuilder("11-09 번들 무결성\n");
        var s = AddressableAssetSettingsDefaultObject.Settings;
        sb.AppendLine($"플레이 모드: {s.ActivePlayModeDataBuilder.Name}");

        // 원본 재질 — 번들 쪽에서 텍스처가 빈 재질만 그때그때 같은 이름으로 찾는다(11-09 1차: 4,354개를 한꺼번에 읽다 메모리 40GB+).
        var srcMats = new Dictionary<string, List<(string shader, HashSet<string> filled)>>();
        List<(string shader, HashSet<string> filled)> Source(string name)
        {
            if (srcMats.TryGetValue(name, out var list)) return list;
            srcMats[name] = list = new List<(string, HashSet<string>)>();
            foreach (var g in AssetDatabase.FindAssets($"\"{name}\" t:Material"))
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != name) continue;
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null || m.shader == null) continue;
                var filled = new HashSet<string>();
                foreach (var p in m.GetTexturePropertyNames()) if (m.GetTexture(p) != null) filled.Add(p);
                list.Add((m.shader.name, filled));
            }
            return list;
        }

        int loaded = 0, failed = 0, pink = 0, missTex = 0, missFont = 0, entries = 0;
        var fails = new StringBuilder(); var pinks = new StringBuilder(); var misses = new StringBuilder();
        var seenMat = new HashSet<int>();
        bool abort = false;
        try
        {
            foreach (var group in s.groups)
            {
                if (abort) break;
                if (group == null || group.Name == "Duplicate Asset Isolation" || group.ReadOnly) continue;
                foreach (var e in group.entries)
                {
                    if (e == null || e.IsFolder) continue;
                    var t = e.MainAssetType;
                    if (t != typeof(GameObject) && t != typeof(Material)) continue;
                    entries++;
                    AsyncOperationHandle<Object> h = default;
                    try
                    {
                        h = Addressables.LoadAssetAsync<Object>(e.address);
                        while (!h.IsDone) await UniTask.Yield();
                        var obj = h.Status == AsyncOperationStatus.Succeeded ? h.Result : null;
                        if (obj == null) { failed++; fails.AppendLine($"    {h.Status}  {group.Name} · {e.address} — {h.OperationException?.Message}"); continue; }
                        loaded++;
                        var mats = new List<Material>();
                        if (obj is Material mm) mats.Add(mm);
                        else if (obj is GameObject go)
                        {
                            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) mats.AddRange(r.sharedMaterials);
                            foreach (var tx in go.GetComponentsInChildren<TMP_Text>(true))
                                if (tx.font == null) { missFont++; misses.AppendLine($"    글꼴 없음  {e.address} › {tx.name}"); }
                        }
                        foreach (var m in mats)
                        {
                            if (m == null || !seenMat.Add(m.GetInstanceID())) continue;
                            var sh = m.shader;
                            if (sh == null || sh.name == "Hidden/InternalErrorShader" || !sh.isSupported)
                            { pink++; pinks.AppendLine($"    {(sh == null ? "셰이더 없음" : sh.name)}  {m.name}  ← {e.address}"); continue; }
                            List<(string shader, HashSet<string> filled)> srcs = null;
                            foreach (var p in m.GetTexturePropertyNames())
                            {
                                if (m.GetTexture(p) != null) continue;
                                srcs ??= Source(m.name);
                                bool allFilled = true, any = false;
                                foreach (var (shader, filled) in srcs)
                                {
                                    if (shader != sh.name) continue;
                                    any = true;
                                    if (!filled.Contains(p)) { allFilled = false; break; }
                                }
                                if (any && allFilled) { missTex++; misses.AppendLine($"    텍스처 빔  {m.name}.{p}  ← {e.address}"); }
                            }
                        }
                    }
                    catch (Exception ex) { failed++; fails.AppendLine($"    실패  {group.Name} · {e.address} — {ex.GetType().Name}: {ex.Message}"); }
                    finally { if (h.IsValid()) Addressables.Release(h); }
                    if (entries % 20 == 0)
                    {
                        await Resources.UnloadUnusedAssets();
                        EditorUtility.UnloadUnusedAssetsImmediate();
                        long mb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
                        Debug.Log($"[BundleIntegrity1109] 진행 {entries} · 메모리 {mb}MB");
                        if (mb > 12000) { abort = true; sb.AppendLine($"메모리 상한(12GB) — {entries}번째에서 중단"); break; }
                    }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("예외: " + ex); }
        finally
        {
            sb.AppendLine($"검사 항목(프리팹·재질) {entries} · 불러옴 {loaded} · 실패 {failed} · 분홍 재질 {pink} · 빈 텍스처 {missTex} · 글꼴 없음 {missFont}");
            if (fails.Length > 0) sb.Append("  불러오기 실패:\n").Append(fails);
            if (pinks.Length > 0) sb.Append("  분홍 재질:\n").Append(pinks);
            if (misses.Length > 0) sb.Append("  빈 텍스처 · 글꼴:\n").Append(misses);
            Directory.CreateDirectory("Temp");
            File.WriteAllText(Out, sb.ToString());
            Debug.Log("[BundleIntegrity1109] 끝\n" + sb.ToString(0, Mathf.Min(sb.Length, 3000)));
        }
    }
}
#endif
