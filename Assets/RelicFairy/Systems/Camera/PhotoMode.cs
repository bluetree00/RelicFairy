using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 촬영 모드(11-06 사용자 「상점 페이지 이미지 — 스크린샷을 좋은 느낌으로」) — 에디터 · 개발 빌드(<see cref="SteamManager.IsDevBuild"/>)에서만 켜진다.
///  F12 = 4K PNG 저장(바탕화면 RelicFairy_스크린샷) · F11 = 시간 멈춤/풀기 · F10 = 화면 UI 숨기기/보이기 · F9 = 몬스터 이름표 · 체력 줄 숨기기/보이기 · F8 = 피해 숫자 끄기/켜기 ·
///  [ ] = 카메라 가까이/멀리 · , . = 카메라 왼쪽/오른쪽으로 돌리기. 같은 일을 에디터 메뉴 「RelicFairy/Photo」에서도 한다.
/// 화면 UI = 월드 공간이 아닌 Canvas(HUD · 창). 몬스터 이름표 · 데미지 숫자 같은 월드 UI는 남긴다. 거리 배율은 저장하지 않는다.
/// </summary>
public sealed class PhotoMode : MonoBehaviour
{
    // ── Constants ─────────────────────────────────────────────
    private const int   TargetHeight = 2160;
    private const float ZoomStep = 0.1f, MinZoom = 0.5f, MaxZoom = 1.6f;
    private const float TurnStep = 10f;

    // ── Static ────────────────────────────────────────────────
    private static PhotoMode s_instance;

    // ── Private ───────────────────────────────────────────────
    private readonly List<Behaviour> _hidden = new();
    private readonly List<Canvas> _hiddenBars = new();
    private bool  _frozen;
    private bool  _hideBars;
    private float _savedScale = 1f;
    private float _nextBarScan;

    // ── Properties ────────────────────────────────────────────
    /// <summary>카메라 거리 배율(촬영 전용 · 1 = 그대로) — <see cref="CameraUserFraming"/>가 곱한다.</summary>
    public static float Zoom { get; private set; } = 1f;

    // ── Lifecycle ─────────────────────────────────────────────
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { s_instance = null; Zoom = 1f; DamagePopupSpawner.Suppressed = false; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (s_instance != null || !SteamManager.IsDevBuild) return;
        var go = new GameObject("[PhotoMode]");
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<PhotoMode>();
    }

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.f12Key.wasPressedThisFrame) Capture();
        if (kb.f11Key.wasPressedThisFrame) ToggleFreeze();
        if (kb.f10Key.wasPressedThisFrame) ToggleUi();
        if (kb.f9Key.wasPressedThisFrame)  ToggleMonsterBars();
        if (kb.f8Key.wasPressedThisFrame)  ToggleDamageNumbers();
        if (_hideBars && Time.unscaledTime >= _nextBarScan) HideNewBars();
        if (kb.leftBracketKey.wasPressedThisFrame)  AddZoom(-ZoomStep);
        if (kb.rightBracketKey.wasPressedThisFrame) AddZoom(ZoomStep);
        if (kb.commaKey.wasPressedThisFrame)  Turn(-TurnStep);
        if (kb.periodKey.wasPressedThisFrame) Turn(TurnStep);
    }

    // ── Public Methods ────────────────────────────────────────
    /// <summary>지금 화면을 세로 2160 이상으로 저장 — 저장 경로(파일은 프레임 끝에 쓰인다).</summary>
    public static string Capture()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RelicFairy_스크린샷");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"RF_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        int super = Mathf.Max(1, Mathf.CeilToInt(TargetHeight / (float)Mathf.Max(1, Screen.height)));
#if UNITY_EDITOR
        if (s_instance != null) s_instance.HideDevOverlayAsync().Forget();   // 「테스트 ▾」는 어느 사진에도 안 들어가야 한다
#endif
        ScreenCapture.CaptureScreenshot(path, super);
        Debug.Log($"[PhotoMode] 저장 — {path} ({Screen.width * super}×{Screen.height * super})");
        return path;
    }

    public static void ToggleUi()     { if (s_instance != null) s_instance.SetUiHidden(s_instance._hidden.Count == 0); }

    /// <summary>새 피해 숫자 끄기/켜기(이미 떠 있는 건 사라질 때까지 둔다).</summary>
    public static void ToggleDamageNumbers()
    {
        DamagePopupSpawner.Suppressed = !DamagePopupSpawner.Suppressed;
        Debug.Log($"[PhotoMode] 피해 숫자 {(DamagePopupSpawner.Suppressed ? "끔" : "켬")}");
    }

    /// <summary>몬스터 이름표 · 체력 줄(월드 공간) 숨기기/보이기 — 가까이 찍으면 이름표가 화면 앞을 가린다. 켜 둔 동안 새로 나온 몬스터도 숨긴다.</summary>
    public static void ToggleMonsterBars()
    {
        if (s_instance == null) return;
        s_instance._hideBars = !s_instance._hideBars;
        if (s_instance._hideBars) { s_instance.HideNewBars(); return; }
        foreach (var c in s_instance._hiddenBars) if (c != null) c.enabled = true;
        s_instance._hiddenBars.Clear();
    }
    public static void ToggleFreeze() { if (s_instance != null) s_instance.SetFrozen(!s_instance._frozen); }
    public static void AddZoom(float delta) => Zoom = Mathf.Clamp(Zoom + delta, MinZoom, MaxZoom);

    public static void Turn(float deg)
    {
        var cam = GameCameraController.Instance;
        if (cam != null) cam.SetHeadingRaw(cam.CurrentHeading + deg);
    }

    // ── Private Methods ───────────────────────────────────────
    private void HideNewBars()
    {
        _nextBarScan = Time.unscaledTime + 0.25f;
        foreach (var bar in FindObjectsByType<MonsterHPBar>(FindObjectsSortMode.None))
            foreach (var c in bar.GetComponentsInChildren<Canvas>(true))
                if (c.enabled) { c.enabled = false; _hiddenBars.Add(c); }
    }

    private void SetUiHidden(bool hide)
    {
        if (!hide)
        {
            foreach (var b in _hidden) if (b != null) b.enabled = true;
            _hidden.Clear();
            return;
        }
        foreach (var c in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            if (c.enabled && c.rootCanvas.renderMode != RenderMode.WorldSpace) { c.enabled = false; _hidden.Add(c); }
#if UNITY_EDITOR
        foreach (var o in FindObjectsByType<TestRunOverlay>(FindObjectsSortMode.None))
            if (o.enabled) { o.enabled = false; _hidden.Add(o); }
#endif
        Debug.Log($"[PhotoMode] 화면 UI 숨김 {_hidden.Count}개");
    }

#if UNITY_EDITOR
    /// <summary>저장하는 프레임 동안만 에디터 테스트 표시(IMGUI)를 끈다 — 화면 UI는 그대로.</summary>
    private async UniTaskVoid HideDevOverlayAsync()
    {
        var off = new List<TestRunOverlay>();
        foreach (var o in FindObjectsByType<TestRunOverlay>(FindObjectsSortMode.None))
            if (o.enabled) { o.enabled = false; off.Add(o); }
        try { await UniTask.DelayFrame(3, PlayerLoopTiming.Update, this.GetCancellationTokenOnDestroy()); }
        catch (OperationCanceledException) { return; }
        foreach (var o in off) if (o != null && !_hidden.Contains(o)) o.enabled = true;
    }
#endif

    private void SetFrozen(bool on)
    {
        if (on == _frozen) return;
        _frozen = on;
        if (on) { _savedScale = Time.timeScale; Time.timeScale = 0f; }
        else Time.timeScale = _savedScale;
    }
}
