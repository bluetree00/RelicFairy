using System.Collections.Generic;
using UnityEngine;

namespace RelicFairy.Monster
{
/// <summary>
/// DeathKnight 보스룸 그리드 컨텍스트.
/// 맵 바닥을 Width×Height 셀로 나누며, 셀↔월드 좌표 변환을 제공한다.
/// 기본값: 30×30, cellSize=1f, 중심=Vector3.zero
/// </summary>
public static class DKBossRoomContext
{
    // ── 칸 바닥 높이(11-11) ─────────────────────────────────
    private const float FloorProbeUp    = 4f;     // 격자 중심 높이 + 이만큼에서 아래로 — 기사 단(0.46) · 잔해 위
    private const float FloorProbeDepth = 8f;
    private const float FloorProbeInset = 0.3f;   // 칸 가운데에서 네 모서리 쪽으로 칸 크기 × 이만큼(2 m 칸 → 0.6 m)
    private const float FloorBand       = 0.25f;  // 기준 면 ± 이만큼만 같은 바닥 — 잔해(0.06~0.18)는 따라 오르고 모서리에 걸친 단(0.46) · 의자는 안 따른다(칸이 뜬다)
    private const float ObstacleBand    = 0.6f;   // 칸 가운데가 가장 낮은 면 + 이보다 높으면(계단 · 기둥) 가운데 대신 가장 낮은 면을 기준으로

    private static readonly Dictionary<Vector2Int, float> s_floorY = new();
    private static readonly RaycastHit[] s_hits = new RaycastHit[16];
    private static readonly Vector3[] s_probes = { Vector3.zero, new(1f, 0f, 1f), new(1f, 0f, -1f), new(-1f, 0f, 1f), new(-1f, 0f, -1f) };
    private static readonly float[] s_tops = new float[5];

    // ── 기본 설정 ──────────────────────────────────────────
    public static int     Width       = 30;
    public static int     Height      = 30;
    public static float   CellSize    = 1f;
    public static Vector3 WorldCenter => _overrideActive ? _worldCenterOverride : _worldCenterBase;

    private static Vector3 _worldCenterBase;
    private static Vector3 _worldCenterOverride;
    private static bool    _overrideActive;

    // ── 초기화 ─────────────────────────────────────────────

    /// <summary>보스룸 그리드 파라미터를 설정한다.</summary>
    public static void Initialize(int width, int height, float cellSize, Vector3 worldCenter)
    {
        Width            = width;
        Height           = height;
        CellSize         = cellSize;
        _worldCenterBase = worldCenter;
        _overrideActive  = false;
        s_floorY.Clear();
    }

    /// <summary>특정 패턴 실행 시 그리드 중심을 임시로 지정된 위치로 교체한다.</summary>
    public static void SetWorldCenterOverride(Vector3 center)
    {
        _worldCenterOverride = center;
        _overrideActive      = true;
    }

    /// <summary>SetWorldCenterOverride로 설정한 오버라이드를 제거하고 원래 중심으로 복귀한다.</summary>
    public static void ClearWorldCenterOverride()
    {
        _overrideActive = false;
    }

    // ── 좌표 변환 ──────────────────────────────────────────

    /// <summary>
    /// 그리드 셀 (x, z) → 월드 좌표 변환.
    /// yOffset: 바닥에서 살짝 띄울 높이 (장판 Z-파이팅 방지).
    /// </summary>
    public static Vector3 CellToWorld(int x, int z, float yOffset = 0.05f)
    {
        float wx = WorldCenter.x + (x * CellSize) - (Width  - 1) * 0.5f * CellSize;
        float wz = WorldCenter.z + (z * CellSize) - (Height - 1) * 0.5f * CellSize;
        return new Vector3(wx, WorldCenter.y + yOffset, wz);
    }

    /// <summary>칸 가운데 — 그 칸 바닥(<see cref="CellFloorY"/>) + <paramref name="yOffset"/>. 패턴 칸 · 테두리 · 줄 예고 칸을 깔 때.</summary>
    public static Vector3 TileToWorld(int x, int z, float yOffset = 0.05f)
    {
        Vector3 p = CellToWorld(x, z, 0f);
        p.y = CellFloorY(x, z) + yOffset;
        return p;
    }

    /// <summary>
    /// 칸 (x, z)의 바닥 높이 — 패턴 칸을 이 위에 깐다(11-11 사용자 「Ch3 보스방 바닥 타일이 안 보이거나 들어간다」:
    /// 칸을 격자 중심 높이 + 0.05에 깔아 돌 잔해(0.06~0.18) · 기사 단(0.46) 같은 높은 바닥 조각에 묻혔다 — 플레이어가 설 수 있는 칸 401 중 78).
    /// 칸 가운데와 네 모서리 안쪽에서 아래로 쏴 맨 위 면을 잡고, 칸 가운데 면(계단 · 기둥이면 가장 낮은 면) ± <see cref="FloorBand"/> 안에서 가장 높은 면 —
    /// 잔해는 따라 오르되 모서리에 걸친 단 · 의자 높이로 칸이 뜨지 않게(첫 판: 가장 낮은 면 + 0.6이면 18칸이 0.3~0.56 m 떴다).
    /// 맞힐 수 있는 것(몬스터 · 플레이어 · 기둥 · 성흔석) · 트리거 · 결계 유리는 바닥이 아니다. 아무것도 못 맞히면 격자 중심 높이. 칸마다 한 번 재고 기억한다.
    /// </summary>
    public static float CellFloorY(int x, int z)
    {
        Vector3 c = CellToWorld(x, z, 0f);
        var key = new Vector2Int(Mathf.RoundToInt(c.x * 4f), Mathf.RoundToInt(c.z * 4f));
        if (s_floorY.TryGetValue(key, out float cached)) return cached;

        float inset = CellSize * FloorProbeInset;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < s_probes.Length; i++)
        {
            s_tops[i] = TopSurface(c + s_probes[i] * inset);
            if (s_tops[i] > float.MinValue && s_tops[i] < lo) lo = s_tops[i];
        }
        float center = s_tops[0];   // s_probes[0] = 칸 가운데
        float basis  = center > float.MinValue && center <= lo + ObstacleBand ? center : lo;
        for (int i = 0; i < s_tops.Length; i++)
            if (s_tops[i] > float.MinValue && Mathf.Abs(s_tops[i] - basis) <= FloorBand && s_tops[i] > hi) hi = s_tops[i];
        float y = hi > float.MinValue ? hi : WorldCenter.y;
        s_floorY[key] = y;
        return y;
    }

    /// <summary>모든 칸의 바닥 높이를 미리 잰다 — 첫 패턴(칸 수백 개)에서 한꺼번에 재면 한 프레임이 끊긴다. 격자를 정한 직후(방을 열 때) 부른다.</summary>
    public static void WarmFloorHeights()
    {
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
            CellFloorY(x, z);
    }

    /// <summary>이 자리 맨 위 바닥 면 높이 — 없으면 float.MinValue.</summary>
    private static float TopSurface(Vector3 p)
    {
        int n = Physics.RaycastNonAlloc(new Vector3(p.x, WorldCenter.y + FloorProbeUp, p.z), Vector3.down, s_hits,
                                        FloorProbeUp + FloorProbeDepth, ~0, QueryTriggerInteraction.Ignore);
        float top = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var col = s_hits[i].collider;
            if (s_hits[i].point.y <= top || col.GetComponentInParent<IDamageable>() != null || col.GetComponentInParent<SoftBarrier>() != null) continue;
            top = s_hits[i].point.y;
        }
        return top;
    }

    /// <summary>월드 좌표 → 그리드 셀 변환.</summary>
    public static Vector2Int WorldToCell(Vector3 world)
    {
        float localX = world.x - WorldCenter.x + (Width  - 1) * 0.5f * CellSize;
        float localZ = world.z - WorldCenter.z + (Height - 1) * 0.5f * CellSize;
        int cellX = Mathf.RoundToInt(localX / CellSize);
        int cellZ = Mathf.RoundToInt(localZ / CellSize);
        cellX = Mathf.Clamp(cellX, 0, Width  - 1);
        cellZ = Mathf.Clamp(cellZ, 0, Height - 1);
        return new Vector2Int(cellX, cellZ);
    }

    /// <summary>
    /// 벽(외곽)을 제외한 내부 셀 목록 반환.
    /// x=1..Width-2, z=1..Height-2
    /// </summary>
    public static IEnumerable<Vector2Int> GetInteriorCells()
    {
        for (int z = 1; z <= Height - 2; z++)
        for (int x = 1; x <= Width  - 2; x++)
            yield return new Vector2Int(x, z);
    }

    /// <summary>해당 좌표가 내부 셀인지 판정한다.</summary>
    public static bool IsInterior(int x, int z)
        => x >= 1 && x <= Width - 2 && z >= 1 && z <= Height - 2;
}
}
