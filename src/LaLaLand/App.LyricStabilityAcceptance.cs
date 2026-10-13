#if DEBUG
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using LaLaLand.Controls;
using LaLaLand.Models;
using LaLaLand.Services;
using LaLaLand.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LaLaLand;

public partial class App
{
    private async Task RunLyricStabilityAcceptanceAsync(string mode)
    {
        var checks = new List<string>();
        var report = new Dictionary<string, object?>();
        SettingsWindow? settingsWindow = null;
        IslandWindow? island = null;
        void Check(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        try
        {
            if (mode == "status")
            {
                const string message = "部分歌词来源暂时不可用，未找到可靠匹配；可重试或导入本地歌词";
                foreach (double width in new[] { 340.0, 400.0, 600.0 })
                foreach (double inset in new[] { 0.0, 160.0 })
                {
                    var panel = new LyricsPanel { Width = width, Height = 236 };
                    panel.SetMessageInset(inset);
                    panel.ShowMessage(message);
                    panel.Measure(new Size(width, 236));
                    panel.Arrange(new Rect(0, 0, width, 236));
                    var text = Field<TextBlock>(panel, "_message");
                    Check($"long status wraps at width {width}, inset {inset}", text.TextWrapping != TextWrapping.NoWrap && text.ActualHeight > 28);
                    Check($"status has readable margins at width {width}, inset {inset}", text.ActualWidth <= width - inset - 40 && text.ActualHeight <= 204);
                }
                var delayed = new MediaWorkerState(null, false, true, 1, TimeSpan.FromSeconds(10).Ticks,
                    Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 0.2), TimeSpan.FromMinutes(3).Ticks, null, false, null);
                delayed = JsonSerializer.Deserialize<MediaWorkerState>(JsonSerializer.Serialize(delayed, MediaWorker.Json), MediaWorker.Json)!;
                typeof(MediaService).GetMethod("ApplyWorkerState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, new object[] { delayed });
                Check("pipe delay does not make lyric interpolation lag", Media.Position.TotalSeconds >= 10.19 && Media.Position.TotalSeconds < 10.5);
                var art = await MakeAcceptanceArtworkAsync(transparent: false);
                var decoded = JsonSerializer.Deserialize<MediaWorkerState>(JsonSerializer.Serialize(delayed with { ArtworkChanged = true, Artwork = art }, MediaWorker.Json), MediaWorker.Json)!;
                Check("artwork and RGB palette roundtrip without native objects", decoded.Artwork!.LyricPalette.SequenceEqual(art!.LyricPalette) && decoded.Artwork.BackdropPixels.SequenceEqual(art.BackdropPixels));
            }
            else if (mode == "lookup")
            {
                Settings.Current.LyricsEnabled = true;
                Settings.Current.HideWhenIdle = false;
                Settings.Current.PeekOnTrackChange = false;
                using var handler = new LookupAcceptanceHandler();
                using var client = new HttpClient(handler);
                var service = new LyricsService(client, Path.Combine(PanelAcceptanceDirectory!, "lookup-cache"));
                island = new IslandWindow(Settings, Media, service, AppleMusic, Volume);
                var track = new TrackInfo("Where's My Love", "SYML", "Syml", TimeSpan.FromSeconds(218), "LaLaLand.Acceptance");
                void Publish(TrackInfo song, double duration, double position = 0) =>
                    typeof(MediaService).GetMethod("ApplyWorkerState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media,
                        new object[] { new MediaWorkerState(song, true, false, 1, TimeSpan.FromSeconds(position).Ticks,
                            Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(duration).Ticks, song.SourceAppId, false, null) });
                Publish(track, 218);
                await Task.Delay(900);
                Check("previous song duration produces the reported missing lyric state", Field<Lyrics>(island, "_lyrics").IsEmpty && handler.RequestCount > 0);
                Publish(track with { Duration = TimeSpan.FromSeconds(202) }, 202);
                await Task.Delay(1000);
                Check("late corrected duration retrieves lyrics without changing song", !Field<Lyrics>(island, "_lyrics").IsEmpty);
                int requests = handler.RequestCount;
                for (int tick = 1; tick <= 20; tick++) Publish(track with { Duration = TimeSpan.FromSeconds(202) }, 202, tick);
                await Task.Delay(600);
                Check("ordinary timeline updates do not repeat lookup", handler.RequestCount == requests);
                report["requestCount"] = handler.RequestCount;
            }
            else
            {
                bool withMedia = Environment.GetEnvironmentVariable("LALALAND_STABILITY_WITH_MEDIA") == "1";
                Settings.Current.IsIslandVisible = withMedia;
                Settings.Current.HideWhenIdle = false;
                Settings.Current.LyricsEnabled = false;
                if (withMedia)
                {
                    await Media.InitializeAsync();
                    await Task.Delay(1000);
                    Check("real media session is attached", Media.HasSession);
                }
                report["withMedia"] = withMedia;
                report["mediaTrack"] = Media.Track;
                settingsWindow = new SettingsWindow(Settings, Hotkeys, Lyrics);
                island = new IslandWindow(Settings, Media, Lyrics, AppleMusic, Volume);
                var chinese = Field<ComboBox>(settingsWindow, "LyricChineseFontBox");
                var choices = chinese.Items.Cast<ComboBoxItem>().Where(i =>
                    ((string)i.Tag).Contains("寒蝉", StringComparison.OrdinalIgnoreCase) ||
                    (string)i.Tag is "Microsoft YaHei" or "微软雅黑" or "Segoe UI" or "Eras Demi ITC").ToArray();
                Check("installed reproduction fonts are available", choices.Length > 2);
                var fixture = LrcParser.Parse("[00:00.00]Cheap Hotel，烟花易冷 日本語 한국어\n[00:10.00]Someday or One Day，想见你", "[00:00.00]测试中文译文\n[00:10.00]字体切换测试", "stability-fixture");
                typeof(IslandWindow).GetField("_lyrics", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(island, fixture);
                typeof(IslandWindow).GetMethod("ShowLyricsContent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(island, null);
                typeof(IslandWindow).GetMethod("SetPinned", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(island, new object[] { true });
                typeof(IslandWindow).GetMethod("GoTo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(island, new object[] { IslandState.Full });
                report["fontChoices"] = choices.Select(i => (string)i.Tag).ToArray();
                for (int cycle = 0; cycle < 120; cycle++)
                {
                    chinese.SelectedItem = choices[cycle % choices.Length];
                    var flow = Field<LyricFlowPanel>(island, "LyricFlow");
                    flow.SetActive(cycle % 2, TimeSpan.FromSeconds(cycle % 2 * 10 + 0.5), true);
                    await Task.Delay(16);
                    if (withMedia)
                    {
                        typeof(MediaService).GetMethod("UpdatePlaybackInfo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, null);
                        typeof(MediaService).GetMethod("UpdateTimeline", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, null);
                        await (Task)typeof(MediaService).GetMethod("RefreshMetadataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, null)!;
                    }
                    if (cycle % 5 == 0)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                    report["completedCycles"] = cycle + 1;
                    File.WriteAllText(Path.Combine(PanelAcceptanceDirectory!, "lyric-stability-progress.json"), JsonSerializer.Serialize(report));
                }
                Check("all real font selection events survive collection and rendering", (int)report["completedCycles"]! == 120);
                report["artworkPresent"] = Media.Artwork is not null;
                if (Media.Artwork is { } cover)
                    Check("cover pixels and palette survive the worker pipe", cover.Encoded.Length > 0 && cover.BackdropPixels.Length == cover.BackdropSize * cover.BackdropSize * 4 && cover.LyricPalette.All(c => c.A == 255));
                if (Environment.GetEnvironmentVariable("LALALAND_STABILITY_CRASH_MEDIA") == "1")
                {
                    int? previous = Media.WorkerProcessId;
                    Check("native monitor belongs to a different process", previous.HasValue && previous != Environment.ProcessId);
                    await Media.InjectNativeFailureForTestAsync();
                    for (int attempt = 0; attempt < 30 && (Media.WorkerProcessId is null || Media.WorkerProcessId == previous || !Media.HasSession); attempt++)
                        await Task.Delay(100);
                    Check("native heap failure preserves UI and reconnects monitoring", Media.WorkerProcessId.HasValue && Media.WorkerProcessId != previous && Media.HasSession);
                    Check("main process owns no native SMTC session or manager", Field<object?>(Media, "_session") is null && Field<object?>(Media, "_manager") is null);
                }
                island.Shutdown();
                island = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Check("window release survives collection", true);
            }
            report["passed"] = true;
        }
        catch (Exception ex) { report["passed"] = false; report["error"] = ex.ToString(); }
        finally
        {
            island?.Shutdown();
            settingsWindow?.Close();
            report["checks"] = checks;
            File.WriteAllText(Path.Combine(PanelAcceptanceDirectory!, "lyric-stability-acceptance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Quit();
        }
    }

    private sealed class LookupAcceptanceHandler : HttpMessageHandler
    {
        public int RequestCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestCount++;
            if (request.RequestUri!.Host != "lrclib.net")
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            var candidate = new { id = 4042785, trackName = "Where's My Love", artistName = "SYML", albumName = "Syml", duration = 202,
                syncedLyrics = "[00:10.00]integration fixture", plainLyrics = "", instrumental = false };
            object body = request.RequestUri.AbsolutePath.EndsWith("/search") ? new object[] { candidate } : candidate;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }
}
#endif
