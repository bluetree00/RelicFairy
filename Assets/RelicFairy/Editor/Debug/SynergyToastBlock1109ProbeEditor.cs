#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 11-09 피드백 「룬판 시너지 알림이 빛 룬 가운데 줄을 막아 사라질 때까지 못 건드린다」 실측(테스트 허브 · 플레이 중).
/// 룬판을 열고 「◆ 시너지 활성화!」 띠를 띄운 뒤 ① 띠가 판 칸과 겹치지 않나 ② 예전 띠 자리(판 둘째 줄)를 누르면 칸이 잡히나
/// ③ 새 띠 자리를 눌러도 띠가 클릭을 먹지 않나. 결과: Temp/synergy_toast_1109.txt · Temp/ui_shots/TOAST_1109.png · 로그 「[ToastBlock1109] 끝」.
/// </summary>
public static class SynergyToastBlock1109ProbeEditor
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private static int s_pass, s_all;

    [MenuItem("RelicFairy/Debug/11-09 룬판 시너지 알림 가림 확인 (테스트 허브, 플레이 중)")]
    private static void Run()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[ToastBlock1109] 플레이 모드(테스트 허브)에서"); return; }
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        s_pass = s_all = 0;
        var sb = new StringBuilder("11-09 룬판 시너지 알림 가림 확인\n");
        try
        {
            var ensure = typeof(SynergyUiShotProbeEditor).GetMethod("EnsureRunAsync", Stat);
            if (ensure == null || !await (UniTask<bool>)ensure.Invoke(null, new object[] { sb })) { Check(sb, "런 시작", false); return; }

            if (UI_GridPanel.Instance == null) Managers.UI?.ShowOverlayUI<UI_GridPanel>();
            UI_GridPanel.Instance?.ShowWithNewItem(null);
            await Wait(1.0f);
            var panel = UI_GridPanel.Instance;
            if (panel == null) { Check(sb, "룬판 열림", false); return; }

            typeof(UI_GridPanel).GetMethod("ShowSynergyActivated", Inst)?.Invoke(panel, new object[] { "LIGHT: probe" });
            await Wait(0.35f);

            var toast = typeof(UI_GridPanel).GetField("_synergyToast", Inst)?.GetValue(panel) as GameObject;
            Check(sb, "알림 띠 켜짐", toast != null && toast.activeInHierarchy);
            if (toast == null) return;
            var toastRect = WorldRect((RectTransform)toast.transform);
            var panelRT = (RectTransform)panel.transform;

            // ① 띠 ↔ 판 칸 겹침
            var grid  = typeof(UI_GridPanel).GetField("_hexGridView", Inst)?.GetValue(panel) as Component;
            var cells = grid != null ? typeof(MerlinRuneHexGridView).GetField("_cellFlashImages", Inst)?.GetValue(grid) as Dictionary<Vector2Int, Image> : null;
            int overlap = 0, total = 0;
            float gridTop = float.MinValue;
            if (cells != null)
                foreach (var kv in cells)
                {
                    if (kv.Value == null || !kv.Value.gameObject.activeInHierarchy) continue;
                    total++;
                    var r = WorldRect(kv.Value.rectTransform);
                    gridTop = Mathf.Max(gridTop, r.yMax);
                    if (Mathf.Min(r.xMax, toastRect.xMax) - Mathf.Max(r.xMin, toastRect.xMin) > 1f &&
                        Mathf.Min(r.yMax, toastRect.yMax) - Mathf.Max(r.yMin, toastRect.yMin) > 1f) overlap++;
                }
            Check(sb, $"① 띠가 판 칸과 겹치지 않음 — 겹친 칸 {overlap}/{total} · 띠 아래 {toastRect.yMin:F0} · 판 맨 위 {gridTop:F0}", total > 0 && overlap == 0);

            // ② 예전 띠 자리(판 위쪽 0.80~0.86 · 가로 0.24~0.73 가운데) — 이제 판이 잡히나
            var (oldTop, oldToast, oldGrid) = TopHit(PanelPoint(panelRT, 0.485f, 0.83f), toast.transform, grid != null ? grid.transform : null);
            Check(sb, $"② 예전 띠 자리 맨 위 판정 = {oldTop} · 띠 {oldToast} · 판 {oldGrid}", !oldToast && oldGrid);

            // ③ 새 띠 한가운데를 눌러도 띠가 먹지 않나
            var (newTop, newToast, _) = TopHit(toastRect.center, toast.transform, null);
            Check(sb, $"③ 새 띠 자리 맨 위 판정 = {newTop} · 띠 {newToast}", !newToast);

            // ④ 판 칸 전수 — 칸 한가운데를 누르면 판이 잡히나(띠가 떠 있는 동안). 판이 아닌 게 맨 위면 그 이름을 센다.
            var blockers = new Dictionary<string, int>();
            int blocked = 0, swept = 0;
            if (cells != null)
                foreach (var kv in cells)
                {
                    if (kv.Value == null || !kv.Value.gameObject.activeInHierarchy) continue;
                    swept++;
                    var (n, _, inGrid) = TopHit(WorldRect(kv.Value.rectTransform).center, toast.transform, grid.transform);
                    if (inGrid) continue;
                    blocked++;
                    blockers[n] = blockers.TryGetValue(n, out int c) ? c + 1 : 1;
                }
            var bl = new StringBuilder();
            foreach (var kv in blockers) bl.Append($"{kv.Key}×{kv.Value} ");
            Check(sb, $"④ 판 칸 전수 — 판이 아닌 게 맨 위인 칸 {blocked}/{swept} {bl}", swept > 0 && blocked == 0);

            // GridNameText(Puzzle 프리팹의 옛 글자) — 어디 · 무엇 · 클릭을 먹나
            foreach (var t in Object.FindObjectsByType<TMPro.TMP_Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (t.name != "GridNameText") continue;
                var r = WorldRect(t.rectTransform);
                sb.AppendLine($"  INFO GridNameText 「{t.text}」 raycast {t.raycastTarget} · 보임 {t.enabled && t.alpha > 0.01f} · 사각형 x {r.xMin:F0}~{r.xMax:F0} y {r.yMin:F0}~{r.yMax:F0} · 부모 {HierPath(t.transform)}");
            }

            await Shot("TOAST_1109", 0);
        }
        catch (System.Exception e) { sb.AppendLine("예외: " + e); }
        finally
        {
            if (UI_GridPanel.Instance != null) typeof(UI_GridPanel).GetMethod("ClosePanel", Inst)?.Invoke(UI_GridPanel.Instance, null);
            sb.AppendLine($"CHECK 합계 {s_pass}/{s_all}");
            Directory.CreateDirectory("Temp");
            File.WriteAllText(Path.Combine("Temp", "synergy_toast_1109.txt"), sb.ToString());
            Debug.Log("[ToastBlock1109] 끝\n" + sb);
        }
    }

    /// <summary>패널 사각형 안 비율 위치 → 화면 좌표(오버레이 캔버스 = 월드 좌표가 화면 좌표).</summary>
    private static Vector2 PanelPoint(RectTransform rt, float fx, float fy)
    {
        var r = WorldRect(rt);
        return new Vector2(Mathf.Lerp(r.xMin, r.xMax, fx), Mathf.Lerp(r.yMin, r.yMax, fy));
    }

    /// <summary>그 점의 맨 위 UI 판정 이름 · 띠(toast)가 맞았나 · 맨 위가 판(grid) 안인가.</summary>
    private static (string name, bool toast, bool grid) TopHit(Vector2 screen, Transform toastRoot, Transform gridRoot)
    {
        if (EventSystem.current == null) return ("(EventSystem 없음)", false, false);
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = screen }, hits);
        if (hits.Count == 0) return ("(없음)", false, false);
        var top = hits[0].gameObject.transform;
        bool toastHit = false;
        foreach (var h in hits) if (h.gameObject.transform.IsChildOf(toastRoot)) { toastHit = true; break; }
        return (top.name, toastHit, gridRoot != null && top.IsChildOf(gridRoot));
    }

    private static string HierPath(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    private static Rect WorldRect(RectTransform rt)
    {
        var c = new Vector3[4];
        rt.GetWorldCorners(c);
        return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
    }

    private static void Check(StringBuilder sb, string what, bool ok)
    {
        s_all++;
        if (ok) s_pass++;
        sb.AppendLine($"  CHECK {what}: {ok}");
    }

    private static UniTask Wait(float s) => UniTask.Delay((int)(s * 1000), DelayType.Realtime);

    private static UniTask Shot(string name, int settleMs)
    {
        var m = typeof(UILayoutRuntimeProbeEditor).GetMethod("ShotAsync", Stat);
        return m != null ? (UniTask)m.Invoke(null, new object[] { name, settleMs }) : UniTask.CompletedTask;
    }
}
#endif
