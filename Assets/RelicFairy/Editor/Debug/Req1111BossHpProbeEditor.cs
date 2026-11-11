#if UNITY_EDITOR
using System.Reflection;
using System.Text;
using RelicFairy.Monster;
using UnityEditor;
using UnityEngine;

/// <summary>
/// [편집 모드] 11-11 보스 시기 체력 확인 — 테스트 시기 덮어쓰기(봉인기 · 해방기 · 악몽)마다 네 챕터 보스의 총 체력을 계산한다.
/// 숲 · 화룡 · 기사 = config 최대 체력 × 실제 BossPages.HpScale · 리치 = × LichMonster.BossHpScale(리플렉션).
/// 끝나면 덮어쓰기를 원래 값으로 돌린다. 결과: 로그 「[BossHp1111]」.
/// </summary>
public static class Req1111BossHpProbeEditor
{
    private const string Root = "Assets/RelicFairy/Characters/Monster/";
    private static readonly int[]    Eras     = { 0, 1, 2 };   // StoryProgress 덮어쓰기 — 봉인기 · 해방기 · 악몽 모드
    private static readonly string[] EraNames = { "봉인기", "해방기", "악몽" };
    private static readonly (string name, string prefab, string config)[] Bosses =
    {
        ("Ch1 숲의 수호자", Root + "Monster/ForestGuardian/Prefab/ForestGuardian.prefab", Root + "Monster/ForestGuardian/SO/ForestGuardianBossConfig.asset"),
        ("Ch2 화룡",       Root + "Monster/DragonBoss/Prefab/DragonBoss.prefab",         Root + "Monster/DragonBoss/SO/DragonBossConfig.asset"),
        ("Ch3 죽음의 기사", Root + "Monster/DeathKnightBoss/Prefab/DeathKnightBoss.prefab", Root + "Monster/DeathKnightBoss/SO/DeathKnightBossConfig.asset"),
        ("Ch4 리치",       Root + "Boss_Monster/Lich/Lich.prefab",                       Root + "Boss_Monster/Lich/LichConfig.asset"),
    };

    [MenuItem("RelicFairy/Debug/11-11 보스 시기 체력 확인 (편집 모드)")]
    private static void Run()
    {
        if (Application.isPlaying) { Debug.LogWarning("[BossHp1111] 편집 모드에서만"); return; }
        int saved = EditorPrefs.GetInt(StoryProgress.DebugOverridePrefsKey, -1);
        var scaleProp = typeof(MonsterBase).GetProperty("BossHpScale", BindingFlags.Instance | BindingFlags.NonPublic);
        var sb = new StringBuilder("[BossHp1111] 보스 총 체력 — 봉인기 / 해방기 / 악몽\n");
        try
        {
            foreach (var b in Bosses)
            {
                var boss = AssetDatabase.LoadAssetAtPath<GameObject>(b.prefab)?.GetComponent<MonsterBase>();
                var cfg  = AssetDatabase.LoadAssetAtPath<MonsterConfigSO>(b.config);
                if (boss == null || cfg == null) { sb.AppendLine($"{b.name}: 프리팹/설정 못 읽음"); continue; }
                sb.Append($"{b.name} (기본 {cfg.stat.maxHp:N0}):");
                for (int i = 0; i < Eras.Length; i++)
                {
                    EditorPrefs.SetInt(StoryProgress.DebugOverridePrefsKey, Eras[i]);
                    StoryProgress.RefreshDebugOverride();
                    var pages = boss is IPagedBoss ? new BossPages(boss, BossPages.ResolveEnabled()) : null;
                    float scale = pages != null ? pages.HpScale : (float)scaleProp.GetValue(boss);
                    int total = Mathf.RoundToInt(cfg.stat.maxHp * scale);
                    string page2 = pages != null && pages.Enabled ? $" · 2페이지 {Mathf.RoundToInt(total * pages.Page2Share / (1f + pages.Page2Share)):N0}" : "";
                    sb.Append($" | {EraNames[i]} {total:N0}(×{scale:0.###}{page2})");
                }
                sb.AppendLine();
            }
        }
        finally
        {
            EditorPrefs.SetInt(StoryProgress.DebugOverridePrefsKey, saved);
            StoryProgress.RefreshDebugOverride();
        }
        Debug.Log(sb.ToString());
    }
}
#endif
