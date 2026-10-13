#if DEBUG
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LaLaLand.Interop;
using LaLaLand.Models;
using LaLaLand.Services;
using LaLaLand.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace LaLaLand;

// Windows integration checks: run the real windows, message pump, settings and SMTC lifecycle in isolation.
public partial class App
{
    private static string? PanelAcceptanceDirectory =>
        Environment.GetEnvironmentVariable("LALALAND_PANEL_ACCEPTANCE_DIR") is { Length: > 0 } path ? path : null;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    private async Task RunPanelAcceptanceAsync()
    {
        var checks = new List<string>();
        var report = new Dictionary<string, object?>();
        void Check(string name, bool condition)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            checks.Add(name);
        }
        void CheckHidden()
        {
            Check("hidden state has no media worker", Media.WorkerProcessId is null);
            Check("hidden panel owns no island window", _island is null && !Settings.Current.IsIslandVisible);
            Check("media subscriptions and sessions released", !Field<bool>(Media, "_monitoring") &&
                Field<object?>(Media, "_manager") is null && Field<object?>(Media, "_session") is null);
            Check("media debounce/recheck timers stopped", !Field<DispatcherQueueTimer>(Media, "_metadataDebounce").IsRunning &&
                !Field<DispatcherQueueTimer>(Media, "_artworkRecheck").IsRunning);
            Check("artwork cache released", Media.Artwork is null && Media.Track is null);
            Check("all global hotkeys remain registered", Field<Dictionary<int, HotkeyAction>>(Hotkeys, "_registered").Count == Settings.Current.Hotkeys.Count);
        }
        void CheckReleased(IslandWindow island)
        {
            Check("old HWND destroyed", !IsWindow(Field<IntPtr>(island, "_hwnd")));
            Check("closed panel releases foreground settings listener", Field<Action?>(Settings, "LyricPaintChanged")?.GetInvocationList()
                .All(listener => !ReferenceEquals(listener.Target, island)) ?? true);
            Check("all island timers stopped", island.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(DispatcherQueueTimer))
                .All(f => f.GetValue(island) is not DispatcherQueueTimer timer || !timer.IsRunning));
            Check("audio capture stopped", !Field<AudioAnalyzer>(island, "_analyzer").IsRunning);
            Check("foreground hook released", Field<IntPtr>(island, "_foregroundHook") == IntPtr.Zero);
            Check("backdrop compositor released", Field<object?>(Field<object>(island, "_backdrop"), "_compositor") is null);
        }
        try
        {
            CheckHidden();
            string? commandSource = await Media.GetControlSourceAppIdAsync();
            Check("hidden player lookup keeps its worker temporary", Media.WorkerProcessId is null && !Field<bool>(Media, "_monitoring"));
            report["hiddenControlSource"] = commandSource;
            await PressHotkeyAsync(HotkeyAction.ToggleIsland);
            Check("registered hotkey restores visible panel", _island is not null && IsWindowVisible(Field<IntPtr>(_island, "_hwnd")));
            Check("restoring panel resumes media monitoring", Field<bool>(Media, "_monitoring"));

            OpenSettings();
            var toggle = Field<ToggleSwitch>(_settingsWindow!, "HidePanelToggle");
            Check("settings switch starts synchronized", !toggle.IsOn);
            var oldIsland = _island!;
            report["mediaSnapshot"] = new { Media.SourceAppId, Title = Media.Track?.Title, Artist = Media.Track?.Artist,
                Album = Media.Track?.Album, DurationSeconds = Media.Duration.TotalSeconds, PositionSeconds = Media.Position.TotalSeconds };
            // Freeze external SMTC callbacks before installing the synthetic recording. Real player's asynchronous
            // metadata/timeline updates must not replace fixture identity while local import awaits file IO.
            typeof(MediaService).GetMethod("SuspendMonitoring", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, null);
            await Task.Delay(200);
            typeof(MediaService).GetField("_monitoring", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Media, true);
            // Synthetic playback state drives real frame clocks and WASAPI capture without changing any player.
            typeof(MediaService).GetProperty(nameof(MediaService.Track))!.SetValue(Media,
                new TrackInfo("Panel lifecycle fixture", "LaLaLand", "Acceptance", TimeSpan.FromMinutes(3), "LaLaLand.Acceptance"));
            typeof(MediaService).GetProperty(nameof(MediaService.Duration))!.SetValue(Media, TimeSpan.FromMinutes(3));
            typeof(MediaService).GetProperty(nameof(MediaService.IsPlaying))!.SetValue(Media, true);
            Field<Action?>(Media, "TrackChanged")?.Invoke();
            Field<Action?>(Media, "PlaybackStateChanged")?.Invoke();
            var fixtureTrack = Media.Track!;
            var songOffset = Field<NumberBox>(_settingsWindow!, "SongOffsetBox");
            var songDrift = Field<NumberBox>(_settingsWindow!, "SongDriftBox");
            Check("current-song calibration controls enabled", songOffset.IsEnabled && songDrift.IsEnabled);
            songOffset.Value = 1500;
            songDrift.Value = 1;
            var calibration = Settings.Current.SongLyricCalibrations[LyricCalibration.Key(fixtureTrack)];
            Check("XAML controls persist song advance and drift", calibration.OffsetMs == 1500 && Math.Abs(calibration.TimeScale - 1.01) < 0.00001);
            var position = Media.Position;
            var lyricClock = (TimeSpan)oldIsland.GetType().GetProperty("LyricClock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(oldIsland)!;
            Check("island clock applies live song calibration", (lyricClock - calibration.Apply(position, Settings.Current.LyricOffsetMs)).Duration() < TimeSpan.FromMilliseconds(100));
            var lyricFile = Path.Combine(PanelAcceptanceDirectory!, "fixture.lrc");
            await File.WriteAllTextAsync(lyricFile, "[00:01.00]local acceptance fixture");
            await Lyrics.ImportAsync(fixtureTrack, lyricFile, CancellationToken.None);
            await Task.Delay(100);
            Check("local import updates real settings controls", Field<Button>(_settingsWindow!, "RemoveLocalLyricsButton").Visibility == Microsoft.UI.Xaml.Visibility.Visible &&
                !Field<Button>(_settingsWindow!, "RefreshLyricsButton").IsEnabled);
            Check("cache clearing keeps local lyric override", Lyrics.HasLocalLyrics(fixtureTrack));
            Lyrics.ClearCache();
            Check("online cache clearing preserves local import", Lyrics.HasLocalLyrics(fixtureTrack));
            Settings.SaveNow();
            var lyricReload = new SettingsService(_dispatcher);
            Check("song calibration survives actual settings reload", lyricReload.Current.SongLyricCalibrations[LyricCalibration.Key(fixtureTrack)] == calibration);
            await Task.Delay(500);
            // Process loopback of this fixture process exercises actual WASAPI without relying on the default
            // output device or changing any player. The production shutdown must stop this capture thread too.
            Field<AudioAnalyzer>(oldIsland, "_analyzer").Start((uint)Environment.ProcessId);
            await Task.Delay(250);
            Check("fixture starts real audio capture", Field<AudioAnalyzer>(oldIsland, "_analyzer").Mode is not null);
            Check("fixture starts the animation frame clock", Field<DispatcherQueueTimer>(oldIsland, "_frameTimer").IsRunning);
            var captureThread = Field<Thread>(Field<AudioAnalyzer>(oldIsland, "_analyzer"), "_thread");
            toggle.IsOn = true; // Drives the actual XAML Toggled handler.
            await Task.Delay(200);
            CheckHidden();
            CheckReleased(oldIsland);
            Check("hidden panel disables per-song lyric controls", !songOffset.IsEnabled && !songDrift.IsEnabled);
            Check("capture thread has exited", !captureThread.IsAlive);

            await PressHotkeyAsync(HotkeyAction.ToggleLyrics);
            CheckHidden();
            Check("lyrics hotkey never reveals a hidden panel", _island is null);
            await PressHotkeyAsync(HotkeyAction.ToggleIsland);
            Check("shortcut synchronizes open settings switch", !toggle.IsOn && _island is not null);
            _settingsWindow!.Close();

            for (int i = 0; i < 4; i++)
            {
                oldIsland = _island!;
                await PressHotkeyAsync(HotkeyAction.ToggleIsland);
                CheckHidden();
                CheckReleased(oldIsland);
                await PressHotkeyAsync(HotkeyAction.ToggleIsland);
                Check("repeated restore recreates panel", _island is not null);
            }

            // Race an unfinished manager request / artwork fetch against repeated enable/disable operations.
            for (int i = 0; i < 17; i++)
                TogglePanelVisibility();
            await Task.Delay(800);
            CheckHidden();
            await Media.GetControlSourceAppIdAsync(); // Read-only on-demand lookup; never controls the user's music.
            CheckHidden();
            Check("on-demand command lookup does not restart monitoring", !Field<bool>(Media, "_monitoring"));
            Settings.SaveNow();
            var reloaded = new SettingsService(_dispatcher);
            Check("hidden setting survives reload", !reloaded.Current.IsIslandVisible);

            // Measure after one-shot save/trim and canceled async work have settled; no ongoing timers are used.
            await Task.Delay(1500);
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            await Task.Delay(5000);
            process.Refresh();
            report["hiddenIdleSample"] = new
            {
                seconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                workingSetBytes = process.WorkingSet64,
                privateBytes = process.PrivateMemorySize64,
            };
            CheckHidden();
            report["passed"] = true;
        }
        catch (Exception ex)
        {
            report["passed"] = false;
            report["error"] = ex.ToString();
        }
        finally
        {
            report["checks"] = checks;
            File.WriteAllText(Path.Combine(PanelAcceptanceDirectory!, "panel-acceptance.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Quit();
        }
    }

    private async Task PressHotkeyAsync(HotkeyAction action)
    {
        int id = Field<Dictionary<int, HotkeyAction>>(Hotkeys, "_registered").Single(p => p.Value == action).Key;
        if (!Native.PostMessage(_messageWindow.Handle, Native.WM_HOTKEY, (IntPtr)id, IntPtr.Zero))
            throw new InvalidOperationException("Posting the registered hotkey failed.");
        await Task.Delay(200);
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.GetValue(owner)!;
}
#endif
