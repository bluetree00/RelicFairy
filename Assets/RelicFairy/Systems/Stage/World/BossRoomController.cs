using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using RelicFairy.Monster;
using RelicFairy.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 보스방 입장 감지 + 연출 제어.
///
/// 흐름: 플레이어가 입장 Trigger 통과
///       → 배리어 닫힘 + BossSpawner.Trigger() 호출 (+ lockInputOnEnter면 여기서 입력 차단)
///       → 보스가 플레이어를 감지(OnEntranceRequested 발행)
///       → 플레이어 입력 차단
///       → 카메라 클로즈업 팬(보스 방향) + 홀드 + 복귀
///       → boss.TriggerEntrance() 호출 → Appear 연출 시작
///       → OnCombatReady 발행 → 플레이어 입력 복구
///
/// Trigger Collider(isTrigger=true)가 이 GameObject에 붙어 있어야 한다.
/// BossSpawner.waitForExternalTrigger = true로 설정해두면 이 컨트롤러가 소환 시점을 제어한다.
/// </summary>
public class BossRoomController : MonoBehaviour
{
    // ── Constants ────────────────────────────────────────────────
    private const float LodPopTolerance = 0.1f;   // 바닥 조각의 먼 단계 모양이 LOD0과 윗면 · 자리가 이만큼 넘게 다르면 그 단계를 뺀다(11-11)
    private const float FlatMaxHeight   = 1.2f;   // 바닥 같은 것 — 두께가 이 아래이고 넓이가 FlatMinArea 넘음
    private const float FlatMinArea     = 0.5f;

    [Header("연결 컴포넌트")]
    [Tooltip("보스 소환을 담당하는 BossSpawner. waitForExternalTrigger=true 필수.")]
    [SerializeField] private BossSpawner bossSpawner;

    [Tooltip("입장 직후 활성화할 배리어 오브젝트. null이면 배리어 없음.")]
    [SerializeField] private GameObject barrier;

    [Tooltip("보스 연출 시작 시 활성화할 벽 오브젝트 배열.")]
    [SerializeField] private GameObject[] introWalls;

    [Header("카메라 팬 설정")]
    [Tooltip("카메라가 이동할 목표 지점 (보스 주변 Transform).")]
    [SerializeField] private Transform bossZoneCenter;

    [Tooltip("카메라가 목표로 이동하는 시간 (초).")]
    [SerializeField, Min(0.1f)] private float panDuration = 1.5f;

    [Tooltip("목표 지점에서 카메라가 머무는 시간 (초).")]
    [SerializeField, Min(0.1f)] private float holdDuration = 2.0f;

    [Tooltip("카메라가 플레이어에게 돌아오는 시간 (초).")]
    [SerializeField, Min(0.1f)] private float returnDuration = 1.0f;

    [Tooltip("보스 클로즈업 카메라 오프셋 (bossZoneCenter 기준 월드 좌표).\n" +
             "z 음수 = 보스 앞에서 바라봄. 방 배치에 따라 조정.")]
    [SerializeField] private Vector3 bossCloseUpOffset = new Vector3(0f, 3f, -7f);

    [Tooltip("카메라가 바라볼 시선 대상 (bossZoneCenter 기준 월드 좌표 오프셋).")]
    [SerializeField] private Vector3 bossCloseUpLookOffset = new Vector3(0f, 1.5f, 0f);

    [Tooltip("true 시 카메라 팬을 생략하고 즉시 보스 등장 연출을 시작한다. 연출 내부에서 카메라를 직접 제어하는 경우 사용.")]
    [SerializeField] private bool _skipCameraPan = false;

    [Tooltip("true 시 콜라이더 자동 트리거를 무시하고, 외부(연출 디렉터)의 BeginBossFightExternally 호출로만 전투를 시작한다. 인트로 프롤로그용.")]
    [SerializeField] private bool externalTriggerOnly = false;

    [Tooltip("true(기본): 입구를 통과하는 순간 입력을 잠근다.\n" +
             "false: 입구에서는 배리어만 닫고, 보스가 플레이어를 감지(OnEntranceRequested)할 때 잠근다.\n" +
             "입구가 보스 감지 반경 밖에 있는 넓은 아레나는 false여야 한다 — true면 감지 반경에 못 들어간 채 잠겨 영구 이동 불가.")]
    [SerializeField] private bool lockInputOnEnter = true;

    [Header("보스방 공용 연출 스위치 (끄면 일반 방과 같은 흐름)")]
    [Tooltip("방 진입 안개 베일을 쓰지 않는다 — 조립 디졸브를 가리는 장치인데 커스텀 아레나엔 가릴 조립이 없다.")]
    [SerializeField] private bool suppressEntryFogVeil = false;
    [Tooltip("방 입장 대사(BossRoom_ChN_Enter)를 대사창 대신 걸으면서 듣는 자막으로 띄운다.")]
    [SerializeField] private bool entryDialogueAsBark = false;
    [Tooltip("입구를 지나는 순간부터 방을 나갈 때까지 전투 자동 줌아웃(CombatCameraFraming)을 멈춘다 — 보스가 자기 구도를 쓴다.")]
    [SerializeField] private bool suppressCombatFraming = false;

    // ── Private ──────────────────────────────────────────────────
    private bool             _triggered;
    private bool             _unbeatable;
    private bool             _playerPassing;
    private Transform        _playerTransform;
    private PlayerController _playerController;

    // ── Properties ───────────────────────────────────────────────
    public bool SuppressEntryFogVeil => suppressEntryFogVeil;
    /// <summary>등장 연출이 끝나 조작이 돌아왔는가 — 외부 연출(인트로)이 전투 대사·HUD를 이 뒤로 미룬다.</summary>
    public bool IsCombatReady { get; private set; }
    /// <summary>등장 클로즈업에 띄울 이름 — 비우면 보스 자신의 인카운터 대사(인트로는 「타락한 모르드레드」).</summary>
    public string EntranceTitle { get; set; }
    public bool EntryDialogueAsBark  => entryDialogueAsBark;

    // ── Lifecycle ────────────────────────────────────────────────
    private void Awake() => SteadyFloorLods(transform.parent != null ? transform.parent : transform);

    private void OnDestroy()
    {
        GameCameraController.Instance?.SetCombatFramingSuppressed(this, false);
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Trigger
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private void OnTriggerEnter(Collider other)
    {
        if (externalTriggerOnly || _triggered) return;
        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;
        _playerPassing    = true;
        _playerController = player;
    }

    private void OnTriggerExit(Collider other)
    {
        if (externalTriggerOnly || _triggered || !_playerPassing) return;
        var player = other.GetComponentInParent<PlayerController>();
        if (player == null) return;

        _triggered     = true;
        _playerPassing = false;
        OnPlayerEntered(player);
    }

    private void OnPlayerEntered(PlayerController player)
    {
        _playerTransform  = player.transform;
        _playerController = player;
        if (lockInputOnEnter) { player.SetInputEnabled(false); player.CancelActions(); }   // 하던 공격 · 스킬도 끊는다 — 입력만 끄면 진행 중 동작이 보스를 때려 등장이 깨졌다(11-05 #1)
        if (suppressCombatFraming)
            GameCameraController.Instance?.SetCombatFramingSuppressed(this, true);

        var bossBgmKey = SceneManager.GetActiveScene().name switch
        {
            "GameScene_Ch1" => "Ch1_Boss",
            "GameScene_Ch2" => "Ch2_Boss",
            "GameScene_Ch3" => "Ch3_Boss",
            "GameScene_Ch4" => "Ch3_Boss",   // 10-29 임시 — 리치 전용 곡이 오기 전까지 Ch3 보스 곡
            _               => (string)null,
        };
        if (bossBgmKey != null) Managers.Sound.CrossfadeBgmAsync(bossBgmKey).Forget();

        if (barrier != null)
            barrier.SetActive(true);

        if (introWalls != null)
            foreach (var wall in introWalls)
                if (wall != null) wall.SetActive(true);

        // 이미 스폰된 보스가 있으면 소급 연결, 없으면 이벤트 구독 후 소환
        if (bossSpawner.SpawnedBoss != null)
        {
            OnBossSpawned(bossSpawner.SpawnedBoss);
        }
        else
        {
            bossSpawner.OnMonsterSpawned += OnBossSpawned;
            bossSpawner?.Trigger();
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // 보스 감지 → 입력 차단 → 카메라 팬 → TriggerEntrance → 입력 복구
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>외부 연출(예: IntroMordredDirector)이 상호작용·컷신 뒤 전투를 시작할 때 호출.
    /// 콜라이더 자동 트리거 대신 이 경로로 barrier·소환·등장 연출을 실행한다. unbeatable=true면 보스 HP 바닥(무적).</summary>
    public void BeginBossFightExternally(PlayerController player, bool unbeatable = false)
    {
        if (_triggered || player == null) return;
        _triggered  = true;
        _unbeatable = unbeatable;
        OnPlayerEntered(player);
    }

    private void OnBossSpawned(MonsterBase boss)
    {
        bossSpawner.OnMonsterSpawned -= OnBossSpawned;

        if (_unbeatable && boss != null) boss.HpFloorMin1 = true;

        // 등장 연출(IBossEntrance) 없는 보스(예: ForestGuardian)는 입장 시 차단한 입력을
        // 복구해줄 OnCombatReady 시퀀스가 없다 — 여기서 즉시 복구하지 않으면 플레이어가 영구 이동 불가.
        if (boss is not IBossEntrance entrance)
        {
            _playerController?.SetInputEnabled(true);
            IsCombatReady = true;
            ShowNonEntranceEncounterBark(boss); // 등장 연출 없는 보스도 인카운터 대사는 띄움
            return;
        }

        // 전투 준비 완료 시 플레이어 입력 복구 (단발)
        Action combatReadyHandler = null;
        combatReadyHandler = () =>
        {
            entrance.OnCombatReady -= combatReadyHandler;
            if (this == null) return;
            _playerController?.SetInputEnabled(true);
            IsCombatReady = true;
        };
        entrance.OnCombatReady += combatReadyHandler;

        // 보스 감지 시 입력 차단 + 카메라 팬 (단발)
        Action entranceHandler = null;
        entranceHandler = () =>
        {
            entrance.OnEntranceRequested -= entranceHandler;
            if (this == null) return;
            _playerController?.SetInputEnabled(false);
            _playerController?.CancelActions();   // 하던 공격 · 스킬도 끊는다(11-05 #1)
            DoCameraAndTriggerAsync(entrance, destroyCancellationToken).Forget();
        };
        entrance.OnEntranceRequested += entranceHandler;
    }

    /// <summary>등장 연출(IBossEntrance) 없는 보스의 인카운터 대사 — 타입명에서 키 유도(예: ForestGuardianMonster → ForestGuardian_Encounter).
    /// 방문 변형(GetVisitLines) 적용. 대사 미작성/미로드 시 조용히 스킵(대사 없어도 정상 진행).</summary>
    private static void ShowNonEntranceEncounterBark(MonsterBase boss)
    {
        if (boss == null || UI_BossBark.Instance == null) return;

        string typeName = boss.GetType().Name;
        const string suffix = "Monster";
        if (typeName.EndsWith(suffix)) typeName = typeName.Substring(0, typeName.Length - suffix.Length);

        var lines = Managers.DialogueData?.GetBossEncounterLines($"{typeName}_Encounter");
        if (lines != null && lines.Length > 0)
            UI_BossBark.Show(lines[0].text, BossBarkType.BossIntro);
    }

    private async UniTaskVoid DoCameraAndTriggerAsync(IBossEntrance entrance, CancellationToken ct)
    {
        var cam = GameCameraController.Instance;

        // 카메라 팬 생략 모드: 즉시 수동 제어 전환 후 연출 시작
        if (_skipCameraPan)
        {
            cam?.TakeManualControl();
            entrance.TriggerEntrance();
            return;
        }

        if (cam != null && bossZoneCenter != null)
        {
            try
            {
                // onPanComplete: 줌인 도달 직후 Appear 연출 시작 → 카메라가 줌된 상태에서 보스 등장이 보임
                await cam.PanToZoneAndReturnAsync(
                    bossZoneCenter.position,
                    panDuration,
                    // 홀드는 사실상 무한 — 등장 연출(DormantState)이 자기 팬을 걸면
                    // GameCameraController가 이 팬을 취소하고 이어받는다. holdDuration으로
                    // 짧게 잡으면 연출 도중 카메라가 플레이어로 튕겨 돌아온다.
                    3600f,
                    returnDuration,
                    _playerTransform,
                    ct,
                    bossCloseUpOffset,
                    onPanComplete: entrance.TriggerEntrance,
                    customLookOffset: bossCloseUpLookOffset);
            }
            catch (OperationCanceledException)
            {
                _playerController?.SetInputEnabled(true);
                return;
            }
        }
        else
        {
            try { await UniTask.Delay(TimeSpan.FromSeconds(panDuration), cancellationToken: ct); }
            catch (OperationCanceledException)
            {
                _playerController?.SetInputEnabled(true);
                return;
            }
            entrance.TriggerEntrance();
            try { await UniTask.Delay(TimeSpan.FromSeconds(holdDuration), cancellationToken: ct); }
            catch (OperationCanceledException) { }
        }
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // 바닥 LOD
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    /// <summary>
    /// 바닥 조각의 먼 LOD 단계가 가까운 단계와 모양이 크게 다르면(윗면 · 자리 0.1 m 넘게) 그 단계를 빼고 앞 단계가 그 거리까지 맡게 한다 —
    /// 카메라가 움직일 때마다 바닥이 솟았다 꺼졌다 했다(11-11 사용자 「Ch3 보스방 바닥 타일이 튀어나오거나 들어간다」: 단 둘레 SM_TrimConcrete의 L5 · L6이
    /// 윗면 0.10~0.23 m 높고 0.12~0.31 m 비껴 있었다 — 자리(TRS)는 정상, 팩의 단순화 메시 자체 차이). 꺼지는 거리(맨 끝 전환 높이)는 그대로.
    /// </summary>
    private static void SteadyFloorLods(Transform root)
    {
        int fixedGroups = 0;
        foreach (var g in root.GetComponentsInChildren<LODGroup>())
        {
            var lods = g.GetLODs();
            if (lods.Length < 2 || !TryBounds(lods[0], out Bounds b0)) continue;
            if (b0.size.y > FlatMaxHeight || b0.size.x * b0.size.z < FlatMinArea) continue;

            var keep = new System.Collections.Generic.List<LOD>(lods.Length) { lods[0] };
            for (int i = 1; i < lods.Length; i++)
            {
                bool pops = TryBounds(lods[i], out Bounds bi)
                            && (Mathf.Abs(bi.max.y - b0.max.y) > LodPopTolerance
                                || new Vector2(bi.center.x - b0.center.x, bi.center.z - b0.center.z).magnitude > LodPopTolerance);
                if (!pops) { keep.Add(lods[i]); continue; }
                var prev = keep[keep.Count - 1];
                prev.screenRelativeTransitionHeight = lods[i].screenRelativeTransitionHeight;   // 앞 단계가 이 단계의 거리까지
                keep[keep.Count - 1] = prev;
                foreach (var r in lods[i].renderers)
                    if (r != null && System.Array.IndexOf(prev.renderers, r) < 0) r.enabled = false;   // LODGroup 밖이 되면 늘 그려진다
            }
            if (keep.Count == lods.Length) continue;
            g.SetLODs(keep.ToArray());
            fixedGroups++;
        }
        if (fixedGroups > 0) Debug.Log($"[BossRoom] 먼 단계 모양이 어긋난 바닥 {fixedGroups}개 — 앞 단계가 맡게 함({root.name})");
    }

    private static bool TryBounds(LOD lod, out Bounds b)
    {
        b = default;
        bool any = false;
        if (lod.renderers == null) return false;
        foreach (var r in lod.renderers)
        {
            if (r == null) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any;
    }

    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
    // Gizmos
    // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

    private void OnDrawGizmosSelected()
    {
        if (bossZoneCenter != null)
        {
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.5f);
            Gizmos.DrawLine(transform.position, bossZoneCenter.position);
            Gizmos.DrawWireSphere(bossZoneCenter.position, 2f);

            // 클로즈업 카메라 위치 미리보기
            Gizmos.color = Color.cyan;
            Vector3 camPreview = bossZoneCenter.position + bossCloseUpOffset;
            Gizmos.DrawLine(bossZoneCenter.position, camPreview);
            Gizmos.DrawWireSphere(camPreview, 0.5f);
        }
    }
}
