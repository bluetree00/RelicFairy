using UnityEditor;

/// <summary>촬영 모드(<see cref="PhotoMode"/>) 키를 메뉴로도 — MCP · 실측 스크립트가 플레이 중에 부른다.</summary>
public static class PhotoModeMenu
{
    private const string Root = "RelicFairy/Photo/";

    [MenuItem(Root + "4K 저장 (F12)")]              private static void Capture()   => PhotoMode.Capture();
    [MenuItem(Root + "화면 UI 숨기기 · 보이기 (F10)")] private static void Ui()        => PhotoMode.ToggleUi();
    [MenuItem(Root + "시간 멈춤 · 풀기 (F11)")]       private static void Freeze()    => PhotoMode.ToggleFreeze();
    [MenuItem(Root + "몬스터 이름표 숨기기 · 보이기 (F9)")] private static void Bars()   => PhotoMode.ToggleMonsterBars();
    [MenuItem(Root + "피해 숫자 끄기 · 켜기 (F8)")]          private static void Numbers() => PhotoMode.ToggleDamageNumbers();
    [MenuItem(Root + "카메라 가까이 ([)")]            private static void ZoomIn()    => PhotoMode.AddZoom(-0.1f);
    [MenuItem(Root + "카메라 멀리 (])")]              private static void ZoomOut()   => PhotoMode.AddZoom(0.1f);
    [MenuItem(Root + "카메라 왼쪽으로 (,)")]          private static void TurnLeft()  => PhotoMode.Turn(-10f);
    [MenuItem(Root + "카메라 오른쪽으로 (.)")]        private static void TurnRight() => PhotoMode.Turn(10f);

    /// <summary>스토어 스크린샷은 영어 세트 · 한국어 세트를 따로 찍는다(게임 설정 화면과 같은 저장값).</summary>
    [MenuItem(Root + "언어 바꾸기 (한 ↔ 영)")]
    private static void SwapLanguage() =>
        Loc.SetLanguage(Loc.Current == Loc.Language.Korean ? Loc.Language.English : Loc.Language.Korean);
}
