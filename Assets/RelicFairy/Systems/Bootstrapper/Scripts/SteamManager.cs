using UnityEngine;
using Steamworks;

/// <summary>
/// SteamAPI 라이프사이클 관리 (Init / RunCallbacks / Shutdown)
/// AppBootstrapper가 생성하며 DontDestroyOnLoad로 앱 전체 유지
/// </summary>
public sealed class SteamManager : MonoBehaviour
{
    /// <summary>Steam 앱 번호(steam_appid.txt와 같은 값).</summary>
    public const uint AppId = 4488270;

    public static SteamManager Instance { get; private set; }
    public static bool Initialized { get; private set; }

    /// <summary>에디터 또는 개발 빌드(exe 옆에 steam_appid.txt — BuildScript가 복사 · Steam 업로드본엔 없다).</summary>
    public static bool IsDevBuild =>
        Application.isEditor || System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath) ?? "", "steam_appid.txt"));

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

#if !UNITY_EDITOR
        // 11-06 Steam 밖에서 exe를 직접 켜면 Steam으로 다시 켠다 — Steam 없이 켜지면 계정을 몰라 Steam 계정 세이브 폴더(SaveRoot)를 못 찾아 진행이 사라진 것처럼 보였다.
        // 개발 빌드(exe 옆 steam_appid.txt)는 다시 켜지 않는다 — Steam은 그 파일을 「작업 폴더」에서 찾아, 바로가기 · 도구로 다른 폴더에서 켜면 바로 꺼졌다(11-06 21차 부팅 확인).
        // 대신 앱 번호를 환경 변수로 줘 어느 폴더에서 켜도 Init이 붙는다(실측: 파일 없이 SteamAppId만으로 Init 성공). Steam 업로드에서는 그 파일을 뺀다.
        if (IsDevBuild)
        {
            System.Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
        }
        else
        {
            try
            {
                if (SteamAPI.RestartAppIfNecessary(new AppId_t(AppId)))
                {
                    Application.Quit();
                    return;
                }
            }
            catch (System.DllNotFoundException e)
            {
                Debug.LogError($"[SteamManager] steam_api64.dll을 못 찾았다 — Steam 없이 계속: {e.Message}");
            }
        }
#endif

        if (!Packsize.Test())
        {
            Debug.LogError("[SteamManager] Packsize 불일치 — 잘못된 Steamworks.NET 빌드입니다.");
            return;
        }

        if (!SteamAPI.Init())
        {
            Debug.LogError("[SteamManager] SteamAPI.Init() 실패 — Steam 클라이언트가 실행 중인지 확인하세요.");
            return;
        }

        Initialized = true;
        Debug.Log($"[SteamManager] 초기화 성공 | {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID()})");
    }

    private void Update()
    {
        if (!Initialized) return;
        SteamAPI.RunCallbacks();
        SteamAchievements.Tick();   // 11-06 — 이번 프레임에 새로 연 도전과제를 한 번에 보낸다
    }

    private void OnDestroy()
    {
        if (!Initialized) return;

        SteamAPI.Shutdown();
        Initialized = false;
    }
}
