#if DEBUG
using System.Collections;
using System.Reflection;
using System.Text.Json;
using LaLaLand.Controls;
using LaLaLand.Models;
using LaLaLand.Services;
using LaLaLand.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace LaLaLand;

public partial class App
{
    private async Task RunLyricAppearanceAcceptanceAsync()
    {
        var checks = new List<string>();
        var report = new Dictionary<string, object?>();
        SettingsWindow? window = null;
        IslandWindow? island = null;
        void Check(string name, bool condition)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        try
        {
            Check("legacy font migrates into both independent choices", Settings.Current.LyricChineseFontFamily == "Eras Demi ITC" &&
                Settings.Current.LyricEnglishFontFamily == "Eras Demi ITC");
            Check("cover mixing is the default on legacy files", Settings.Current.LyricColorMode == LyricColorMode.Album);
            Check("legacy appearance keeps full gradient and opacity", Settings.Current.LyricGradientStrength == 1 && Settings.Current.LyricOpacity == 1);
            window = new SettingsWindow(Settings, Hotkeys, Lyrics);
            var chineseBox = Field<ComboBox>(window, "LyricChineseFontBox");
            var englishBox = Field<ComboBox>(window, "LyricEnglishFontBox");
            var modeBox = Field<ComboBox>(window, "LyricColorModeBox");
            var picker = Field<ColorPicker>(window, "LyricColorPicker");
            var gradientSlider = Field<Slider>(window, "LyricGradientSlider");
            var transparencySlider = Field<Slider>(window, "LyricTransparencySlider");
            Check("loading sliders preserves defaults without writing their initial zero", gradientSlider.Value == 100 && transparencySlider.Value == 0 &&
                Settings.Current.LyricGradientStrength == 1 && Settings.Current.LyricOpacity == 1);
            Check("loading settings controls preserves migrated values", SelectedFont(chineseBox) == "Eras Demi ITC" && SelectedFont(englishBox) == "Eras Demi ITC");
            Check("automatic mode hides custom controls", modeBox.SelectedIndex == 0 && Field<Grid>(window, "LyricCustomColorRow").Visibility == Visibility.Collapsed);
            var fonts = FontCatalog.GetFamilies();
            string chinese = fonts.First(f => f.Contains("YaHei", StringComparison.OrdinalIgnoreCase) || f == "微软雅黑");
            string english = fonts.First(f => f.Equals("Arial", StringComparison.OrdinalIgnoreCase));
            chineseBox.SelectedItem = chineseBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == chinese);
            Check("changing Chinese leaves English unchanged", Settings.Current.LyricChineseFontFamily == chinese && Settings.Current.LyricEnglishFontFamily == "Eras Demi ITC");
            englishBox.SelectedItem = englishBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == english);
            Check("changing English leaves Chinese unchanged", Settings.Current.LyricChineseFontFamily == chinese && Settings.Current.LyricEnglishFontFamily == english);
            modeBox.SelectedIndex = 1;
            Check("custom mode exposes the picker", Settings.Current.LyricColorMode == LyricColorMode.Custom && Field<Grid>(window, "LyricCustomColorRow").Visibility == Visibility.Visible);
            Check("custom mode disables the cover-only gradient control", !gradientSlider.IsEnabled && transparencySlider.IsEnabled);
            picker.Color = Windows.UI.Color.FromArgb(255, 18, 52, 86);
            Check("color picker persists RGB and refreshes its preview", Settings.Current.LyricFontColor == "#123456" &&
                Field<TextBlock>(window, "LyricFontPreview").Foreground is SolidColorBrush { Color.R: 18, Color.G: 52, Color.B: 86 });
            modeBox.SelectedIndex = 0;
            Check("returning to cover mode retains the custom choice", Settings.Current.LyricColorMode == LyricColorMode.Album && Settings.Current.LyricFontColor == "#123456");
            Check("preview without artwork falls back to white", Field<TextBlock>(window, "LyricFontPreview").Foreground is SolidColorBrush { Color.R: 255, Color.G: 255, Color.B: 255 });

            var art = await MakeAcceptanceArtworkAsync(transparent: false);
            Check("decoded cover yields multiple hues from the processed glow", art is { LyricPalette.Count: 3 } && art.LyricPalette.Select(LyricColors.Hex).Distinct().Count() > 1);
            report["palette"] = art!.LyricPalette.Select(LyricColors.Hex).ToArray();
            var gradient = LyricColors.Create(LyricColorMode.Album, "#123456", art);
            Check("automatic ink is a multi-stop gradient", gradient is LinearGradientBrush { GradientStops.Count: 3 });
            Check("missing cover produces opaque white", LyricColors.Create(LyricColorMode.Album, "#123456", null) is SolidColorBrush { Color.A: 255, Color.R: 255, Color.G: 255, Color.B: 255 });
            var transparent = await MakeAcceptanceArtworkAsync(transparent: true);
            Check("fully transparent artwork falls back to white", transparent is { LyricPalette.Count: 0 } &&
                LyricColors.Create(LyricColorMode.Album, "#123456", transparent) is SolidColorBrush { Color.R: 255, Color.G: 255, Color.B: 255 });
            Check("custom color ignores cover hues", LyricColors.Create(LyricColorMode.Custom, "#123456", art) is SolidColorBrush { Color.R: 18, Color.G: 52, Color.B: 86 });
            typeof(MediaService).GetMethod("SetArtwork", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, new object?[] { art });
            Check("late artwork refreshes the open settings preview", Field<TextBlock>(window, "LyricFontPreview").Foreground is LinearGradientBrush);
            island = new IslandWindow(Settings, Media, Lyrics, AppleMusic, Volume);
            var islandTicker = Field<SweepText>(island, "_currentTicker");
            var islandGlyphs = Field<object>(islandTicker, "_visuals");
            var islandFontCache = Field<TextFitter>(island, "_fitter");
            int generalChanges = 0, paintChanges = 0;
            Settings.Changed += CountGeneralChange;
            Settings.LyricPaintChanged += CountPaintChange;
            void CountGeneralChange() => generalChanges++;
            void CountPaintChange() => paintChanges++;
            var preview = Field<TextBlock>(window, "LyricFontPreview");
            var previewRuns = preview.Inlines.OfType<Run>().ToArray();
            gradientSlider.Value = 0;
            Check("zero gradient strength uses one cover color", Settings.Current.LyricGradientStrength == 0 && preview.Foreground is SolidColorBrush);
            gradientSlider.Value = 50;
            var half = (LinearGradientBrush)preview.Foreground;
            var full = (LinearGradientBrush)gradient;
            for (int i = 0; i < half.GradientStops.Count; i++)
            {
                var a = half.GradientStops[i].Color;
                var b = half.GradientStops[^1].Color;
                var c = full.GradientStops[i].Color;
                var d = full.GradientStops[^1].Color;
                Check($"half strength halves color separation at stop {i}",
                    Math.Abs((a.R - b.R) - (c.R - d.R) * 0.5) <= 1 &&
                    Math.Abs((a.G - b.G) - (c.G - d.G) * 0.5) <= 1 &&
                    Math.Abs((a.B - b.B) - (c.B - d.B) * 0.5) <= 1);
            }
            gradientSlider.Value = 100;
            Check("full strength restores the original palette", ((LinearGradientBrush)preview.Foreground).GradientStops.Select(s => s.Color).SequenceEqual(art.LyricPalette));
            gradientSlider.Value = 40;
            transparencySlider.Value = 65;
            gradient = preview.Foreground;
            report["sliderPreview"] = new
            {
                Settings.Current.LyricGradientStrength, Settings.Current.LyricOpacity,
                BrushOpacity = gradient.Opacity,
                GradientLabel = Field<TextBlock>(window, "LyricGradientValue").Text,
                TransparencyLabel = Field<TextBlock>(window, "LyricTransparencyValue").Text,
            };
            Check("native slider events update independent settings and preview", Math.Abs(Settings.Current.LyricGradientStrength - 0.4) < 1e-9 &&
                Math.Abs(Settings.Current.LyricOpacity - 0.35) < 1e-9 && Math.Abs(gradient.Opacity - 0.35) < 1e-6 &&
                Field<TextBlock>(window, "LyricGradientValue").Text == "40%" && Field<TextBlock>(window, "LyricTransparencyValue").Text == "65%");
            Check("paint changes preserve preview glyphs", previewRuns.SequenceEqual(preview.Inlines.OfType<Run>()));
            modeBox.SelectedIndex = 1;
            Check("custom color shares the transparency setting", preview.Foreground is SolidColorBrush { Color.R: 18 } && Math.Abs(preview.Foreground.Opacity - 0.35) < 1e-6);
            modeBox.SelectedIndex = 0;
            Check("mode switches retain gradient and transparency", gradientSlider.Value == 40 && transparencySlider.Value == 65 && gradientSlider.IsEnabled);
            transparencySlider.Value = 100;
            Check("maximum transparency makes the foreground invisible", preview.Foreground.Opacity == 0);
            transparencySlider.Value = 0;
            Check("zero transparency restores opaque foreground", preview.Foreground.Opacity == 1);
            transparencySlider.Value = 65;
            Check("paint sliders notify only foreground listeners", generalChanges == 0 && paintChanges > 0);
            Check("live window paint changes preserve ticker glyphs and fitter", ReferenceEquals(islandGlyphs, Field<object>(islandTicker, "_visuals")) &&
                ReferenceEquals(islandTicker, Field<SweepText>(island, "_currentTicker")) && ReferenceEquals(islandFontCache, Field<TextFitter>(island, "_fitter")));
            var islandInk = Field<Brush>(island, "_tickerBrush");
            Check("live window applies slider opacity to every view", Math.Abs(islandInk.Opacity - 0.35) < 1e-6 &&
                ReferenceEquals(islandInk, Field<Brush>(Field<LyricFlowPanel>(island, "LyricFlow"), "_foreground")) &&
                ReferenceEquals(islandInk, Field<Brush>(Field<LyricsPanel>(island, "LyricsView"), "_lyricBrush")));
            Settings.Changed -= CountGeneralChange;
            Settings.LyricPaintChanged -= CountPaintChange;
            island.Shutdown();
            island = null;
            typeof(MediaService).GetMethod("SetArtwork", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Media, new object?[] { null });
            Check("artwork removal clears the preview palette", Field<TextBlock>(window, "LyricFontPreview").Foreground is SolidColorBrush { Color.R: 255, Color.G: 255, Color.B: 255 });
            Check("white fallback retains user transparency", Math.Abs(preview.Foreground.Opacity - 0.35) < 1e-6);
            Check("invalid paint values are safe before rendering", LyricColors.Create(LyricColorMode.Album, "#123456", art, double.NaN, double.PositiveInfinity).Opacity == 1 &&
                LyricColors.Create(LyricColorMode.Custom, "#123456", art, 2, -1).Opacity == 0);

            const string text = "Cheap Hotel，烟花易冷 日本語 한국어 2026";
            var fitter = new TextFitter();
            fitter.Configure(new FontFamily(chinese), new FontFamily(english), 13.5, FontWeights.SemiBold);
            var (fit, tokens) = fitter.Get(text, 220, true);
            var ticker = new SweepText();
            ticker.SetContent(text, tokens, fitter.Shaper, gradient);
            var visuals = ((IEnumerable)Field<object>(ticker, "_visuals")).Cast<object>().ToArray();
            for (int i = 0; i < tokens.Count; i++)
            {
                var root = Field<Grid>(visuals[i], "Root");
                foreach (var block in root.Children.OfType<TextBlock>())
                {
                    var runs = block.Inlines.OfType<Run>().ToArray();
                    var expected = LyricFontScripts.Split(tokens[i].Text).ToArray();
                    Check($"ticker token {i} keeps measured script font and source text", runs.Length == expected.Length &&
                        runs.Zip(expected).All(pair => pair.First.Text == pair.Second.Text && pair.First.FontFamily.Source == (pair.Second.EastAsian ? chinese : english)));
                    Check($"ticker token {i} uses mixed ink", block.Foreground is LinearGradientBrush { GradientStops.Count: 3 });
                    Check($"ticker token {i} retains shared brush opacity", Math.Abs(block.Foreground.Opacity - 0.35) < 1e-6);
                }
            }
            Check("long mixed line can wrap in the collapsed fitter", fit.TwoLines);
            var firstInk = (LinearGradientBrush)Field<TextBlock>(visuals[0], "Lit").Foreground;
            var lastInk = (LinearGradientBrush)Field<TextBlock>(visuals[^1], "Lit").Foreground;
            Check("palette spans the line rather than repeating per word", firstInk.StartPoint.X == 0 && lastInk.StartPoint.X < 0 && firstInk.EndPoint.X > 1);
            ticker.StartSweep(Enumerable.Range(0, text.Length).Select(i => (TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1))).ToArray(), TimeSpan.Zero, true);
            var timedGlyphs = Field<object>(ticker, "_visuals");
            var wordTimes = Field<object>(ticker, "_times");
            ticker.SetForeground(LyricColors.Create(LyricColorMode.Custom, "#123456", art, 0.4, 0.22));
            Check("changing color preserves karaoke timing", ticker.IsSweeping && ReferenceEquals(timedGlyphs, Field<object>(ticker, "_visuals")) && ReferenceEquals(wordTimes, Field<object>(ticker, "_times")));
            Check("changing opacity updates the existing karaoke glyphs", visuals.All(v => Math.Abs(Field<TextBlock>(v, "Lit").Foreground.Opacity - 0.22) < 1e-6));
            Check("foreground opacity does not replace karaoke dimming", visuals.All(v => Math.Abs(((TextBlock)Field<Grid>(v, "Root").Children[0]).Opacity - ticker.DimOpacity) < 1e-6));
            double englishWidth = fitter.Shaper.Width("WWWW Cheap Hotel");
            fitter.Configure(new FontFamily("Segoe UI"), new FontFamily(english), 13.5, FontWeights.SemiBold);
            Check("Chinese font changes do not alter Latin measurement", Math.Abs(fitter.Shaper.Width("WWWW Cheap Hotel") - englishWidth) < 0.01);
            fitter.Configure(new FontFamily(chinese), new FontFamily("Courier New"), 13.5, FontWeights.SemiBold);
            Check("English font changes invalidate cached widths", Math.Abs(fitter.Shaper.Width("WWWW Cheap Hotel") - englishWidth) > 1);

            var lyrics = LrcParser.Parse("[00:00.00]Cheap Hotel，烟花易冷\n[00:10.00]日本語 한국어 2026", "[00:00.00]测试中文译文\n[00:10.00]中文翻译", "appearance-fixture");
            var flow = new LyricFlowPanel { Width = 380, Height = 236 };
            flow.Measure(new Size(380, 236)); flow.Arrange(new Rect(0, 0, 380, 236));
            flow.SetFonts(new FontFamily(chinese), new FontFamily(english));
            flow.SetLyrics(lyrics, true);
            flow.SetActive(1, TimeSpan.FromSeconds(10.5), true);
            var views = ((IEnumerable)Field<object>(flow, "_lines")).Cast<object>().ToArray();
            var main = Field<SweepText>(views[1], "Main");
            flow.SetForeground(gradient);
            Check("flow color change retains active line and glyphs", Field<int>(flow, "_active") == 1 && ReferenceEquals(main, Field<SweepText>(views[1], "Main")) && main.IsSweeping);
            Check("flow original and translation retain shared opacity", views.All(v =>
                ((IEnumerable)Field<object>(Field<SweepText>(v, "Main"), "_visuals")).Cast<object>().All(g => Math.Abs(Field<TextBlock>(g, "Lit").Foreground.Opacity - 0.35) < 1e-6) &&
                ((IEnumerable)Field<object>(Field<SweepText>(v, "Translation"), "_visuals")).Cast<object>().All(g => Math.Abs(Field<TextBlock>(g, "Lit").Foreground.Opacity - 0.35) < 1e-6)));
            Check("color changes create no scroll timer", !Field<DispatcherQueueTimer>(flow, "_scrollTimer").IsRunning);
            flow.SetFonts(new FontFamily(chinese), new FontFamily("Segoe UI"));
            Check("font change retains the active lyric", Field<int>(flow, "_active") == 1);
            flow.SetLive(false);
            flow.Clear();
            var normal = new LyricsPanel();
            normal.SetFonts(new FontFamily(chinese), new FontFamily(english));
            normal.SetLyrics(lyrics, true);
            normal.SetForeground(gradient);
            var linePanels = Field<List<StackPanel>>(normal, "_lines");
            Check("ordinary lyrics and translations share cover ink", linePanels.SelectMany(l => l.Children.OfType<TextBlock>()).All(b => b.Foreground == gradient));
            Check("ordinary lyrics and translations retain shared opacity", linePanels.SelectMany(l => l.Children.OfType<TextBlock>()).All(b => Math.Abs(b.Foreground.Opacity - 0.35) < 1e-6));
            Check("ordinary view renders both independent font families", linePanels.SelectMany(l => l.Children.OfType<TextBlock>()).SelectMany(b => b.Inlines.OfType<Run>()).Select(r => r.FontFamily.Source).Distinct().Count() == 2);
            Settings.SaveNow();
            var reloaded = new SettingsService(_dispatcher);
            Check("independent fonts, automatic mode and custom choice survive reload", reloaded.Current.LyricChineseFontFamily == chinese && reloaded.Current.LyricEnglishFontFamily == english && reloaded.Current.LyricColorMode == LyricColorMode.Album && reloaded.Current.LyricFontColor == "#123456");
            Check("gradient and transparency survive reload", Math.Abs(reloaded.Current.LyricGradientStrength - 0.4) < 1e-9 && Math.Abs(reloaded.Current.LyricOpacity - 0.35) < 1e-9);
            Settings.Update(s => s.LyricChineseFontFamily = "");
            Settings.SaveNow();
            var defaultReloaded = new SettingsService(_dispatcher);
            Check("explicit system default does not inherit the old font again", defaultReloaded.Current.LyricChineseFontFamily == "" && defaultReloaded.Current.LyricEnglishFontFamily == english);
            Settings.Update(s => { s.LyricGradientStrength = 2; s.LyricOpacity = -1; });
            Settings.SaveNow();
            var clampedReloaded = new SettingsService(_dispatcher);
            Check("out-of-range persisted paint settings are clamped", clampedReloaded.Current.LyricGradientStrength == 1 && clampedReloaded.Current.LyricOpacity == 0);
            report["passed"] = true;
        }
        catch (Exception ex) { report["passed"] = false; report["error"] = ex.ToString(); }
        finally
        {
            island?.Shutdown();
            window?.Close();
            report["checks"] = checks;
            File.WriteAllText(Path.Combine(PanelAcceptanceDirectory!, "lyric-appearance-acceptance.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Quit();
        }
    }

    private static string SelectedFont(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private static async Task<ArtworkData?> MakeAcceptanceArtworkAsync(bool transparent)
    {
        const int size = 48;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            if (transparent) continue;
            pixels[offset] = (byte)(x < size / 2 ? 20 : 240);
            pixels[offset + 1] = 40;
            pixels[offset + 2] = (byte)(x < size / 2 ? 240 : 20);
            pixels[offset + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, size, size, 96, 96, pixels);
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var encoded = new byte[(int)stream.Size];
        reader.ReadBytes(encoded);
        return await ArtworkProcessor.TryProcessAsync(encoded, ArtworkProcessor.HashOf(encoded));
    }
}
#endif
