using Velopack;
using Velopack.Sources;

namespace LaLaLand.Services;

/// <summary>
/// In-app updates through Velopack and the GitHub Releases of this repository. The portable zip and Setup.exe both
/// update in place, wherever the user put the folder; a development build can only point to the release page.
/// </summary>
public sealed class UpdateService
{
    public const string ReleasesUrl = "https://github.com/xqzh268/lalaland/releases";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly UpdateManager _manager = new(CreateSource());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _info;
    private Timer? _timer;

    /// <summary>GitHub Releases; LALALAND_UPDATE_SOURCE (a folder or URL of a Velopack release feed) overrides it for testing.</summary>
    private static IUpdateSource CreateSource() =>
        Environment.GetEnvironmentVariable("LALALAND_UPDATE_SOURCE") is { Length: > 0 } custom
            ? new SimpleWebSource(custom)
            : new GithubSource("https://github.com/xqzh268/lalaland", null, false);

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "";
    public string? AvailableVersion => _info?.TargetFullRelease.Version.ToString();
    public string? ReleaseNotes => _info?.TargetFullRelease.NotesMarkdown;
    public bool UpdateReady { get; private set; }
    public string Status { get; private set; } = "";
    public event Action? Changed;

    /// <summary>Checks shortly after start and then twice a day, as long as <paramref name="enabled"/> says so.</summary>
    public void StartBackgroundChecks(Func<bool> enabled)
    {
        _timer = new Timer(async _ =>
        {
            if (enabled() && IsInstalled) await CheckAsync();
        }, null, TimeSpan.FromSeconds(30), CheckInterval);
    }

    public async Task<bool> CheckAsync()
    {
        if (!await _gate.WaitAsync(0)) return _info is not null;
        try
        {
            Set("正在检查更新…");
            if (!IsInstalled)
            {
                Set("当前是开发版或未通过更新包部署，无法自动更新；请从发布页下载便携包或安装包。");
                return false;
            }
            _info = await _manager.CheckForUpdatesAsync();
            UpdateReady = false;
            Set(_info is null ? $"已是最新版本（{CurrentVersion}）" : $"发现新版本 {AvailableVersion}");
            return _info is not null;
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            Set("检查更新失败，请稍后重试或检查网络");
            return false;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DownloadAsync(Action<int> progress)
    {
        if (_info is null) return false;
        await _gate.WaitAsync();
        try
        {
            Set("正在下载更新…");
            await _manager.DownloadUpdatesAsync(_info, progress);
            UpdateReady = true;
            Set($"版本 {AvailableVersion} 已下载，重启后安装");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Update download failed", ex);
            Set("下载更新失败，请稍后重试");
            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hands the downloaded update to Velopack, which installs it once this process has exited and relaunches.</summary>
    public bool ApplyOnExit()
    {
        if (_info is null || !UpdateReady) return false;
        _manager.WaitExitThenApplyUpdates(_info, silent: false, restart: true);
        return true;
    }

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
