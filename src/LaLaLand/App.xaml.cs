using LaLaLand.Interop;
using LaLaLand.Models;
using LaLaLand.Services;
using LaLaLand.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LaLaLand;

public partial class App : Application
{
    private const string InstanceMutexName = "LaLaLand.SingleInstance.7f3c2a";
    private const string ActivateEventName = "LaLaLand.Activate.7f3c2a";
    private static readonly string RunningMarker = Path.Combine(SettingsService.DataDirectory, "running.marker");

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;
    private DispatcherQueue _dispatcher = null!;

    private MessageWindow _messageWindow = null!;
    private TrayIcon _tray = null!;
    private IslandWindow? _island;
    private SettingsWindow? _settingsWindow;

    public static SettingsService Settings { get; private set; } = null!;
    public static MediaService Media { get; private set; } = null!;
    public static LyricsService Lyrics { get; private set; } = null!;
    public static AppleMusicAutomation AppleMusic { get; private set; } = null!;
    public static VolumeService Volume { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static MemoryTrimmer? Trimmer { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled exception", e.Exception);
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string mutexName = InstanceMutexName, eventName = ActivateEventName;
#if DEBUG
        if (PanelAcceptanceDirectory is not null)
        {
            mutexName += "." + Environment.ProcessId;
            eventName += "." + Environment.ProcessId;
        }
#endif
        _instanceMutex = new Mutex(true, mutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: ask the existing instance to open its settings, then leave.
            try
            {
                // Let the running instance take the foreground for its settings window.
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                EventWaitHandle.OpenExisting(eventName).Set();
            }
            catch
            {
            }
            Exit();
            return;
        }

        // Crash resilience: native faults inside system components (e.g. Windows.Media.MediaControl.dll) cannot be
        // caught in managed code. A tiny watchdog copy of this exe relaunches us after a crash, and a marker file
        // tells the next run what happened.
#if DEBUG
        if (PanelAcceptanceDirectory is null)
#endif
            StartWatchdog();
        try
        {
            Directory.CreateDirectory(SettingsService.DataDirectory);
            if (File.Exists(RunningMarker))
                Log.Info("Previous session ended unexpectedly (crash or forced termination); restarted");
            File.WriteAllText(RunningMarker, Environment.ProcessId.ToString());
        }
        catch
        {
        }

#if DEBUG
        // Test hook for crash recovery (Windows only restarts apps that ran for at least 60 s).
        if (Environment.GetEnvironmentVariable("LALALAND_TEST_CRASH") == "1")
            _ = Task.Delay(TimeSpan.FromSeconds(70)).ContinueWith(_ => Environment.FailFast("LALALAND_TEST_CRASH"));
#endif

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        Settings = new SettingsService(_dispatcher);
        AppleMusic = new AppleMusicAutomation();
        Media = new MediaService(_dispatcher, Settings, AppleMusic);
        Lyrics = new LyricsService();
#if DEBUG
        if (PanelAcceptanceDirectory is { } lyricTestDirectory)
            Lyrics = new LyricsService(cacheDirectory: Path.Combine(lyricTestDirectory, "lyrics"));
#endif
        Volume = new VolumeService();

        _messageWindow = new MessageWindow();
        _messageWindow.MessageReceived += OnSystemMessage;
        Hotkeys = new HotkeyService(_messageWindow, Settings);
        Hotkeys.Pressed += OnHotkey;

        Settings.Changed += ApplyPanelVisibility;
        ApplyPanelVisibility();

        _tray = new TrayIcon(_messageWindow) { MenuProvider = BuildTrayMenu };
        _tray.LeftClick += TogglePanelVisibility;
        Media.TrackChanged += UpdateTrayTooltip;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => _dispatcher.TryEnqueue(OpenSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        try
        {
            await Media.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Media session manager unavailable", ex);
        }

        Trimmer = new MemoryTrimmer(_dispatcher);
        Trimmer.Schedule(TimeSpan.FromSeconds(8));
        Log.Info("Started");
#if DEBUG
        if (PanelAcceptanceDirectory is not null)
        {
            if (Environment.GetEnvironmentVariable("LALALAND_LYRIC_STABILITY_TEST") is { Length: > 0 } stabilityMode)
                await RunLyricStabilityAcceptanceAsync(stabilityMode);
            else if (Environment.GetEnvironmentVariable("LALALAND_LYRIC_APPEARANCE_TEST") == "1")
                await RunLyricAppearanceAcceptanceAsync();
            else if (Environment.GetEnvironmentVariable("LALALAND_LYRIC_LAYOUT_FIXTURE") is { Length: > 0 } lyricFixture)
                await RunLyricLayoutAcceptanceAsync(lyricFixture);
            else
                await RunPanelAcceptanceAsync();
        }
#endif
    }

    private static void StartWatchdog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Environment.ProcessPath!, $"{Program.WatchdogArgument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Starting the crash watchdog failed", ex);
        }
    }

    private IntPtr? OnSystemMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Resolution / DPI / taskbar changes move the work area.
        if (msg is Native.WM_DISPLAYCHANGE or Native.WM_SETTINGCHANGE && _island is not null)
            _dispatcher.TryEnqueue(() => _island?.UpdatePlacement());
        return null;
    }

    private void OnHotkey(HotkeyAction action)
    {
        // In "peek only" game mode the island stays hidden; reveal it briefly so the action has visible feedback.
        if (action != HotkeyAction.ToggleIsland)
            _island?.RevealInGame();

        switch (action)
        {
            case HotkeyAction.PlayPause:
                _ = Media.TogglePlayPauseAsync();
                break;
            case HotkeyAction.Next:
                _ = Media.NextAsync();
                break;
            case HotkeyAction.Previous:
                _ = Media.PreviousAsync();
                break;
            case HotkeyAction.ToggleFavorite:
                _ = _island is { } favoriteIsland ? favoriteIsland.ToggleFavoriteAsync() : ToggleHiddenFavoriteAsync();
                break;
            case HotkeyAction.ToggleLyrics:
                _island?.ToggleLyricsPinned();
                break;
            case HotkeyAction.ToggleIsland:
                TogglePanelVisibility();
                break;
            case HotkeyAction.VolumeUp:
                _ = _island is { } volumeUpIsland ? volumeUpIsland.ChangeVolumeAsync((float)Settings.Current.VolumeStep) : ChangeHiddenVolumeAsync((float)Settings.Current.VolumeStep);
                break;
            case HotkeyAction.VolumeDown:
                _ = _island is { } volumeDownIsland ? volumeDownIsland.ChangeVolumeAsync(-(float)Settings.Current.VolumeStep) : ChangeHiddenVolumeAsync(-(float)Settings.Current.VolumeStep);
                break;
            case HotkeyAction.ToggleMute:
                _ = _island is { } muteIsland ? muteIsland.ToggleMuteAsync() : ToggleHiddenMuteAsync();
                break;
        }
    }

    private void TogglePanelVisibility() => Settings.Update(s => s.IsIslandVisible = !s.IsIslandVisible);

    private void ApplyPanelVisibility()
    {
        if (Settings.Current.IsIslandVisible)
        {
            if (_island is null)
            {
                _island = new IslandWindow(Settings, Media, Lyrics, AppleMusic, Volume);
                Log.Info("Panel enabled");
            }
        }
        else if (_island is { } island)
        {
            // Destroy the window rather than keeping a hidden compositor, timers and audio capture alive.
            _island = null;
            island.Shutdown();
            Trimmer?.Schedule(TimeSpan.FromSeconds(1));
            Log.Info("Panel hidden: only hotkeys remain active");
        }
    }

    private static async Task ToggleHiddenFavoriteAsync()
    {
        var source = await Media.GetControlSourceAppIdAsync();
        if (source?.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) == true)
            await AppleMusic.ToggleFavoriteAsync();
    }

    private static async Task ChangeHiddenVolumeAsync(float delta)
    {
        var source = await Media.GetControlSourceAppIdAsync();
        var state = await Volume.StepAsync(source, delta);
        if (state is { Muted: false } volume && Math.Abs(volume.Level - Settings.Current.PlayerVolume) > 0.001)
            Settings.Update(s => s.PlayerVolume = Math.Round(volume.Level, 3));
    }

    private static async Task ToggleHiddenMuteAsync() =>
        await Volume.ToggleMuteAsync(await Media.GetControlSourceAppIdAsync());

    private void UpdateTrayTooltip()
    {
        var track = Media.Track;
        _tray.SetTooltip(track is null ? "LaLaLand" : $"LaLaLand\n{track.Title} — {track.Artist}");
    }

    private IReadOnlyList<TrayIcon.MenuItem> BuildTrayMenu()
    {
        var s = Settings.Current;
        var track = Media.Track;

        TrayIcon.MenuItem Radio<T>(string text, T value, T current, Action<AppSettings, T> set) where T : struct, Enum =>
            new(text, () => Settings.Update(x => set(x, value)), Checked: EqualityComparer<T>.Default.Equals(value, current), Radio: true);

        return
        [
            new(!s.IsIslandVisible ? "面板已隐藏（仅全局快捷键）" : track is null ? "未在播放" : $"♪  {track.Title} — {track.Artist}", Enabled: false),
            TrayIcon.MenuItem.Separator,
            new("隐藏面板（仅保留快捷键）", TogglePanelVisibility, Checked: !s.IsIslandVisible),
            new("展开歌词", () => _island?.ToggleLyricsPinned(), Checked: _island?.IsLyricsPinned == true, Enabled: s.IsIslandVisible && s.LyricsEnabled),
            new("位置", Children:
            [
                Radio("顶部居中", IslandPosition.TopCenter, s.Position, (x, v) => x.Position = v),
                Radio("左上", IslandPosition.TopLeft, s.Position, (x, v) => x.Position = v),
                Radio("右上", IslandPosition.TopRight, s.Position, (x, v) => x.Position = v),
                Radio("左下", IslandPosition.BottomLeft, s.Position, (x, v) => x.Position = v),
                Radio("底部居中", IslandPosition.BottomCenter, s.Position, (x, v) => x.Position = v),
                Radio("右下", IslandPosition.BottomRight, s.Position, (x, v) => x.Position = v),
            ]),
            new("收起样式", Children:
            [
                Radio("封面 + 单行歌词", CollapsedStyle.CoverAndLyric, s.CollapsedStyle, (x, v) => x.CollapsedStyle = v),
                Radio("纯单行歌词", CollapsedStyle.LyricOnly, s.CollapsedStyle, (x, v) => x.CollapsedStyle = v),
                Radio("封面 + 播放控制", CollapsedStyle.CoverAndControls, s.CollapsedStyle, (x, v) => x.CollapsedStyle = v),
            ]),
            new("材质", Children:
            [
                Radio("亚克力", IslandMaterial.Acrylic, s.Material, (x, v) => x.Material = v),
                Radio("毛玻璃", IslandMaterial.Frosted, s.Material, (x, v) => x.Material = v),
                Radio("液态玻璃", IslandMaterial.LiquidGlass, s.Material, (x, v) => x.Material = v),
                Radio("纯色", IslandMaterial.Solid, s.Material, (x, v) => x.Material = v),
                Radio("封面流光", IslandMaterial.AlbumGlow, s.Material, (x, v) => x.Material = v),
            ]),
            new("游戏中", Children:
            [
                Radio("性能优化显示", GamePresentation.Optimized, s.GamePresentation, (x, v) => x.GamePresentation = v),
                Radio("仅切歌时短暂出现", GamePresentation.PeekOnly, s.GamePresentation, (x, v) => x.GamePresentation = v),
                Radio("游戏中隐藏", GamePresentation.Hidden, s.GamePresentation, (x, v) => x.GamePresentation = v),
                Radio("完整效果（不优化）", GamePresentation.Full, s.GamePresentation, (x, v) => x.GamePresentation = v),
            ]),
            new("播放源", Children:
            [
                Radio("优先 Apple Music", PlayerSource.PreferAppleMusic, s.Source, (x, v) => x.Source = v),
                Radio("跟随系统当前媒体", PlayerSource.SystemCurrent, s.Source, (x, v) => x.Source = v),
                Radio("仅 Apple Music", PlayerSource.AppleMusicOnly, s.Source, (x, v) => x.Source = v),
                Radio("仅 Spotify", PlayerSource.SpotifyOnly, s.Source, (x, v) => x.Source = v),
                Radio("仅网易云音乐", PlayerSource.NeteaseOnly, s.Source, (x, v) => x.Source = v),
                Radio("仅 QQ 音乐", PlayerSource.QqMusicOnly, s.Source, (x, v) => x.Source = v),
                Radio("仅酷狗音乐", PlayerSource.KugouOnly, s.Source, (x, v) => x.Source = v),
            ]),
            TrayIcon.MenuItem.Separator,
            new("设置…", OpenSettings),
            new("开机自启", () => StartupService.SetEnabled(!StartupService.IsEnabled), Checked: StartupService.IsEnabled),
            TrayIcon.MenuItem.Separator,
            new("退出", Quit),
        ];
    }

    public void OpenSettings()
    {
        try
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow(Settings, Hotkeys, Lyrics);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Activate();
            _settingsWindow.BringToFront();
        }
        catch (Exception ex)
        {
            Log.Error("Opening settings failed", ex);
            Log.Error(ex.ToString());
        }
    }

    private void Quit()
    {
        Log.Info("Exiting");
        _activateWait?.Unregister(null);
        _settingsWindow?.Close();
        Hotkeys.Dispose();
        Settings.Changed -= ApplyPanelVisibility;
        _tray.Dispose();
        Media.Dispose();
        Settings.SaveNow();
        _island?.Shutdown();
        _messageWindow.Dispose();
        try { File.Delete(RunningMarker); } catch { }
        _instanceMutex?.ReleaseMutex();
        Exit();
    }
}
