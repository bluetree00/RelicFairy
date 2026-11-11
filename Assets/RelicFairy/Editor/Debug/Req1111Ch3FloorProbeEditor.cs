#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using RelicFairy.Monster;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 11-11 사용자 「Ch3 보스방 바닥 타일이 자주 망가진다 — 튀어나오거나 · 안 보이거나 · 들어가거나」.
/// 보스방에서 전투를 연 뒤 빌드 화질(LOD 기준 1)로 —
///  ① 아레나 바닥 LODGroup: 빈 단계(그 거리에서 안 보임) · 단계끼리 윗면 높이 · 자리 차(거리 따라 튀어나옴/들어감) · 판정 윗면과 보이는 윗면 차 · 단계에 안 든 렌더러
///  ② 기사 격자 칸(패턴 바닥 칸 — 높이 0.05에 깐다)과 그 자리 실제 바닥 높이: 칸이 바닥에 묻힘(안 보임) · 떠 있음(튀어나옴), 플레이어가 갈 수 있는 칸만 따로
///  ③ 플레이어 쪽 여러 자리 · 전투 중 12초 사진(Temp/req1111_floor/*.png)
/// 결과 Temp/req1111_floor/floor.txt · 로그 「[Ch3Floor] …」 · 끝 「[Ch3Floor] 끝」.
/// </summary>
public static class Req1111Ch3FloorProbeEditor
{
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const float BuildLodBias = 1f;   // 빌드 기본 화질 Balanced(에디터 High Fidelity = 2)
    private const float TileLift     = 0.05f; // DKGridPatternHelper.SpawnTiles 기본 yOffset

    [MenuItem("RelicFairy/Debug/11-11 Ch3 보스방 바닥 점검 (보스방, Play)")]
    private static void Run()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[Ch3Floor] 플레이 중에"); return; }
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "req1111_floor");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder($"11-11 Ch3 보스방 바닥 점검 — {DateTime.Now:HH:mm:ss}\n");
        float biasWas = QualitySettings.lodBias;
        try
        {
            var player = GameRunBootstrapper.Instance?.Run?.Player;
            var room   = UnityEngine.Object.FindFirstObjectByType<BossRoomController>();
            if (player == null || room == null) { sb.AppendLine($"플레이어 {player != null} · 보스방 {room != null}"); return; }
            player.SetInvincible(900f);
            if (!room.IsCombatReady) room.BeginBossFightExternally(player);
            float t0 = Time.realtimeSinceStartup;
            while (!room.IsCombatReady && Time.realtimeSinceStartup - t0 < 90f) await UniTask.Delay(500, DelayType.Realtime);
            var boss = UnityEngine.Object.FindObjectsByType<DeathKnightBossMonster>(FindObjectsSortMode.None).FirstOrDefault(b => !b.IsDead);
            if (!room.IsCombatReady || boss == null) { sb.AppendLine($"전투가 안 열렸다 — 준비 {room.IsCombatReady} · 기사 {boss != null}"); return; }
            await UniTask.Delay(1500, DelayType.Realtime);
            QualitySettings.lodBias = BuildLodBias;

            Transform arena = FindArena(boss.transform);
            float floorY = DKBossRoomContext.WorldCenter.y;
            var anchorT = typeof(DeathKnightBossMonster).GetField("_pyramidStrikeAnchor", Inst)?.GetValue(boss) as Transform;
            Vector3 anchor = anchorT != null ? anchorT.position : player.transform.position;
            sb.AppendLine($"아레나 {(arena != null ? arena.name : "없음")} · 기사 {boss.transform.position} · 플레이어 구역 가운데 {anchor} · 격자 {DKBossRoomContext.Width}×{DKBossRoomContext.Height} 칸 {DKBossRoomContext.CellSize} m 가운데 {DKBossRoomContext.WorldCenter}");

            LodAudit(arena, floorY, sb);
            GridAudit(anchor, sb);

            // ③ 사진 — 플레이어 쪽 자리마다(카메라가 따라오게 1초) · 그다음 가운데에서 전투 12초
            Vector3 toKnight = Flat(boss.transform.position - anchor).normalized;
            Vector3 side     = Vector3.Cross(Vector3.up, toKnight);
            var spots = new (string name, Vector3 at)[]
            {
                ("가운데", anchor), ("왼쪽 7m", anchor - side * 7f), ("오른쪽 7m", anchor + side * 7f),
                ("뒤 6m", anchor - toKnight * 6f), ("앞 6m(유리 쪽)", anchor + toKnight * 6f), ("뒤 12m", anchor - toKnight * 12f),
            };
            var rb = player.GetComponent<Rigidbody>();
            int n = 0;
            foreach (var (name, at) in spots)
            {
                Vector3 p = Snap(at);
                player.transform.position = p;
                if (rb != null) { rb.position = p; rb.linearVelocity = Vector3.zero; }
                await UniTask.Delay(1200, DelayType.Realtime);
                await Shot(dir, $"s{n++:00}_{name}");
                sb.AppendLine($"사진 s{n - 1:00} {name} — 플레이어 {p} · 카메라 {Camera.main?.transform.position}");
            }
            Vector3 c = Snap(anchor);
            player.transform.position = c;
            if (rb != null) { rb.position = c; rb.linearVelocity = Vector3.zero; }
            int maxSlash = 0, maxDebris = 0;
            for (int i = 0; i < 48; i++)   // 12초 — 0.25초마다 베기 이펙트를 세고 1초마다 사진
            {
                await UniTask.Delay(250, DelayType.Realtime);
                CountSlash(out int s, out int d);
                maxSlash = Mathf.Max(maxSlash, s); maxDebris = Mathf.Max(maxDebris, d);
                if (i % 4 == 3) await Shot(dir, $"f{i / 4:00}");
            }
            sb.AppendLine("전투 사진 f00~f11 — 가운데 · 1초마다");
            string slash = $"③ 베기 이펙트(Sword Slash 15) — 켜진 것 최대 {maxSlash} · 그 안 바닥 조각(SmallStones · GroundCrack) 켜진 것 최대 {maxDebris}";
            sb.AppendLine(slash);
            Debug.Log("[Ch3Floor] " + slash);
        }
        catch (Exception e) { sb.AppendLine("예외: " + e); Debug.LogException(e); }
        finally
        {
            QualitySettings.lodBias = biasWas;
            File.WriteAllText(Path.Combine(dir, "floor.txt"), sb.ToString(), Encoding.UTF8);
            Debug.Log($"[Ch3Floor] 끝 — {dir}");
        }
    }

    // ── ① 아레나 바닥 LOD ─────────────────────────────────────
    private static void LodAudit(Transform arena, float floorY, StringBuilder sb)
    {
        var groups = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None)
                                       .Where(g => g.enabled && g.gameObject.activeInHierarchy && (arena == null || g.transform.IsChildOf(arena))).ToList();
        int floors = 0, bad = 0;
        var lines = new List<string>();
        var counts = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            var lods = g.GetLODs();
            var drawn = new List<(int i, Bounds b)>();
            var empty = new List<int>();
            var registered = new HashSet<Renderer>();
            for (int i = 0; i < lods.Length; i++)
            {
                var rs = lods[i].renderers.Where(r => r != null).ToArray();
                foreach (var r in rs) registered.Add(r);
                if (rs.Length == 0) { empty.Add(i); continue; }
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                drawn.Add((i, b));
            }
            if (drawn.Count == 0) continue;
            Bounds all = drawn[0].b;
            foreach (var d in drawn) all.Encapsulate(d.b);
            // 바닥 같은 것 — 윗면이 바닥 + 1.2 m 아래 · 넓이 0.5 m² 넘음
            if (all.max.y > floorY + 1.2f || all.size.x * all.size.z < 0.5f) continue;
            floors++;
            var issues = new List<string>();
            int lastDrawn = drawn.Max(d => d.i);
            foreach (int e in empty)
                if (e < lastDrawn) Add(issues, counts, $"빈 단계 L{e}(전환 {lods[e].screenRelativeTransitionHeight:0.000}) — 그 거리에서 안 보임", "빈 단계");
            var b0 = drawn[0].b;
            foreach (var d in drawn.Skip(1))
            {
                float dy = d.b.max.y - b0.max.y;
                if (Mathf.Abs(dy) > 0.03f) Add(issues, counts, $"L{d.i} 윗면 {dy:+0.00;-0.00} m", "단계 윗면 차");
                float dxz = Flat(d.b.center - b0.center).magnitude;
                if (dxz > 0.05f) Add(issues, counts, $"L{d.i} 자리 {dxz:0.00} m", "단계 자리 차");
            }
            var cols = g.GetComponentsInChildren<Collider>().Where(cl => cl.enabled && !cl.isTrigger).ToArray();
            if (cols.Length > 0)
            {
                float colTop = cols.Max(cl => cl.bounds.max.y);
                float dy = colTop - b0.max.y;
                if (Mathf.Abs(dy) > 0.05f) Add(issues, counts, $"판정 윗면 {dy:+0.00;-0.00} m(보이는 L0 기준)", "판정 윗면 차");
            }
            var stray = g.GetComponentsInChildren<Renderer>().Where(r => r.enabled && !registered.Contains(r)).ToArray();
            if (stray.Length > 0) Add(issues, counts, $"단계에 안 든 렌더러 {stray.Length}({stray[0].name})", "안 든 렌더러");
            if (issues.Count == 0) continue;
            bad++;
            lines.Add($"  ★ {PathOf(g.transform)} · {g.transform.position:F1} · 윗면 {b0.max.y:0.00} · {string.Join(" · ", issues)}");
        }
        string summary = $"① 바닥 LODGroup {floors}개(아레나 LODGroup {groups.Count}) 중 문제 {bad} — " +
                         (counts.Count == 0 ? "없음" : string.Join(" · ", counts.Select(kv => $"{kv.Key} {kv.Value}")));
        sb.AppendLine(summary);
        foreach (var l in lines.Take(80)) sb.AppendLine(l);
        if (lines.Count > 80) sb.AppendLine($"  … 외 {lines.Count - 80}");
        Debug.Log("[Ch3Floor] " + summary);
    }

    private static void Add(List<string> issues, Dictionary<string, int> counts, string text, string kind)
    {
        issues.Add(text);
        counts[kind] = counts.TryGetValue(kind, out int c) ? c + 1 : 1;
    }

    // ── ② 기사 격자 칸 높이 ───────────────────────────────────
    private static void GridAudit(Vector3 from, StringBuilder sb)
    {
        int cells = 0, buried = 0, floating = 0, reach = 0, rBuried = 0, rFloating = 0;
        var samples = new List<string>();
        foreach (var cell in DKBossRoomContext.GetInteriorCells())
        {
            Vector3 c = DKBossRoomContext.CellToWorld(cell.x, cell.y, 0f);
            float tileY = DKBossRoomContext.TileToWorld(cell.x, cell.y, TileLift).y;   // 패턴이 실제로 까는 높이(11-11 칸 바닥 기준)
            if (!Physics.Raycast(new Vector3(c.x, c.y + 6f, c.z), Vector3.down, out var h, 12f, ~0, QueryTriggerInteraction.Ignore)) continue;
            if (h.collider.GetComponentInParent<MonsterBase>() != null || h.collider.GetComponentInParent<PlayerController>() != null) continue;
            cells++;
            bool canReach = DKGridPatternHelper.IsReachable(from, new Vector3(c.x, h.point.y, c.z));
            if (canReach) reach++;
            float dy = h.point.y - tileY;
            if (dy > 0.01f)
            {
                buried++; if (canReach) { rBuried++; if (samples.Count < 30) samples.Add($"  묻힘 칸 {cell} {c.x:0.0},{c.z:0.0} 바닥 {h.point.y:0.00}({h.collider.name}) · 칸 {tileY:0.00}"); }
            }
            else if (dy < -0.25f)
            {
                floating++; if (canReach) { rFloating++; if (samples.Count < 30) samples.Add($"  뜬 칸 {cell} {c.x:0.0},{c.z:0.0} 바닥 {h.point.y:0.00}({h.collider.name}) · 칸 {tileY:0.00}"); }
            }
        }
        string summary = $"② 격자 칸 {cells} — 바닥에 묻힘 {buried} · 뜸(0.25 m+) {floating} / 플레이어가 갈 수 있는 칸 {reach} 중 묻힘 {rBuried} · 뜸 {rFloating}";
        sb.AppendLine(summary);
        foreach (var s in samples) sb.AppendLine(s);
        Debug.Log("[Ch3Floor] " + summary);
    }

    // ── ③ 베기 이펙트 ─────────────────────────────────────────
    private static void CountSlash(out int slash, out int debris)
    {
        slash = debris = 0;
        foreach (var ps in UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None))
        {
            Transform root = ps.transform;
            while (root.parent != null && !root.name.StartsWith("Sword Slash 15")) root = root.parent;
            if (!root.name.StartsWith("Sword Slash 15")) continue;
            if (ps.transform == root) slash++;
            else if (ps.name == "SmallStones" || ps.name == "GroundCrack") debris++;
        }
    }

    // ── 보조 ─────────────────────────────────────────────────
    private static async UniTask Shot(string dir, string name)
    {
        string path = Path.Combine(dir, name + ".png");
        ScreenCapture.CaptureScreenshot(path);
        for (int i = 0; i < 40 && !File.Exists(path); i++) await UniTask.Delay(50, DelayType.Realtime);
    }

    private static Transform FindArena(Transform from)
    {
        for (var t = from; t != null; t = t.parent)
            if (t.name.StartsWith("Arena_Boss_Ch3")) return t;
        var go = GameObject.Find("Arena_Boss_Ch3(Clone)");
        return go != null ? go.transform : null;
    }

    private static Vector3 Snap(Vector3 p)
        => Physics.Raycast(new Vector3(p.x, p.y + 6f, p.z), Vector3.down, out var h, 12f, ~0, QueryTriggerInteraction.Ignore) ? h.point + Vector3.up * 0.05f : p;

    private static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t != null && i < 3; i++, t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
}
#endif
