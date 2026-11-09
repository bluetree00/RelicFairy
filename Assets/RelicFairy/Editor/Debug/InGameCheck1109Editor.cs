#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 11-09 출시 전 실제 게임 확인(사용자 「실제 인게임도 다 체크」) — 「런 구조 자동 실측」이 Ch1→Ch4를 끝까지 도는 동안
/// 방에 들어갈 때마다(로그 「[RunAuto] 방 진입 X」) 3.5초 뒤 게임 화면(UI 포함)을 Temp/ingame_1109/에 찍고,
/// 그동안 나온 에러 · 예외 · 경고 중 로드 실패(Addressables · 셰이더 · 누락)를 모은다. 「[RunAuto] 종료」에서 Temp/ingame_1109.txt를 쓴다.
/// 메뉴: 켜기(플레이 중 · 자동 실측 시작 전에) · 끄기·기록.
/// </summary>
public static class InGameCheck1109Editor
{
    private const string Root = "RelicFairy/Debug/11-09 인게임 확인/";
    private const string Dir  = "Temp/ingame_1109";

    private static bool s_on;
    private static int s_shot, s_err, s_exc, s_loadWarn;
    private static readonly List<string> s_rooms = new();
    private static readonly Dictionary<string, int> s_errs = new();

    [MenuItem(Root + "켜기 (플레이 중 · 자동 실측 시작 전에)")]
    private static void On()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[InGame1109] 플레이 모드에서"); return; }
        if (s_on) return;
        s_on = true; s_shot = s_err = s_exc = s_loadWarn = 0; s_rooms.Clear(); s_errs.Clear();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        Directory.CreateDirectory(Dir);
        Application.logMessageReceived += OnLog;
        EditorApplication.playModeStateChanged += OnPlayMode;
        Debug.Log("[InGame1109] 켜짐");
    }

    [MenuItem(Root + "끄기·기록")]
    private static void Off() => Finish("수동");

    private static void OnPlayMode(PlayModeStateChange s) { if (s == PlayModeStateChange.ExitingPlayMode) Finish("플레이 끝"); }

    private static void OnLog(string msg, string stack, LogType type)
    {
        if (!s_on || msg.StartsWith("[InGame1109]")) return;
        int at = msg.StartsWith("[RunAuto] t=") ? msg.IndexOf(" 방 진입 ", System.StringComparison.Ordinal) : -1;   // 「[RunAuto] t=12.3 방 진입 X」
        if (at > 0)
        {
            string room = msg.Substring(at + " 방 진입 ".Length).Trim();
            s_rooms.Add(room);
            ShotLater(s_rooms.Count, room).Forget();
            return;
        }
        if (msg.StartsWith("[RunAuto] 종료")) { ShotLater(s_rooms.Count + 1, "end").Forget(); FinishLater(msg).Forget(); return; }
        if (msg.Contains("MCP-FOR-UNITY")) return;   // 에디터 연결 잡음
        bool load = msg.Contains("InvalidKey") || msg.Contains("Unable to load") || msg.Contains("Failed to load") || msg.Contains("RemoteProvider")
                 || msg.Contains("Shader") && msg.Contains("not") || msg.Contains("missing") || msg.Contains("Missing");
        if (type == LogType.Exception) s_exc++;
        else if (type == LogType.Error || type == LogType.Assert) s_err++;
        else if (type == LogType.Warning && load) s_loadWarn++;
        else return;
        string key = $"[{type}] " + (msg.Length > 160 ? msg.Substring(0, 160) : msg).Replace('\n', ' ');
        s_errs[key] = s_errs.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    private static async UniTaskVoid ShotLater(int idx, string room)
    {
        await UniTask.Delay(3500, DelayType.Realtime);
        if (!s_on || !Application.isPlaying) return;
        string safe = room.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(' ', '_');
        ScreenCapture.CaptureScreenshot(Path.Combine(Dir, $"{idx:00}_{safe}.png"));
        s_shot++;
    }

    private static async UniTaskVoid FinishLater(string endMsg)
    {
        await UniTask.Delay(5000, DelayType.Realtime);
        Finish(endMsg);
    }

    private static void Finish(string why)
    {
        if (!s_on) return;
        s_on = false;
        Application.logMessageReceived -= OnLog;
        EditorApplication.playModeStateChanged -= OnPlayMode;
        var sb = new StringBuilder($"11-09 인게임 확인 — 끝: {why}\n방 {s_rooms.Count} · 사진 {s_shot} · 에러 {s_err} · 예외 {s_exc} · 로드/누락 경고 {s_loadWarn}\n");
        for (int i = 0; i < s_rooms.Count; i++) sb.AppendLine($"  {i + 1:00} {s_rooms[i]}");
        sb.AppendLine("에러 · 예외 · 경고(같은 줄 횟수):");
        foreach (var kv in s_errs) sb.AppendLine($"  ×{kv.Value} {kv.Key}");
        File.WriteAllText("Temp/ingame_1109.txt", sb.ToString());
        Debug.Log("[InGame1109] 끝\n" + sb.ToString(0, Mathf.Min(sb.Length, 3000)));
    }
}
#endif
