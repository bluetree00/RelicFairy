#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Inputs;
using RelicFairy.Monster;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// [촬영 · 런 중] 상점 스크린샷용 전투 장면(11-06 사용자 「카타나나 대검에 증강을 켜서 몬스터를 잡는 모습 · 보스 광역 · 패턴」).
/// T3 무기 + 눈에 보이는 증강(III) + 속성 구역 시너지를 입히고 무적으로, 가장 가까운 적을 겨눠 평타 · E · R을 계속 넣는다(장착 · 입력은
/// <see cref="LevelSimBuildProbeEditor"/>와 같은 방식 — InputBuffer에 명령, 조준은 가상 마우스). 제자리에 묶지 않는다 — 몹이 다가오고 보스는 패턴을 쓴다.
/// 사진은 <see cref="PhotoMode"/>(F12 · 메뉴 RelicFairy/Photo)로 따로 찍는다. 로그 「[Showcase]」.
/// </summary>
public static class ShowcaseFightEditor
{
    private const string Root = "RelicFairy/Photo/전투 장면/";
    private const float  Seconds = 120f;
    private const float  SearchRadius = 16f;
    private const float  PinRadius = 3f;
    private const int    PackHp = 4000;
    private const string ProbeMouseName = "ShowcaseProbeMouse";
    private const BindingFlags SAny = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private sealed class Build
    {
        public string Label, SoPath, Augments, Zone;
        public bool  UseE = true;          // false = R(속성 검) 켜고 평타만 — 대검 「속성 검을 무리에 휘두르는」 장면
        public float GustChance = -1f;     // 0 이상이면 촬영 동안만 검풍 확률(%) — 카타나 「분신 여럿 · 검풍」 장면
    }

    private static readonly Build Katana = new()
    {
        Label = "카타나", SoPath = "Assets/RelicFairy/Weapon/Katana/Data/T3_Katana.asset", Zone = "ELECTRIC",
        Augments = "aug_katana_shadow:3,aug_katana_gust:3,aug_katana_phantom:3,aug_legend_issen:1,aug_slaughter_chain:3,aug_element_edge:3,aug_thunder:3",
    };

    private static readonly Build Greatsword = new()
    {
        Label = "대검", SoPath = "Assets/RelicFairy/Weapon/Greatsword/Data/T3_Greatsword.asset", Zone = "FIRE",
        Augments = "aug_legend_rift:1,aug_great_reach:3,aug_great_final:3,aug_great_split:3,aug_great_perimeter:3,aug_slaughter_chain:3,aug_impact:3",
    };

    private static CancellationTokenSource s_cts;
    private static Mouse s_probeMouse, s_realMouse;

    [MenuItem(Root + "카타나 증강 (런 중)")]  private static void RunKatana()     => Begin(Katana);
    [MenuItem(Root + "대검 증강 (런 중)")]    private static void RunGreatsword() => Begin(Greatsword);
    [MenuItem(Root + "카타나 분신 · 검풍 (런 중)")]
    private static void RunKatanaGust() => Begin(new Build { Label = "카타나 분신 · 검풍", SoPath = Katana.SoPath, Augments = Katana.Augments, Zone = Katana.Zone, GustChance = 70f });
    // 그림자 파트너가 있으면 플레이어 검풍은 붉은 분신 없이 칼끝에서 나간다(겹침 방지) — 붉은 분신 여럿을 보이려면 그림자 없이
    [MenuItem(Root + "카타나 붉은 분신 검풍 — 그림자 없이 (런 중)")]
    private static void RunKatanaRedClones() => Begin(new Build { Label = "카타나 붉은 분신", SoPath = Katana.SoPath, Zone = Katana.Zone, GustChance = 100f,
        Augments = "aug_katana_gust:3,aug_katana_phantom:3,aug_legend_issen:1,aug_slaughter_chain:3,aug_thunder:3" });
    [MenuItem(Root + "대검 속성 검 — 평타만 (런 중)")]
    private static void RunGreatswordElement() => Begin(new Build { Label = "대검 속성 검", SoPath = Greatsword.SoPath, Augments = Greatsword.Augments, Zone = Greatsword.Zone, UseE = false });
    [MenuItem(Root + "멈춤")]                 private static void Stop()          { s_cts?.Cancel(); ReleaseMouse(); Debug.Log("[Showcase] 멈춤"); }

    // 시작방 · 빈 방에서도 찍을 수 있게 — 플레이어 앞쪽 반원(4~6 m)에 무리를 부른다(키 = 실측 도구들이 쓰는 몬스터 풀 주소)
    private static readonly string[] PackKeys = { "Werewolf/Werewolf", "Orc/Orc", "Skeleton/Skeleton", "Werewolf/Werewolf", "Orc/Orc", "Skeleton/Skeleton", "EvilMage/EvilMage" };

    [MenuItem(Root + "몬스터 무리 부르기 (런 중)")]
    private static void SpawnPack() => SpawnPackMenu(false);

    [MenuItem(Root + "몬스터 큰 무리 부르기 — 둘레 10 (런 중 · 넓은 방)")]
    private static void SpawnRing() => SpawnPackMenu(true);

    private static void SpawnPackMenu(bool ring)
    {
        var p = GameRunBootstrapper.Instance?.Run?.Player;
        if (!Application.isPlaying || p == null) { Debug.LogWarning("[Showcase] 런 중에"); return; }
        SpawnPackAsync(p.transform, ring).Forget();
    }

    private static async UniTaskVoid SpawnPackAsync(Transform player, bool ring)
    {
        Vector3 fw = player.forward; fw.y = 0f; fw = fw.sqrMagnitude > 1e-4f ? fw.normalized : Vector3.forward;
        var pack = new List<MonsterBase>();
        int count = ring ? 10 : PackKeys.Length;
        for (int i = 0; i < count; i++)
        {
            // 둘레 = 감싸 휘두름마다 여럿이 맞게 · 아니면 앞쪽 좁게(다리 · 좁은 방에서 바닥 밖에 서지 않게)
            float a = ring ? Mathf.Lerp(-120f, 120f, i / (float)(count - 1)) : Mathf.Lerp(-35f, 35f, i / (float)(count - 1));   // 둘레 = 앞 · 옆 240°(뒤는 카메라 앞이라 비운다)
            float d = ring ? (i % 2 == 0 ? 2.8f : 4f) : (i % 2 == 0 ? 3.5f : 5f);
            Vector3 at = player.position + Quaternion.Euler(0f, a, 0f) * fw * d;
            Vector3 face = player.position - at; face.y = 0f;
            var m = await Managers.ObjectPooler.SpawnAsync<MonsterBase>(PackKeys[i % PackKeys.Length], ObjectPoolerManager.PoolType.Monster, at,
                                                                        Quaternion.LookRotation(face.sqrMagnitude > 1e-4f ? face.normalized : -fw));
            if (m != null) pack.Add(m);
        }
        // T3 +9면 1챕터 몹이 사진 한 장 전에 다 죽는다 — 몇 초 버티게(등장 무적이 끝나 초기화가 체력을 다 쓴 뒤에 넣는다)
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < 4f && pack.Exists(m => m != null && m.IsDamageImmuneNow)) await UniTask.Yield();
        foreach (var m in pack) if (m != null && !m.IsDead) SetHp(m, PackHp);
        Debug.Log($"[Showcase] 몬스터 무리 {pack.Count}/{count} · 체력 {PackHp}");
    }

    // 룬판 사진 — 속성 셋을 맞붙여 채워 시너지가 켜진 판(「룬판 빼곡히」 실측 도구의 채우기를 그대로 부른다 · 6속성 다 섞으면 어지럽다)
    // 언어를 바꾼 뒤 판 글(단계 칸 · 반응 줄)을 새 언어로 다시 짓게 — 열린 채 바꾸면 앞 언어가 남는다(게임에선 설정 화면에서 바꿔 판이 닫혀 있다)
    [MenuItem(Root + "룬판 다시 열기 (런 중)")]
    private static void ReopenBoard() => ReopenBoardAsync().Forget();

    private static async UniTaskVoid ReopenBoardAsync()
    {
        var panel = UI_GridPanel.Instance;
        if (!Application.isPlaying || panel == null) return;
        Managers.UI?.CloseAllPopupUI();
        panel.gameObject.SetActive(false);
        await UniTask.Delay(300, ignoreTimeScale: true);
        panel.gameObject.SetActive(true);
        panel.ShowWithNewItem(null);
        await UniTask.Delay(800, ignoreTimeScale: true);
        typeof(UI_GridPanel).GetMethod("RefreshInfoPanelDefault", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(panel, null);
        Debug.Log("[Showcase] 룬판 다시 열기");
    }

    [MenuItem(Root + "룬판 채우기 — 불 · 번개 · 빛 (런 중)")]
    private static void FillBoard() => FillBoardAsync().Forget();

    private static async UniTaskVoid FillBoardAsync()
    {
        var run = GameRunBootstrapper.Instance?.Run;
        if (!Application.isPlaying || run == null) { Debug.LogWarning("[Showcase] 런 중에"); return; }
        if (UI_GridPanel.Instance == null) Managers.UI?.ShowOverlayUI<UI_GridPanel>();
        var panel = UI_GridPanel.Instance;
        if (panel == null) { Debug.LogWarning("[Showcase] 룬판을 못 열었다"); return; }
        panel.ShowWithNewItem(null);
        await UniTask.Delay(1200, ignoreTimeScale: true);
        var fill = typeof(RuneDense1030ProbeEditor).GetMethod("FillElementAsync", BindingFlags.Static | BindingFlags.NonPublic);
        var added = new List<RuntimeItemData>();
        var placed = new List<RuntimeItemData>();
        foreach (var (el, n) in new[] { ("FIRE", 6), ("ELECTRIC", 5), ("LIGHT", 4) })
            if (fill != null) await (UniTask)fill.Invoke(null, new object[] { panel, run.ItemInventory, el, added, placed, n });
        typeof(UI_GridPanel).GetMethod("RefreshInfoPanelDefault", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(panel, null);
        Debug.Log($"[Showcase] 룬판 채움 — {placed.Count}개 · 판 {RuneBlockBudget.Used}/{RuneBlockBudget.Max}");
    }

    private static void SetHp(MonsterBase m, int hp)
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var rt = typeof(MonsterBase).GetField("_runtime", Inst)?.GetValue(m);
        var f  = rt?.GetType().GetField("CurrentHp", Inst);
        if (f != null) f.SetValue(rt, Convert.ChangeType(hp, f.FieldType));
    }

    private static void Begin(Build b)
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Showcase] 플레이 중에"); return; }
        var run = GameRunBootstrapper.Instance?.Run;
        var p = run?.Player;
        if (p == null) { Debug.LogWarning("[Showcase] 런 플레이어가 없다 — 테스트 허브에서 런을 시작한 뒤"); return; }
        s_cts?.Cancel();
        s_cts = new CancellationTokenSource();
        s_buffed.Clear();
        RunAsync(p, run, b, s_cts.Token).Forget();
    }

    private static async UniTaskVoid RunAsync(PlayerController p, GameRunSession run, Build b, CancellationToken ct)
    {
        try
        {
            p.SetInvincible(3600f);
            Managers.UI?.CloseAllPopupUI();
            if (!await EquipAsync(p, b.SoPath, ct)) { Debug.LogWarning($"[Showcase] {b.Label} 장착 실패"); return; }
            var w = p.WeaponManager.Weapon0Data;
            typeof(PlayerWeaponManager).GetMethod("ApplyServerOverrideIfAvailable", SAny)
                ?.Invoke(null, new object[] { w, Path.GetFileNameWithoutExtension(b.SoPath) });
            w.evolutionStage = 2;
            w.enhanceLevel = 9;
            w.highestEnhanceReached = 9;
            w.formTier = 3;
            w.augments = b.Augments;
            w.RecomputeEnhancedStats();
            WeaponFormRuntime.Of(p).Rebuild();
            p.WeaponManager.RaiseEquippedWeaponRefreshed();
            run.EffectManager.Rebuild();
            MerlinRuneBridge.Instance?.OnZoneCellsUpdated(new Dictionary<string, int> { [b.Zone] = 8 });
            if (b.GustChance >= 0f) SwordGustAugment.ProbeChance = b.GustChance;
            Debug.Log($"[Showcase] {b.Label} 장착 — +9 · 형 III · 증강 {b.Augments} · 구역 {b.Zone} 8칸" + (b.GustChance >= 0f ? $" · 검풍 {b.GustChance:0}%" : "") + (b.UseE ? "" : " · E 안 씀"));

            TakeMouse();
            Vector3 home = p.transform.position;   // 돌진이 다리 · 낭떠러지 밖으로 데려가면 떨어져 런이 끝난다(11-06 4챕터 대기방) — 스킬이 끝나면 되돌린다
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < Seconds)
            {
                ct.ThrowIfCancellationRequested();
                if (!p.IsInSkillAction && (p.transform.position - home).sqrMagnitude > PinRadius * PinRadius)
                {
                    if (p.TryGetComponent<Rigidbody>(out var rb)) { rb.position = home; rb.linearVelocity = Vector3.zero; }
                    p.transform.position = home;
                }
                BuffNewMonsters(p.transform.position);
                var target = Nearest(p.transform.position);
                if (target != null && !p.IsInSkillAction)
                {
                    Aim(p, target.transform.position);
                    if (b.UseE && p.CooldownTracker.IsReady(SkillType.E)) p.InputBuffer.Push(Command.ESkill);
                    else if (p.CooldownTracker.IsReady(SkillType.R)) p.InputBuffer.Push(Command.RSkill);
                    else p.InputBuffer.Push(Command.Light);
                }
                await UniTask.Delay(TimeSpan.FromSeconds(0.1f), DelayType.Realtime, cancellationToken: ct);
            }
            Debug.Log("[Showcase] 끝");
        }
        catch (OperationCanceledException) { }
        finally { ReleaseMouse(); SwordGustAugment.ProbeChance = -1f; }
    }

    // 실제 방 웨이브 몹도 몇 초 버티게 — 처음 보이는(등장 무적이 끝난) 몹마다 한 번, 보스처럼 원래 더 센 몹은 그대로
    private static readonly HashSet<int> s_buffed = new();

    private static void BuffNewMonsters(Vector3 from)
    {
        foreach (var m in UnityEngine.Object.FindObjectsByType<MonsterBase>(FindObjectsSortMode.None))
        {
            if (m == null || m.IsDead || m.IsDamageImmuneNow || !s_buffed.Add(m.GetInstanceID())) continue;
            if (m.CurrentHp < PackHp && (m.transform.position - from).sqrMagnitude < SearchRadius * SearchRadius * 4f) SetHp(m, PackHp);
        }
    }

    private static MonsterBase Nearest(Vector3 from)
    {
        MonsterBase best = null;
        float bestSq = SearchRadius * SearchRadius;
        foreach (var m in UnityEngine.Object.FindObjectsByType<MonsterBase>(FindObjectsSortMode.None))
        {
            if (m == null || m.IsDead || !m.isActiveAndEnabled) continue;
            float sq = (m.transform.position - from).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = m; }
        }
        return best;
    }

    private static void Aim(PlayerController p, Vector3 at)
    {
        var cam = Camera.main;
        if (s_probeMouse != null && cam != null)
        {
            Vector3 screen = cam.WorldToScreenPoint(at + Vector3.up * 0.8f);
            if (screen.z > 0f) InputSystem.QueueDeltaStateEvent(s_probeMouse.position, (Vector2)screen);
        }
        Vector3 d = at - p.transform.position;
        d.y = 0f;
        if (d.sqrMagnitude > 1e-4f) p.RequestFacing(Quaternion.LookRotation(d.normalized, Vector3.up));
    }

    private static async UniTask<bool> EquipAsync(PlayerController p, string soPath, CancellationToken ct)
    {
        var wm = p.WeaponManager;
        string key = Path.GetFileNameWithoutExtension(soPath);
        var so = AssetDatabase.LoadAssetAtPath<WeaponSO>(soPath);
        if (so == null) return false;
        var runtime = WeaponData.FromSO(so);
        var preload = typeof(PlayerWeaponManager).GetMethod("PreloadWeaponClipsAsync", SAny);
        if (preload != null) await (UniTask)preload.Invoke(null, new object[] { runtime, ct });
        typeof(PlayerWeaponManager).GetMethod("DestroySlotInstance", SAny)?.Invoke(null, new object[] { wm.slots[PlayerWeaponManager.Slot0] });
        await UniTask.Yield(PlayerLoopTiming.Update, ct);
        await wm.AcquireWeaponToSlotAsync(runtime, PlayerWeaponManager.Slot0, setActive: true, playAppear: false);
        if (wm.CurrentSlotIndex != PlayerWeaponManager.Slot0) await wm.SwitchToSlotAsync(PlayerWeaponManager.Slot0);
        float t0 = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - t0 < 8f)
        {
            var inst = wm.CurrentWeaponInstance;
            if (inst != null && inst.name.StartsWith(key) && inst.activeInHierarchy) return true;
            await UniTask.Yield(PlayerLoopTiming.Update, ct);
        }
        return false;
    }

    // 진짜 커서가 게임 창 위에 있으면 넣은 조준을 덮는다 — 도는 동안 가상 마우스로(장비 형 실측과 같은 이유).
    private static void TakeMouse()
    {
        if (s_probeMouse != null) return;
        s_realMouse = Mouse.current;
        s_probeMouse = InputSystem.AddDevice<Mouse>(ProbeMouseName);
        if (s_realMouse != null) InputSystem.DisableDevice(s_realMouse);
        s_probeMouse.MakeCurrent();
        EditorApplication.playModeStateChanged += ReleaseOnExit;
    }

    private static void ReleaseMouse()
    {
        EditorApplication.playModeStateChanged -= ReleaseOnExit;
        if (s_probeMouse != null) { InputSystem.RemoveDevice(s_probeMouse); s_probeMouse = null; }
        if (s_realMouse != null) { InputSystem.EnableDevice(s_realMouse); s_realMouse.MakeCurrent(); s_realMouse = null; }
    }

    private static void ReleaseOnExit(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.ExitingPlayMode) ReleaseMouse();
    }

    [InitializeOnLoadMethod]
    private static void RestoreMiceAfterReload()
    {
        foreach (var d in InputSystem.devices.ToArray())
        {
            if (d is not Mouse) continue;
            if (d.name == ProbeMouseName) InputSystem.RemoveDevice(d);
            else if (!d.enabled) InputSystem.EnableDevice(d);
        }
    }
}
#endif
