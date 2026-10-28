using System.Net;
using System.Text;
using System.Text.Json;
using LaLaLand.Models;
using LaLaLand.Services;
using LaLaLand.Controls;

internal static class Program
{
    private static readonly TrackInfo Track = new("Fixture Song", "Fixture Artist", "Fixture Album", TimeSpan.FromSeconds(180), "fixture");
    private static void Check(bool passed, string reason) { if (!passed) throw new InvalidOperationException(reason); }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static readonly List<HttpClient> Clients = [];
    private static readonly string TestDirectory = Path.Combine(Path.GetTempPath(), "LaLaLand-Lyrics-Tests", Guid.NewGuid().ToString("N"));
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--live-feedback")) return await LiveFeedbackAsync(args.Contains("--warm-cache"));
        if (args.Contains("--live") || args.Contains("--live-kugou") || args.Contains("--live-qq") || args.Contains("--live-someday"))
            return await LiveAsync(args.Contains("--live-kugou"), args.Contains("--live-qq"), args.Contains("--live-someday"));
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("offset tag anywhere shifts the whole song", () => Sync(() => {
                var lyrics = LrcParser.Parse("[00:10.00]fixture\n[offset:1000]", null, "fixture");
                Check(lyrics.Lines[0].Time == TimeSpan.FromSeconds(9), "offset after a line was ignored");
            })),
            ("enhanced LRC offsets shift words with their line", () => Sync(() => {
                var lyrics = LrcParser.Parse("[offset:1000]\n[00:10.00]<00:10.00>first<00:11.00> second", null, "fixture");
                Check(lyrics.Lines[0].Words![0].Start == TimeSpan.FromSeconds(9), "word highlighting retained the old timestamp");
            })),
            ("repeated enhanced LRC timestamps shift repeated words", () => Sync(() => {
                var lyrics = LrcParser.Parse("[00:10.00][00:30.00]<00:10.00>first<00:11.00> second", null, "fixture");
                Check(lyrics.Lines[1].Words![0].Start == TimeSpan.FromSeconds(30), "repeated chorus kept first-chorus word timings");
            })),
            ("duplicate timestamps preserve original and translation", () => Sync(() => {
                var lyrics = LrcParser.Parse("[00:10.00]original\n[00:10.00]翻译", null, "fixture");
                Check(lyrics.Lines.Count == 1 && lyrics.Lines[0].Text == "original" && lyrics.Lines[0].Translation == "翻译", "bilingual lines overwrote one another");
            })),
            ("different singer cannot win by word timing", async () => {
                var handler = new FixtureHandler(Track) { NeteaseSongs = [Song(1, Track.Title, "Other Artist", 180)], WordLyrics = true };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(lyrics.Source == "LRCLIB", "wrong-singer YRC outranked the correct song");
            }),
            ("live version cannot replace a studio recording", async () => {
                var handler = new FixtureHandler(Track) { NeteaseSongs = [Song(1, Track.Title + " (Live)", Track.Artist, 180)], WordLyrics = true };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(lyrics.Source == "LRCLIB", "live recording was treated as the studio version");
            }),
            ("another matching candidate is tried when top candidate has no lyrics", async () => {
                var handler = new FixtureHandler(Track) { NeteaseSongs = [Song(1, Track.Title, Track.Artist, 180), Song(2, Track.Title, Track.Artist, 180)], EmptyFirst = true, LrclibHit = false };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(!lyrics.IsEmpty && lyrics.Source == "网易云音乐", "only the first matching song was fetched");
            }),
            ("transient provider failure is not cached as a missing song", async () => {
                var handler = new FixtureHandler(Track) { Failing = true };
                var service = Service(handler);
                await service.GetAsync(Track, default);
                handler.Failing = false;
                var lyrics = await service.GetAsync(Track, default);
                Check(!lyrics.IsEmpty, "network failure poisoned the miss cache");
            }),
            ("recording duration participates in cache identity", async () => {
                var handler = new FixtureHandler(Track);
                var service = Service(handler);
                await service.GetAsync(Track, default);
                var longer = Track with { Duration = TimeSpan.FromSeconds(240) };
                handler.Track = longer;
                handler.Lrc = "[00:10.00]longer recording fixture";
                var lyrics = await service.GetAsync(longer, default);
                Check(lyrics.Lines[0].Text == "longer recording fixture", "another recording reused a shorter version's cache");
            }),
            ("Kugou supplies a matching recording when other providers miss", async () => {
                var handler = new FixtureHandler(Track) { LrclibHit = false, KugouHit = true };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(lyrics.Source == "酷狗音乐" && lyrics.IsSynced, "third-provider fallback failed");
            }),
            ("QQ Music supplies a matching recording when other providers miss", async () => {
                var handler = new FixtureHandler(Track) { LrclibHit = false, QqMusicHit = true };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(lyrics.Source == "QQ音乐" && lyrics.IsSynced, "QQ Music provider fallback failed");
            }),
            ("an exact endpoint response still verifies the artist", async () => {
                var handler = new FixtureHandler(Track with { Artist = "Wrong Singer" });
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(lyrics.IsEmpty, "exact endpoint returned unverified artist metadata");
            }),
            ("large recording duration mismatch is rejected", async () => {
                var handler = new FixtureHandler(Track with { Duration = TimeSpan.FromSeconds(300) });
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(lyrics.IsEmpty, "a different recording duration was accepted");
            }),
            ("invalid empty YRC falls back to readable LRC", async () => {
                var handler = new FixtureHandler(Track) { LrclibHit = false, NeteaseSongs = [Song(1, Track.Title, Track.Artist, 180)], InvalidYrc = true };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(!lyrics.IsEmpty && lyrics.Lines[0].Text == "original recording fixture", "empty word data concealed valid LRC");
            }),
            ("simplified and traditional metadata match the same Jay Chou recording", async () => {
                var expected = new TrackInfo("烟花易冷", "周杰伦", "跨时代", TimeSpan.FromSeconds(263), "fixture");
                var handler = new FixtureHandler(expected with { Title = "煙花易冷", Artist = "Jay Chou", Album = "跨時代" });
                var lyrics = await Service(handler).GetAsync(expected, default);
                Check(lyrics.Source == "LRCLIB", "traditional title or provider artist alias prevented matching");
            }),
            ("soundtrack subtitle does not conceal the original recording", async () => {
                var expected = new TrackInfo("Someday or One Day (電視劇《想見你》片頭曲)", "孫盛希", "電視劇《想見你》原聲帶", TimeSpan.FromSeconds(251), "fixture");
                var handler = new FixtureHandler(expected with { Title = "Someday or One Day", Artist = "孙盛希" });
                var lyrics = await Service(handler).GetAsync(expected, default);
                Check(!lyrics.IsEmpty && lyrics.IsSynced, "soundtrack description prevented the same title, singer and duration from matching");
            }),
            ("soundtrack subtitle retains live version rejection", async () => {
                var expected = new TrackInfo("Someday or One Day (電視劇《想見你》片頭曲)", "孫盛希", "", TimeSpan.FromSeconds(251), "fixture");
                var handler = new FixtureHandler(expected with { Title = "Someday or One Day (Live)", Artist = "孙盛希" });
                Check((await Service(handler).GetAsync(expected, default)).IsEmpty, "soundtrack cleanup accepted a live replacement");
            }),
            ("failed exact lookup still searches the same provider", async () => {
                var handler = new FixtureHandler(Track) { FailLrclibExact = true };
                Check(!(await Service(handler).GetAsync(Track, default)).IsEmpty, "an exact endpoint error abandoned working search results");
            }),
            ("one transient lookup outage recovers without a manual reload", async () => {
                var handler = new FixtureHandler(Track) { FailLrclibRequestsRemaining = 4 };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(!lyrics.IsEmpty && lyrics.IsSynced, "a recoverable outage left the song without lyrics");
            }),
            ("definitive provider misses are not retried", async () => {
                var handler = new FixtureHandler(Track) { LrclibHit = false };
                Check((await Service(handler).GetAsync(Track, default)).IsEmpty && handler.LrclibRequestCount == 4,
                    "a definitive miss triggered another provider pass");
            }),
            ("permanent HTTP errors are not retried", async () => {
                var handler = new FixtureHandler(Track) { FailLrclibRequestsRemaining = 100, FailureStatus = HttpStatusCode.Forbidden };
                Check((await Service(handler).GetAsync(Track, default)).IsEmpty && handler.LrclibRequestCount == 4,
                    "a permanent HTTP error triggered another provider pass");
            }),
            ("cancelling during retry backoff prevents another provider pass", async () => {
                var handler = new FixtureHandler(Track) { FailLrclibRequestsRemaining = 4 };
                using var cts = new CancellationTokenSource(50);
                bool cancelled = false;
                try { await Service(handler).GetAsync(Track, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && handler.LrclibRequestCount == 4, "cancellation did not stop the delayed retry");
            }),
            ("local override persists across online cache clearing", async () => {
                var handler = new FixtureHandler(Track) { Failing = true };
                var service = Service(handler);
                Directory.CreateDirectory(TestDirectory);
                var path = Path.Combine(TestDirectory, "fixture.lrc");
                await File.WriteAllTextAsync(path, "[00:03.00]local fixture");
                await service.ImportAsync(Track, path, default);
                service.ClearCache();
                var lyrics = await service.GetAsync(Track, default);
                Check(lyrics.Source == "本地歌词" && lyrics.Lines[0].Text == "local fixture", "cache clearing deleted the explicit local override");
                service.RemoveLocalLyrics(Track);
                Check(!service.HasLocalLyrics(Track) && File.Exists(path), "removing override damaged original file");
            }),
            ("cancelled requests cannot write a missing-song cache", async () => {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                var handler = new FixtureHandler(Track);
                var service = Service(handler);
                bool cancelled = false;
                try { await service.GetAsync(Track, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !(await service.GetAsync(Track, default)).IsEmpty, "cancellation was swallowed or poisoned cache");
            }),
            ("single-song advance and drift have an exact inverse for seeking", () => Sync(() => {
                var calibration = new LyricCalibration(1500, 1.01);
                var clock = calibration.Apply(TimeSpan.FromSeconds(100), 500);
                Check(clock == TimeSpan.FromSeconds(103), "advance/drift sign is incorrect");
                Check((calibration.ToMedia(clock, 500) - TimeSpan.FromSeconds(100)).Duration() < TimeSpan.FromMilliseconds(0.01), "line seeking didn't invert calibration");
                Check((calibration.RealDelay(TimeSpan.FromSeconds(2.02), 2) - TimeSpan.FromSeconds(1)).Duration() < TimeSpan.FromMilliseconds(0.01), "next-line scheduling ignored speed or drift");
            })),
            ("single-song calibration survives serialization and stays recording-specific", () => Sync(() => {
                var key = LyricCalibration.Key(Track);
                var values = new Dictionary<string, LyricCalibration> { [key] = new(1700, 1.005) };
                var restored = JsonSerializer.Deserialize<Dictionary<string, LyricCalibration>>(JsonSerializer.Serialize(values))!;
                Check(restored[key].OffsetMs == 1700 && restored[key].TimeScale == 1.005, "calibration was not persisted");
                Check(key != LyricCalibration.Key(Track with { Duration = TimeSpan.FromSeconds(240) }), "calibration leaked into a different recording");
            })),
            ("translation timestamps tolerate small rounding differences", () => Sync(() => {
                var lyrics = LrcParser.Parse("[00:10.10]original", "[00:10.00]翻译", "fixture");
                Check(lyrics.Lines[0].Translation == "翻译", "slight timestamp difference lost the translation");
            })),
            ("in-flight network cancellation releases lookup and leaves it retryable", async () => {
                using var cts = new CancellationTokenSource(30);
                var handler = new FixtureHandler(Track) { DelayMs = 200 };
                var service = Service(handler);
                bool cancelled = false;
                try { await service.GetAsync(Track, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
                handler.DelayMs = 0;
                Check(cancelled && !(await service.GetAsync(Track, default)).IsEmpty, "in-flight cancellation wasn't propagated or blocked retry");
            }),
            ("a credited artist cannot disguise a different primary singer", async () => {
                var handler = new FixtureHandler(Track) { LrclibHit = false, NeteaseSongs = [Song(1, Track.Title, "Other Artist / " + Track.Artist, 180)], WordLyrics = true };
                Check((await Service(handler).GetAsync(Track, default)).IsEmpty, "different primary singer was accepted from overlapping credits");
            }),
            ("plain exact match can be upgraded to synced search lyrics", async () => {
                var handler = new FixtureHandler(Track) { LrclibExactPlain = true };
                Check((await Service(handler).GetAsync(Track, default)).IsSynced, "plain exact result prevented a synced search result");
            }),
            ("available plain lyrics survive a failed synced search", async () => {
                var handler = new FixtureHandler(Track) { LrclibExactPlain = true, FailLrclibSearch = true };
                var lyrics = await Service(handler).GetAsync(Track, default);
                Check(!lyrics.IsEmpty && !lyrics.IsSynced, "failed search discarded the available plain lyric result");
            }),
            ("bottom-anchored wrapping preserves reading order and variable circle slots", () => Sync(() => {
                var random = new Random(42);
                for (int sample = 0; sample < 100; sample++)
                {
                    var tokens = Enumerable.Range(0, random.Next(1, 50)).Select(i => new Token(i.ToString(), i, 1, random.Next(10, 80), 5)).ToArray();
                    var x = new double[tokens.Length];
                    var rows = new int[tokens.Length];
                    (double Left, double Width) Slot(int bottomRow) => (bottomRow % 3 * 20, 100 + bottomRow % 4 * 25);
                    int count = TokenLayout.WrapFromBottom(tokens, Slot, x, rows);
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        var slot = Slot(count - rows[i] - 1);
                        Check(i == 0 || rows[i] >= rows[i - 1], "bottom-up layout reversed source reading order");
                        Check(x[i] >= slot.Left && x[i] + tokens[i].Width <= slot.Left + slot.Width, "token violated its row's circle slot");
                        if (i > 0 && rows[i] == rows[i - 1]) Check(x[i] == x[i - 1] + tokens[i - 1].Width + tokens[i - 1].Gap, "in-row gaps changed");
                    }
                }
            })),
            ("empty bottom-anchored text occupies no rows", () => Sync(() => {
                Check(TokenLayout.WrapFromBottom([], _ => (0, 100), [], []) == 0, "empty translation created a phantom row");
            })),
            ("mixed lyric fonts keep Latin and East Asian scripts independent", () => Sync(() => {
                var runs = LyricFontScripts.Split("烟花Cheap Hotel 2026，易冷").ToArray();
                Check(runs.Length == 3 && runs[0].EastAsian && !runs[1].EastAsian && runs[2].EastAsian,
                    "English embedded in Chinese did not receive its own font");
                Check(runs[1].Text == "Cheap Hotel 2026", "Latin words, spaces or digits changed");
            })),
            ("Japanese and Korean including half-width and decomposed forms follow Chinese", () => Sync(() => {
                foreach (string text in new[] { "日本語あいうアイウ", "한국어", "한", "ﾊﾝｸﾞﾙ" })
                    Check(LyricFontScripts.Split(text).All(r => r.EastAsian), "an East Asian script followed the Latin font");
            })),
            ("font segmentation preserves supplementary Han and timing character indices", () => Sync(() => {
                const string text = "A𠀀\U000E0100B🙂";
                var runs = LyricFontScripts.Split(text).ToArray();
                Check(string.Concat(runs.Select(r => r.Text)) == text, "segmentation changed the timed UTF-16 text");
                Check(runs.Length == 3 && runs[1].EastAsian && runs[1].Text.Length == 4, "Han variation sequence was split or assigned to Latin");
            })),
            ("Latin accents stay with their base and empty text creates no runs", () => Sync(() => {
                var runs = LyricFontScripts.Split("中Cafe\u0301").ToArray();
                Check(runs.Length == 2 && !runs[1].EastAsian && runs[1].Text == "Cafe\u0301", "accented Latin text was split");
                Check(!LyricFontScripts.Split("").Any(), "empty text created a font run");
            })),
        };
        int failed = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
        foreach (var client in Clients) client.Dispose();
        if (Directory.Exists(TestDirectory)) Directory.Delete(TestDirectory, recursive: true);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> LiveFeedbackAsync(bool warmCache)
    {
        TrackInfo[] tracks = [
            new("What Are Words (feat. Peter Hollens & Evynne Hollens)", "The Piano Guys", "What Are Words (feat. Peter Hollens & Evynne Hollens) - Single", TimeSpan.FromSeconds(218), "feedback"),
            new("are you sure?", "Loote", "lost - EP", TimeSpan.FromSeconds(205), "feedback"),
            new("Overwhelmed (Ryan Mack Remix)", "Ryan Mack", "Overwhelmed (Ryan Mack Remix) - Single", TimeSpan.FromSeconds(103), "feedback"),
            new("Where's My Love", "SYML", "Syml", TimeSpan.FromSeconds(202), "feedback"),
            new("Our Own Heaven", "Masetti", "Dreamer", TimeSpan.FromSeconds(235), "feedback"),
        ];
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LaLaLand/0.1 (Windows desktop lyrics)");
        var service = new LyricsService(client, warmCache ? null : Path.Combine(TestDirectory, "feedback"));
        var results = new List<object>();
        int failed = 0;
        foreach (var track in tracks)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var lyrics = await service.GetAsync(track, default);
            if (lyrics.IsEmpty || !lyrics.IsSynced) failed++;
            results.Add(new { track.Title, track.Artist, Seconds = track.Duration.TotalSeconds, Found = !lyrics.IsEmpty,
                lyrics.Source, lyrics.IsSynced, lyrics.HasWordTiming, LineCount = lyrics.Lines.Count, lyrics.Match,
                Message = service.LastLookupMessage, ElapsedMilliseconds = timer.ElapsedMilliseconds });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = failed == 0, Failed = failed, Results = results },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (Directory.Exists(TestDirectory)) Directory.Delete(TestDirectory, recursive: true);
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> LiveAsync(bool kugouOnly, bool qqOnly, bool someday = false)
    {
        var track = someday ? new TrackInfo("Someday or One Day (電視劇《想見你》片頭曲)", "孫盛希", "電視劇《想見你》原聲帶", TimeSpan.FromSeconds(251), "live-check")
            : kugouOnly ? new TrackInfo("Hello", "Adele", "25", TimeSpan.FromSeconds(295), "live-check")
            : new TrackInfo("烟花易冷", "周杰伦", "跨时代", TimeSpan.FromSeconds(263.235918), "live-check");
        var directory = Path.Combine(TestDirectory, "live");
        using var client = kugouOnly
            ? new HttpClient(new ProviderOnlyHandler("lyrics.kugou.com"))
            : qqOnly ? new HttpClient(new ProviderOnlyHandler("c.y.qq.com")) : new HttpClient();
        var service = new LyricsService(client, directory);
        var lyrics = await service.GetAsync(track, default);
        Console.WriteLine(JsonSerializer.Serialize(new {
            Track = track.Title, Artist = track.Artist, Album = track.Album, Source = lyrics.Source, lyrics.IsSynced,
            lyrics.HasWordTiming, LineCount = lyrics.Lines.Count, lyrics.Match,
            FirstTimestampSeconds = lyrics.Lines.FirstOrDefault()?.Time.TotalSeconds,
            LastTimestampSeconds = lyrics.Lines.LastOrDefault()?.Time.TotalSeconds,
            Message = service.LastLookupMessage,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        // The live artifact deliberately contains only metadata, never lyric text or provider access keys.
        if (Directory.Exists(TestDirectory)) Directory.Delete(TestDirectory, recursive: true);
        return !lyrics.IsEmpty && lyrics.IsSynced ? 0 : 1;
    }

    private sealed class ProviderOnlyHandler(string host) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Host == host ? base.SendAsync(request, ct) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static object Song(int id, string title, string artist, double seconds) =>
        new { id, name = title, artists = new[] { new { name = artist } }, album = new { name = Track.Album }, duration = seconds * 1000 };

    private static LyricsService Service(FixtureHandler handler)
    {
        var client = new HttpClient(handler);
        Clients.Add(client);
        return new LyricsService(client, Path.Combine(TestDirectory, Guid.NewGuid().ToString("N")));
    }

    private sealed class FixtureHandler(TrackInfo track) : HttpMessageHandler
    {
        public TrackInfo Track = track;
        public object[] NeteaseSongs = [];
        public bool WordLyrics, EmptyFirst, Failing, KugouHit, QqMusicHit, InvalidYrc;
        public bool LrclibHit = true;
        public bool LrclibExactPlain, FailLrclibSearch, FailLrclibExact;
        public int FailLrclibRequestsRemaining;
        public int LrclibRequestCount;
        public HttpStatusCode FailureStatus = HttpStatusCode.ServiceUnavailable;
        public int DelayMs;
        public string Lrc = "[00:10.00]original recording fixture";
        private int _lyricCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            if (Failing) throw new HttpRequestException("fixture transient failure");
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("lrclib.net"))
            {
                LrclibRequestCount++;
                if (FailLrclibRequestsRemaining > 0)
                {
                    FailLrclibRequestsRemaining--;
                    throw new HttpRequestException("fixture provider failure", null, FailureStatus);
                }
            }
            if (FailLrclibSearch && url.Contains("lrclib.net/api/search")) throw new HttpRequestException("fixture search failure");
            if (FailLrclibExact && url.Contains("lrclib.net/api/get")) throw new HttpRequestException("fixture exact failure");
            object body;
            if (url.Contains("music.163.com") && url.Contains("search"))
                body = new { result = new { songs = NeteaseSongs } };
            else if (url.Contains("music.163.com") && url.Contains("lyric"))
            {
                bool empty = EmptyFirst && (request.Method == HttpMethod.Post ? ++_lyricCalls == 1 : url.Contains("id=1&"));
                body = new { lrc = new { lyric = empty ? "" : Lrc }, yrc = new { lyric = InvalidYrc ? "[10000,2000]" : !empty && WordLyrics ? "[10000,2000](10000,2000,0)word fixture" : "" } };
            }
            else if (url.Contains("lrclib.net"))
            {
                var candidate = new { id = 1, trackName = Track.Title, artistName = Track.Artist, albumName = Track.Album, duration = Track.Duration.TotalSeconds, syncedLyrics = LrclibExactPlain && !url.Contains("/search") ? "" : Lrc, plainLyrics = "fixture", instrumental = false };
                body = url.Contains("/search") ? (LrclibHit ? new object[] { candidate } : Array.Empty<object>()) : LrclibHit ? candidate : new { };
            }
            else if (url.Contains("kugou.com/download"))
                body = new { status = 200, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Lrc)) };
            else if (url.Contains("c.y.qq.com/soso"))
                body = new { code = 0, data = new { song = new { list = QqMusicHit
                    ? new object[] { new { mid = "qq-fixture", name = Track.Title, singer = new[] { new { name = Track.Artist } }, album = new { name = Track.Album }, interval = Track.Duration.TotalSeconds } }
                    : Array.Empty<object>() } } };
            else if (url.Contains("c.y.qq.com/lyric"))
                body = new { retcode = 0, code = 0, lyric = Lrc, trans = "" };
            else body = new { status = 200, candidates = KugouHit ? new object[] { new { id = "1", song = Track.Title, singer = Track.Artist, duration = Track.Duration.TotalMilliseconds, accesskey = "fixture" } } : Array.Empty<object>() };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}

namespace LaLaLand.Services
{
    public static class Log
    {
        public static void Info(string message) { }
        public static void Error(string message, Exception? ex = null) { }
    }
}
