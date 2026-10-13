using LaLaLand.Controls;
using LaLaLand.Services;
using LaLaLand.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Views;

public sealed partial class SettingsWindow
{
    private (string? Chinese, string? English)? _previewFonts;

    private void InitLyricAppearance()
    {
        App.Media.ArtworkChanged += UpdateLyricPreview;
        Closed += (_, _) => App.Media.ArtworkChanged -= UpdateLyricPreview;
    }

    private void LoadLyricAppearance()
    {
        var s = _settings.Current;
        var fonts = FontCatalog.GetFamilies();
        LoadFonts(LyricChineseFontBox, s.LyricChineseFontFamily ?? s.LyricFontFamily, fonts);
        LoadFonts(LyricEnglishFontBox, s.LyricEnglishFontFamily ?? s.LyricFontFamily, fonts);
        LyricColorPicker.Color = LyricColors.Parse(s.LyricFontColor);
        LyricGradientSlider.Value = s.LyricGradientStrength * 100;
        LyricTransparencySlider.Value = (1 - s.LyricOpacity) * 100;
        UpdateLyricPreview();
    }

    private static void LoadFonts(ComboBox box, string current, IReadOnlyList<string> fonts)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "系统默认", Tag = "" });
        int selected = 0;
        foreach (string family in fonts)
        {
            if (family.Equals(current, StringComparison.OrdinalIgnoreCase)) selected = box.Items.Count;
            box.Items.Add(new ComboBoxItem
            {
                Tag = family,
                Content = new TextBlock { Text = family, FontFamily = new FontFamily(family), FontSize = 14 },
            });
        }
        // Keep an unavailable saved font visible; Windows will fall back until it is installed again.
        if (selected == 0 && current.Length > 0)
        {
            selected = box.Items.Count;
            box.Items.Add(new ComboBoxItem { Content = current + "（未安装，使用回退）", Tag = current });
        }
        box.SelectedIndex = selected;
    }

    private void OnLyricFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string family } } box) return;
        _settings.Update(s =>
        {
            if (box == LyricChineseFontBox) s.LyricChineseFontFamily = family;
            else if (box == LyricEnglishFontBox) s.LyricEnglishFontFamily = family;
        });
        UpdateLyricPreview();
    }

    private void OnLyricColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        _settings.UpdateLyricPaint(mode: LyricColorMode.Custom, color: LyricColors.Hex(args.NewColor));
        UpdateLyricPreview();
    }

    private void OnResetLyricColor(object sender, RoutedEventArgs e) => LyricColorPicker.Color = Microsoft.UI.Colors.White;

    private void OnLyricColorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LyricColorModeBox.SelectedIndex < 0) return;
        _settings.UpdateLyricPaint(mode: LyricColorModeBox.SelectedIndex == 1 ? LyricColorMode.Custom : LyricColorMode.Album);
        UpdateLyricPreview();
    }

    private void OnLyricPaintChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || sender is not Slider slider) return;
        if (slider == LyricGradientSlider) _settings.UpdateLyricPaint(gradientStrength: e.NewValue / 100);
        else if (slider == LyricTransparencySlider) _settings.UpdateLyricPaint(opacity: 1 - e.NewValue / 100);
        UpdateLyricPreview();
    }

    private void UpdateLyricPreview()
    {
        var s = _settings.Current;
        var fonts = (s.LyricChineseFontFamily ?? s.LyricFontFamily, s.LyricEnglishFontFamily ?? s.LyricFontFamily);
        if (_previewFonts != fonts)
        {
            _previewFonts = fonts;
            var typography = new LyricTypography(LyricTypography.Family(fonts.Item1), LyricTypography.Family(fonts.Item2));
            typography.Apply(LyricFontPreview, "烟花易冷 · Cheap Hotel");
            typography.Apply(LyricAsianPreview, "日本語の歌詞 · 한국어 가사");
        }
        var brush = LyricColors.Create(s.LyricColorMode, s.LyricFontColor, App.Media.Artwork, s.LyricGradientStrength, s.LyricOpacity);
        LyricFontPreview.Foreground = LyricAsianPreview.Foreground = brush;
        LyricColorSwatch.Background = new SolidColorBrush(LyricColors.Parse(s.LyricFontColor));
        LyricColorHex.Text = LyricColors.Hex(LyricColors.Parse(s.LyricFontColor));
        LyricGradientValue.Text = $"{s.LyricGradientStrength * 100:0}%";
        LyricTransparencyValue.Text = $"{(1 - s.LyricOpacity) * 100:0}%";
        LyricGradientSlider.IsEnabled = s.LyricColorMode == LyricColorMode.Album;
        LyricGradientRow.Opacity = LyricGradientSlider.IsEnabled ? 1 : 0.5;
        bool loading = _loading;
        _loading = true;
        LyricColorModeBox.SelectedIndex = s.LyricColorMode == LyricColorMode.Custom ? 1 : 0;
        LyricCustomColorRow.Visibility = s.LyricColorMode == LyricColorMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        _loading = loading;
    }
}
