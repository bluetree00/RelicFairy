#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using RelicFairy.Monster;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 11-11 ③ 「Ch3 기사 보스 장벽 때문에 처치 후 보스 자리 골드를 못 줍는다」 — Ch3 보스방(테스트 허브)에서 전투를 열고 기사를 쓰러뜨려,
/// 쓰러진 자리 · 금화 자리 · 유리 장벽 상태 · 바닥 높이 지도(1 m 칸)를 재고, 플레이어 쪽 9 m에서 플레이어 걸음(속도 + 턱 오르기 0.35 m)으로
/// 금화까지 곧장 걸어 몇 개를 줍는지 · 어디서 왜 멈추는지 센다(첫판은 NavMesh 길을 따라 걷다 층계에 올라 방을 떠났다).
/// 로그 「[DKGold] …」 · 끝 「[DKGold] 끝」.
/// </summary>
public static class Req1111DKGoldProbeEditor
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const float WalkSpeed = 6f;

    [MenuItem("RelicFairy/Debug/11-11 Ch3 기사 처치 뒤 골드 줍기 (보스방, Play)")]
    private static void Run()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[DKGold] 플레이 중에"); return; }
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        try
        {
            var player = GameRunBootstrapper.Instance?.Run?.Player;
            var room   = UnityEngine.Object.FindFirstObjectByType<BossRoomController>();
            if (player == null || room == null) { Debug.LogWarning($"[DKGold] 플레이어 {player != null} · 보스방 {room != null}"); return; }
            player.SetInvincible(900f);

            // 1) 전투 열기 — 입장 트리거를 안 지났으면 바깥 진입으로 연다(등장 연출 · 유리 켜짐까지 기다린다)
            if (!room.IsCombatReady) room.BeginBossFightExternally(player);
            float t0 = Time.realtimeSinceStartup;
            while (!room.IsCombatReady && Time.realtimeSinceStartup - t0 < 90f)
                await UniTask.Delay(500, DelayType.Realtime);
            var boss = UnityEngine.Object.FindObjectsByType<DeathKnightBossMonster>(FindObjectsSortMode.None).FirstOrDefault(b => !b.IsDead);
            if (!room.IsCombatReady || boss == null) { Debug.LogWarning($"[DKGold] 전투가 안 열렸다 — 준비 {room.IsCombatReady} · 기사 {boss != null}"); return; }
            await UniTask.Delay(1500, DelayType.Realtime);

            var barriers = typeof(DeathKnightBossMonster).GetField("_entranceEndBarriers", Inst)?.GetValue(boss) as GameObject[] ?? Array.Empty<GameObject>();
            Debug.Log($"[DKGold] 전투 열림 — 기사 {boss.transform.position} · 플레이어 {player.transform.position} · " +
                      $"유리 {Describe(barriers)} · 플레이어→기사 길 {PathTo(player.transform.position, boss.transform.position)}");
            foreach (var b in barriers)
                if (b != null)
                    foreach (var col in b.GetComponentsInChildren<Collider>())
                        Debug.Log($"[DKGold] 유리 {b.name}/{col.name} 범위 {col.bounds.min} ~ {col.bounds.max}");

            // 2) 쓰러뜨리기(10-25 ExitReach와 같은 방식 — 50% 관문은 푼다). 해방기 · 악몽은 2페이지로 넘기고 전환이 끝난 뒤
            typeof(DeathKnightBossMonster).GetField("_soulGateCleared", Inst)?.SetValue(boss, true);
            var pages = boss.Pages;
            if (pages != null && pages.Enabled && !pages.IsPage2)
            {
                Debug.Log($"[DKGold] {TestRunOverlay.JumpBossToNextPage()}");
                for (int i = 0; i < 40 && !pages.IsPage2 && !pages.Transitioning; i++)
                {
                    boss.TakeDamage(Mathf.Max(50, Mathf.RoundToInt(boss.EffectiveMaxHp * 0.02f)), player.gameObject);
                    await UniTask.Delay(250, DelayType.Realtime);
                }
                t0 = Time.realtimeSinceStartup;
                while ((pages.Transitioning || !pages.IsPage2) && Time.realtimeSinceStartup - t0 < 40f)
                    await UniTask.Delay(500, DelayType.Realtime);
                await UniTask.Delay(2000, DelayType.Realtime);
                Debug.Log($"[DKGold] 2페이지 {pages.IsPage2} · 전환 중 {pages.Transitioning} · 기사 {boss.transform.position} · 유리 {Describe(barriers)}");
            }
            string hp = TestRunOverlay.JumpBossNearDeath();
            Debug.Log($"[DKGold] {hp}");
            for (int i = 0; i < 20 && !boss.IsDead; i++)
            {
                boss.TakeDamage(boss.EffectiveMaxHp, player.gameObject);
                await UniTask.Delay(250, DelayType.Realtime);
            }
            Vector3 fall = boss.transform.position;
            Debug.Log($"[DKGold] 기사 쓰러짐 {boss.IsDead} — 자리 {fall} 발밑 {Ground(fall)} · 유리 {Describe(barriers)}");

            // 3) 금화 — 내려앉은 뒤 자리 · 높이 · 길
            await UniTask.Delay(700, DelayType.Realtime);
            var coins     = LiveCoins();
            var collected = new HashSet<GoldCoinPickup>();
            Debug.Log($"[DKGold] 금화 {coins.Count}개 — {DescribeCoins(coins, player.transform.position)}");

            // 4) 바닥 높이 지도 — 플레이어 쪽에서 기사 자리까지(1 m 칸 · 기사 자리 기준 x −8~+8 · z −12~+4)
            Vector3 toPlayer = Flat(player.transform.position - fall).normalized;
            if (toPlayer.sqrMagnitude < 0.01f) toPlayer = Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, toPlayer);
            for (int r = 12; r >= -4; r--)
            {
                var row = new System.Text.StringBuilder();
                for (int c = -8; c <= 8; c++)
                {
                    Vector3 p = fall + toPlayer * r + side * c;
                    row.Append(Physics.Raycast(new Vector3(p.x, fall.y + 6f, p.z), Vector3.down, out var h, 12f, ~0, QueryTriggerInteraction.Ignore)
                        ? $"{h.point.y,5:0.00}" : "    -");
                }
                Debug.Log($"[DKGold] 높이 {(r > 0 ? "플레이어 쪽" : "뒤쪽")} {Mathf.Abs(r),2} m: {row}");
            }

            // 5a) 제자리 — 싸우던 자리에 선 채(움직이지 않고) 8초: 보스 금화가 날아와 들어오는가(11-11 고침). 파츠 고르기 창이 뜨면 넘긴다.
            float s0 = Time.realtimeSinceStartup, sDraft = s0;
            while (Time.realtimeSinceStartup - s0 < 8f && Alive(coins) > 0)
            {
                if (Time.timeScale < 0.01f && Time.realtimeSinceStartup - sDraft > 1.5f) { sDraft = Time.realtimeSinceStartup; EditorApplication.ExecuteMenuItem("RelicFairy/Debug/Confirm Draft Popup (Play)"); }
                Track(coins, collected);
                await UniTask.Delay(100, DelayType.Realtime);
            }
            Track(coins, collected);
            Debug.Log($"[DKGold] 제자리 {Time.realtimeSinceStartup - s0:0.0}초 — 플레이어 {player.transform.position}(금화까지 {Flat(fall - player.transform.position).magnitude:0.0} m) · 주움 {collected.Count}/{coins.Count} · 남음 {Alive(coins)}");

            // 5) 플레이어 걸음으로 금화까지 — 기사 자리에서 플레이어 쪽 9 m(층계 자리를 비켜 옆 4 m)에서 곧장 걷는다.
            //    속도 + 턱 오르기(StepClimb · 0.35 m)를 그대로 쓴다. 파츠 고르기 창이 뜨면 넘긴다.
            var rb = player.GetComponent<Rigidbody>();
            Vector3 start = fall + toPlayer * 9f + side * 4f;
            if (Physics.Raycast(new Vector3(start.x, fall.y + 6f, start.z), Vector3.down, out var sh, 12f, ~0, QueryTriggerInteraction.Ignore)) start = sh.point + Vector3.up * 0.05f;
            player.transform.position = start;
            if (rb != null) { rb.position = start; rb.linearVelocity = Vector3.zero; }
            await UniTask.Delay(300, DelayType.Realtime);
            Debug.Log($"[DKGold] 걷기 시작 {start} 발밑 {Ground(start)} → 금화 가운데 {Center(coins)}");
            float r0 = Time.realtimeSinceStartup, lastMove = r0, lastDraft = r0, lastTrace = r0, minDist = float.MaxValue;
            Vector3 lastPos = rb != null ? rb.position : start;
            string blocked = null;
            while (rb != null && Time.realtimeSinceStartup - r0 < 20f && Alive(coins) > 0)
            {
                if (Time.timeScale < 0.01f)
                {
                    if (Time.realtimeSinceStartup - lastDraft > 1.5f) { lastDraft = Time.realtimeSinceStartup; EditorApplication.ExecuteMenuItem("RelicFairy/Debug/Confirm Draft Popup (Play)"); }
                    lastMove = Time.realtimeSinceStartup;
                    await UniTask.Delay(200, DelayType.Realtime);
                    continue;
                }
                Track(coins, collected);
                var near = Nearest(coins, rb.position);
                if (near == null) break;
                Vector3 dir = Flat(near.transform.position - rb.position);
                minDist = Mathf.Min(minDist, (near.transform.position - rb.position).magnitude);
                if (dir.sqrMagnitude < 0.04f) { await UniTask.WaitForFixedUpdate(); continue; }
                dir.Normalize();
                rb.linearVelocity = new Vector3(dir.x * WalkSpeed, rb.linearVelocity.y, dir.z * WalkSpeed);
                player.MoveAbility?.StepClimb(player, dir);
                if ((rb.position - lastPos).magnitude > 0.3f) { lastPos = rb.position; lastMove = Time.realtimeSinceStartup; }
                if (Time.realtimeSinceStartup - lastTrace > 0.5f) { lastTrace = Time.realtimeSinceStartup; Debug.Log($"[DKGold] · 자리 {rb.position} · 가까운 금화까지 {(near.transform.position - rb.position).magnitude:0.0} m · 주움 {collected.Count}"); }
                if (Time.realtimeSinceStartup - lastMove > 1.5f)
                {
                    Vector3 at = rb.position;
                    blocked = $"멈춤 {at} 발밑 {Ground(at)} · 앞 0.6 m 바닥 {Ground(at + dir * 0.6f)} · 앞 1.2 m 바닥 {Ground(at + dir * 1.2f)} · 앞 광선 0.1 m {RayHits(at + Vector3.up * 0.1f, at + Vector3.up * 0.1f + dir * 2f, player.transform)} · 0.3 m {RayHits(at + Vector3.up * 0.3f, at + Vector3.up * 0.3f + dir * 2f, player.transform)} · 0.6 m {RayHits(at + Vector3.up * 0.6f, at + Vector3.up * 0.6f + dir * 2f, player.transform)} · 1.0 m {RayHits(at + Vector3.up, at + Vector3.up + dir * 2f, player.transform)}";
                    break;
                }
                await UniTask.WaitForFixedUpdate();
            }
            if (rb != null) rb.linearVelocity = Vector3.zero;
            await UniTask.Delay(800, DelayType.Realtime);
            Track(coins, collected);
            int left = Alive(coins);
            Debug.Log($"[DKGold] 걷기 — 주움 {collected.Count}/{coins.Count} · 남음 {left} · 가까운 금화까지 최소 {minDist:0.0} m(줍기 0.6 · 끌림 4.5) · {blocked ?? "막힘 없음"}");
            // 줍는 순간 녹아 사라지는 금화(0.18초)는 Track 사이에 지워져 셈에서 빠질 수 있다 — 남은 금화 0이면 다 주운 것으로 본다
            Debug.Log(coins.Count > 0 && left == 0
                ? $"[DKGold] PASS — 기사 자리 금화 {coins.Count}개 모두 주움(셈 {collected.Count})"
                : $"[DKGold] FAIL — 금화 {coins.Count}개 중 {coins.Count - left}개만 주움(남음 {left})");
        }
        catch (Exception e) { Debug.LogException(e); }
        finally { Debug.Log("[DKGold] 끝"); }
    }

    // ── 금화 ─────────────────────────────────────────────────
    private static List<GoldCoinPickup> LiveCoins()
        => (typeof(GoldCoinPickup).GetField("s_live", Stat)?.GetValue(null) as List<GoldCoinPickup>)?.Where(c => c != null).ToList() ?? new List<GoldCoinPickup>();

    private static bool Flag(GoldCoinPickup c, string name) => c != null && (bool)(typeof(GoldCoinPickup).GetField(name, Inst)?.GetValue(c) ?? false);

    private static void Track(List<GoldCoinPickup> coins, HashSet<GoldCoinPickup> collected)
    {
        foreach (var c in coins)
            if (c != null && Flag(c, "_collected")) collected.Add(c);
    }

    private static bool IsAlive(GoldCoinPickup c) => c != null && !Flag(c, "_collected") && !Flag(c, "_vanishing");
    private static int Alive(List<GoldCoinPickup> coins) => coins.Count(IsAlive);

    private static GoldCoinPickup Nearest(List<GoldCoinPickup> coins, Vector3 from)
        => coins.Where(IsAlive).OrderBy(c => (c.transform.position - from).sqrMagnitude).FirstOrDefault();

    private static Vector3? Center(List<GoldCoinPickup> coins)
    {
        var live = coins.Where(IsAlive).ToList();
        if (live.Count == 0) return null;
        Vector3 s = Vector3.zero;
        foreach (var c in live) s += c.transform.position;
        return s / live.Count;
    }

    private static string DescribeCoins(List<GoldCoinPickup> coins, Vector3 player)
    {
        var live = coins.Where(IsAlive).ToList();
        if (live.Count == 0) return "남은 금화 없음";
        Vector3 c = Center(coins).Value;
        float minY = live.Min(x => x.transform.position.y), maxY = live.Max(x => x.transform.position.y);
        return $"가운데 {c} · 높이 {minY:0.00}~{maxY:0.00}(플레이어 {player.y:0.00}) · 플레이어까지 {Flat(c - player).magnitude:0.0} m · 가운데 발밑 {Ground(c)} · 길 {PathTo(player, c)}";
    }

    // ── 재기 ─────────────────────────────────────────────────
    private static readonly NavMeshQueryFilter s_walk = new() { agentTypeID = 0, areaMask = NavMesh.AllAreas };

    private static string PathTo(Vector3 from, Vector3 to)
    {
        if (!NavMesh.SamplePosition(from, out var a, 3f, s_walk)) return "출발 면 없음";
        if (!NavMesh.SamplePosition(to, out var b, 3f, s_walk)) return "도착 면 없음(3 m 안)";
        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(a.position, b.position, s_walk, path)) return "계산 실패";
        Vector3 end = path.corners.Length > 0 ? path.corners[path.corners.Length - 1] : a.position;
        return $"{path.status}(끝에서 도착까지 {Flat(b.position - end).magnitude:0.0} m · 도착 면 {b.position.y:0.00})";
    }

    private static string Ground(Vector3 p)
        => Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, 8f, ~0, QueryTriggerInteraction.Ignore)
            ? $"{hit.collider.name} y {hit.point.y:0.00}" : "바닥 없음(8 m)";

    private static string RayHits(Vector3 from, Vector3 to, Transform self)
    {
        var hits = Physics.RaycastAll(from, (to - from).normalized, Vector3.Distance(from, to), ~0, QueryTriggerInteraction.Ignore)
                          .Where(h => !h.transform.IsChildOf(self)).OrderBy(h => h.distance).Take(5)
                          .Select(h => $"{h.collider.name}[{LayerMask.LayerToName(h.collider.gameObject.layer)}]" +
                                       $"{(h.collider.GetComponentInParent<SoftBarrier>(true) != null ? "(유리 장벽)" : "")}" +
                                       $"{(h.collider.GetComponentInParent<DKResonanceStone>(true) != null ? "(성흔석)" : "")} {h.distance:0.0} m");
        return string.Join(" · ", hits.DefaultIfEmpty("걸림 없음"));
    }

    private static string Describe(GameObject[] barriers)
        => barriers.Length == 0 ? "없음" : string.Join(" · ", barriers.Select(b => b == null ? "(없음)" : $"{b.name} {(b.activeInHierarchy ? "켜짐" : "꺼짐")}"));

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
}
#endif
