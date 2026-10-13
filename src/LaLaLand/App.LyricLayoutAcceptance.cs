#if DEBUG
using System.Collections;
using System.Reflection;
using System.Text.Json;
using LaLaLand.Controls;
using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LaLaLand;

public partial class App
{
    private Task RunLyricLayoutAcceptanceAsync(string fixturePath)
    {
        var report = new Dictionary<string, object?>();
        var cases = new List<object>();
        LyricFlowPanel? panel = null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(fixturePath));
            var entry = json.RootElement;
            string Value(string name) => entry.TryGetProperty(name, out var value) ? value.ToString() : "";
            var lyrics = Value("Yrc") is { Length: > 0 } yrc ? YrcParser.Parse(yrc, Value("Translation"), Value("Source"))
                : LrcParser.Parse(Value("Lrc"), Value("Translation"), Value("Source"));
            if (lyrics.IsEmpty) throw new InvalidOperationException("Fixture contains no readable lyrics");
            report["fixture"] = new { Title = Value("Title"), Artist = Value("Artist"), LineCount = lyrics.Lines.Count };
            var font = Environment.GetEnvironmentVariable("LALALAND_LYRIC_LAYOUT_FONT") ?? "";
            report["font"] = font;
            var chineseFont = Environment.GetEnvironmentVariable("LALALAND_LYRIC_LAYOUT_CHINESE_FONT");
            report["chineseFont"] = chineseFont ?? font;
            bool obstacle = Environment.GetEnvironmentVariable("LALALAND_LYRIC_LAYOUT_NO_OBSTACLE") != "1";
            report["obstacle"] = obstacle;
            var relayout = typeof(LyricFlowPanel).GetMethod("Relayout", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var failures = 0;
            foreach (var width in new[] { 380.0, 420.0, 460.0 })
            foreach (var translation in new[] { true, false })
            {
                panel = new LyricFlowPanel { Width = width, Height = 236 };
                if (font.Length > 0 || chineseFont is not null) panel.SetFonts(LyricTypography.Family(chineseFont ?? font), LyricTypography.Family(font));
                panel.Measure(new Size(width, 236));
                panel.Arrange(new Rect(0, 0, width, 236));
                panel.SetObstacle(obstacle, 29.5, 12, 118);
                panel.SetLyrics(lyrics, translation);
                panel.SetLive(true);
                var sequence = Enumerable.Range(0, lyrics.Lines.Count).Concat(new[] {
                    lyrics.Lines.Count - 1, 0, lyrics.Lines.Count / 2, Math.Max(0, lyrics.Lines.Count / 2 - 1),
                    0, Math.Max(0, lyrics.Lines.Count - 3), Math.Max(0, lyrics.Lines.Count - 4) });
                foreach (int index in sequence)
                {
                    var before = CaptureTokenPositions(panel);
                    panel.SetActive(index, lyrics.Lines[index].Time, playing: false);
                    Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                    relayout.Invoke(panel, new object[] { false, 0.0 });
                    var after = CaptureTokenPositions(panel);
                    double initialJump = before.Where(p => after.ContainsKey(p.Key) && p.Value.Y is >= 0 and <= 236)
                        .Select(p => Distance(p.Value, after[p.Key])).DefaultIfEmpty().Max();
                    var positions = new List<double>();
                    var heights = new HashSet<double>();
                    int movingFrames = 0;
                    var previousFrame = after;
                    double maxFrameStep = 0;
                    bool transitionCircleClear = true;
                    for (int frame = 0; frame < 280; frame++)
                    {
                        bool moving = (bool)relayout.Invoke(panel, new object[] { false, 0.016 })!;
                        if (frame < 32)
                        {
                            var currentFrame = CaptureTokenPositions(panel);
                            foreach (var point in before.Where(p => p.Value.Y is >= 0 and <= 236))
                                if (previousFrame.TryGetValue(point.Key, out var previousPoint) && currentFrame.TryGetValue(point.Key, out var currentPoint))
                                    maxFrameStep = Math.Max(maxFrameStep, Distance(previousPoint, currentPoint));
                            transitionCircleClear &= !obstacle || RenderedClearOfCircle(panel, currentFrame);
                            previousFrame = currentFrame;
                        }
                        if (frame < 200) continue;
                        var views = ((IEnumerable)Field<object>(panel, "_lines")).Cast<object>().ToArray();
                        positions.Add(Field<double>(views[index], "Top") - Field<double>(panel, "_scroll"));
                        heights.Add(Field<double>(views[index], "Height"));
                        if (moving) movingFrames++;
                    }
                    var jitter = positions.Max() - positions.Min();
                    var finalViews = ((IEnumerable)Field<object>(panel, "_lines")).Cast<object>().ToArray();
                    double anchorError = Math.Abs(Field<double>(finalViews[index], "Top") - Field<double>(panel, "_scroll") +
                        Field<double>(finalViews[index], "Height") / 2 - 236 * 0.38);
                    bool circleClear = !obstacle || ClearOfCircle(panel, finalViews);
                    var settled = CaptureTokenPositions(panel);
                    double travel = before.Where(p => settled.ContainsKey(p.Key) && p.Value.Y is >= 0 and <= 236)
                        .Select(p => Distance(p.Value, settled[p.Key])).DefaultIfEmpty().Max();
                    bool smooth = before.Count == 0 || initialJump < 0.15 && maxFrameStep <= Math.Max(3, travel * 0.18);
                    bool passed = smooth && transitionCircleClear && jitter < 0.3 && heights.Count == 1 && movingFrames == 0 && anchorError < 0.3 && circleClear;
                    if (!passed) failures++;
                    cases.Add(new { width, translation, index, timestampSeconds = lyrics.Lines[index].Time.TotalSeconds,
                        jitterPixels = jitter, distinctHeights = heights.Count, movingFramesAfterSettling = movingFrames,
                        anchorErrorPixels = anchorError, circleClear, transitionCircleClear, initialJumpPixels = initialJump,
                        maxFrameStepPixels = maxFrameStep, travelPixels = travel, smooth, passed });
                }
                foreach (int index in new[] { 1, lyrics.Lines.Count / 2, lyrics.Lines.Count - 2 })
                {
                    panel.SetActive(index, lyrics.Lines[index].Time, false);
                    Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                    for (int frame = 0; frame < 8; frame++) relayout.Invoke(panel, new object[] { false, 0.016 });
                    var before = CaptureTokenPositions(panel);
                    int next = index + 1;
                    panel.SetActive(next, lyrics.Lines[next].Time, false);
                    Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                    relayout.Invoke(panel, new object[] { false, 0.0 });
                    var after = CaptureTokenPositions(panel);
                    double jump = before.Where(p => after.ContainsKey(p.Key) && p.Value.Y is >= 0 and <= 236)
                        .Select(p => Distance(p.Value, after[p.Key])).DefaultIfEmpty().Max();
                    for (int frame = 0; frame < 40; frame++) relayout.Invoke(panel, new object[] { false, 0.016 });
                    bool stopped = !(bool)relayout.Invoke(panel, new object[] { false, 0.016 })!;
                    bool circleClear = !obstacle || RenderedClearOfCircle(panel, CaptureTokenPositions(panel));
                    bool passed = jump < 0.15 && stopped && circleClear;
                    if (!passed) failures++;
                    cases.Add(new { interrupted = true, width, translation, index, initialJumpPixels = jump, stopped, circleClear, passed });
                }
                panel.SetActive(0, lyrics.Lines[0].Time, false);
                Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                relayout.Invoke(panel, new object[] { false, 0.064 });
                panel.SetLive(false);
                bool hiddenIdle = !Field<DispatcherQueueTimer>(panel, "_scrollTimer").IsRunning &&
                    !Field<LyricLayoutTransition>(panel, "_transition").IsMoving;
                if (!hiddenIdle) failures++;
                cases.Add(new { hiddenDuringTransition = true, width, translation, passed = hiddenIdle });
                panel.SetLive(true);
                foreach (double dt in new[] { 1.0 / 144, 1.0 / 30, 0.1 })
                {
                    panel.SetActive(1, lyrics.Lines[1].Time, false);
                    Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                    relayout.Invoke(panel, new object[] { false, 0.0 });
                    double elapsed = 0;
                    bool moving = true;
                    while (elapsed < 0.6 && moving)
                    {
                        moving = (bool)relayout.Invoke(panel, new object[] { false, dt })!;
                        elapsed += dt;
                    }
                    bool passed = !moving && elapsed <= LyricLayoutTransition.DurationSeconds + dt + 0.0001;
                    if (!passed) failures++;
                    cases.Add(new { frameIntervalSeconds = dt, width, translation, settledAfterSeconds = elapsed, passed });
                    panel.SetActive(0, lyrics.Lines[0].Time, false);
                    Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                    relayout.Invoke(panel, new object[] { true, 0.0 });
                }
                panel.SetLive(false);
                panel.Clear();
            }
            panel = new LyricFlowPanel { Width = 380, Height = 236 };
            if (font.Length > 0 || chineseFont is not null) panel.SetFonts(LyricTypography.Family(chineseFont ?? font), LyricTypography.Family(font));
            panel.Measure(new Size(380, 236));
            panel.Arrange(new Rect(0, 0, 380, 236));
            panel.SetObstacle(obstacle, 29.5, 12, 118);
            panel.SetLyrics(new Lyrics(lyrics.Lines, false, "plain-fixture"), true);
            panel.SetLive(true);
            foreach (double fraction in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                panel.SetPlainProgress(fraction);
                Field<DispatcherQueueTimer>(panel, "_scrollTimer").Stop();
                int movingFrames = 0;
                for (int frame = 0; frame < 280; frame++)
                    if ((bool)relayout.Invoke(panel, new object[] { false, 0.016 })! && frame >= 200) movingFrames++;
                var views = ((IEnumerable)Field<object>(panel, "_lines")).Cast<object>().ToArray();
                bool circleClear = !obstacle || ClearOfCircle(panel, views);
                bool passed = movingFrames == 0 && circleClear;
                if (!passed) failures++;
                cases.Add(new { plain = true, fraction, circleClear, movingFramesAfterSettling = movingFrames, passed });
            }
            report["failedCaseCount"] = failures;
            report["passed"] = failures == 0;
        }
        catch (Exception ex) { report["passed"] = false; report["error"] = ex.ToString(); }
        finally
        {
            panel?.SetLive(false);
            report["cases"] = cases;
            File.WriteAllText(Path.Combine(PanelAcceptanceDirectory!, "lyric-layout-acceptance.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Quit();
        }
        return Task.CompletedTask;
    }

    private static Dictionary<(int Line, bool Translation, int Token), (double X, double Y)> CaptureTokenPositions(LyricFlowPanel panel)
    {
        var result = new Dictionary<(int, bool, int), (double, double)>();
        var views = ((IEnumerable)Field<object>(panel, "_lines")).Cast<object>().ToArray();
        for (int i = 0; i < views.Length; i++)
        foreach (bool translation in new[] { false, true })
        {
            if (Field<SweepText?>(views[i], translation ? "Translation" : "Main") is not { } text) continue;
            var visuals = ((IEnumerable)Field<object>(text, "_visuals")).Cast<object>().ToArray();
            for (int token = 0; token < visuals.Length; token++)
            {
                double x = Field<float>(visuals[token], "LastX"), y = Field<float>(visuals[token], "LastY");
                if (double.IsFinite(x) && double.IsFinite(y)) result[(i, translation, token)] = (x, y);
            }
        }
        return result;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static bool RenderedClearOfCircle(LyricFlowPanel panel,
        Dictionary<(int Line, bool Translation, int Token), (double X, double Y)> positions)
    {
        double mainHeight = Field<TextShaper>(panel, "_mainShaper").LineHeight;
        double translationHeight = Field<TextShaper>(panel, "_translationShaper").LineHeight;
        foreach (var point in positions)
        {
            double top = point.Value.Y, bottom = top + (point.Key.Translation ? translationHeight : mainHeight);
            if (bottom < 0 || top > 236) continue;
            double distance = 12 < top ? top - 12 : 12 > bottom ? 12 - bottom : 0;
            if (distance >= 130) continue;
            if (point.Value.X < 29.5 + Math.Sqrt(130 * 130 - distance * distance) - 0.15) return false;
        }
        return true;
    }

    private static bool ClearOfCircle(LyricFlowPanel panel, object[] views)
    {
        var mainHeight = Field<TextShaper>(panel, "_mainShaper").LineHeight;
        var translationHeight = Field<TextShaper>(panel, "_translationShaper").LineHeight;
        var scroll = Field<double>(panel, "_scroll");
        foreach (var view in views.Where(v => Field<object?>(v, "Main") is not null))
        {
            var top = Field<double>(view, "Top") - scroll;
            var tokens = Field<List<Token>>(view, "Tokens");
            var rows = Field<int[]>(view, "Row");
            if (!CheckBand(tokens, Field<double[]>(view, "X"), rows, top, mainHeight)) return false;
            if (Field<List<Token>?>(view, "TranslationTokens") is { } translations)
            {
                var translatedTop = top + (rows.Length > 0 ? rows[^1] + 1 : 1) * mainHeight + 4;
                if (!CheckBand(translations, Field<double[]>(view, "TX"), Field<int[]>(view, "TRow"), translatedTop, translationHeight)) return false;
            }
        }
        return true;

        static bool CheckBand(List<Token> tokens, double[] x, int[] rows, double top, double height)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                double rowTop = top + rows[i] * height, rowBottom = rowTop + height;
                if (rowBottom < 0 || rowTop > 236) continue;
                double distance = 12 < rowTop ? rowTop - 12 : 12 > rowBottom ? 12 - rowBottom : 0;
                if (distance >= 130) continue;
                double circleRight = 29.5 + Math.Sqrt(130 * 130 - distance * distance);
                if (22 + x[i] < circleRight - 0.1) return false;
            }
            return true;
        }
    }
}
#endif
