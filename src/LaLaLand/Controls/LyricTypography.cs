using LaLaLand.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Controls;

/// <summary>One script-aware font policy for both XAML rendering and DirectWrite measurement.</summary>
internal sealed class LyricTypography(FontFamily? chinese = null, FontFamily? english = null)
{
    public FontFamily Chinese { get; } = chinese ?? FontFamily.XamlAutoFontFamily;
    public FontFamily English { get; } = english ?? FontFamily.XamlAutoFontFamily;

    public static FontFamily? Family(string? name) => string.IsNullOrWhiteSpace(name) ? null : new FontFamily(name);

    public void Apply(TextBlock block, string text)
    {
        block.Text = "";
        block.Inlines.Clear();
        foreach (var run in LyricFontScripts.Split(text))
            block.Inlines.Add(new Run { Text = run.Text, FontFamily = run.EastAsian ? Chinese : English });
    }
}
