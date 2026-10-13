using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LaLaLand.Controls;

/// <summary>
/// Pretext-style "prepare" step: splits text into tokens and measures each distinct token once with the same
/// XAML/DirectWrite engine that renders it. Everything after this is arithmetic (<see cref="TokenLayout"/>).
/// </summary>
internal sealed class TextShaper
{
    private const string Closing = "，。！？、；：）」』】》〉”’…,.!?;:)]}%";
    private const string Opening = "（「『【《〈“‘([{";

    private readonly TextBlock _probe = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly Dictionary<string, double> _widths = new();
    private double _spaceWidth;

    private LyricTypography _typography = new();
    public double FontSize { get; private set; } = 14;
    public Windows.UI.Text.FontWeight Weight { get; private set; } = FontWeights.Normal;
    public double LineHeight { get; private set; }

    public TextShaper(FontFamily? family, double fontSize, Windows.UI.Text.FontWeight weight) => Configure(family, fontSize, weight);

    public void Configure(FontFamily? family, double fontSize, Windows.UI.Text.FontWeight weight)
        => Configure(family, family, fontSize, weight);

    public void Configure(FontFamily? chinese, FontFamily? english, double fontSize, Windows.UI.Text.FontWeight weight)
    {
        _typography = new LyricTypography(chinese, english);
        FontSize = fontSize;
        Weight = weight;
        _probe.FontSize = fontSize;
        _probe.FontWeight = weight;
        _widths.Clear();
        _spaceWidth = Measure("a a").Width - Measure("aa").Width;
        LineHeight = Math.Ceiling(Measure("Ag国あ한").Height);
    }

    /// <summary>Applies this shaper's typography to a TextBlock that renders its tokens.</summary>
    public void Style(TextBlock block, string text)
    {
        block.FontSize = FontSize;
        block.FontWeight = Weight;
        _typography.Apply(block, text);
    }

    public double Width(string text)
    {
        if (!_widths.TryGetValue(text, out var w))
            _widths[text] = w = Measure(text).Width;
        return w;
    }

    public List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        while (i < text.Length)
        {
            int start = i;
            // Opening punctuation sticks to what follows it.
            while (i < text.Length && Opening.Contains(text[i]))
                i++;
            if (i < text.Length && IsCjk(text[i]) && !Closing.Contains(text[i]))
                i++;
            else
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && !(IsCjk(text[i]) && !Closing.Contains(text[i])) && !Opening.Contains(text[i]))
                    i++;
            if (i == start)
                i++; // safety: always consume something
            // Closing punctuation sticks to what precedes it.
            while (i < text.Length && Closing.Contains(text[i]))
                i++;

            int end = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            var piece = text[start..end];
            tokens.Add(new Token(piece, start, end - start, Width(piece), (i - end) * _spaceWidth));
        }
        return tokens;
    }

    public static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';

    private Size Measure(string text)
    {
        _typography.Apply(_probe, text);
        _probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _probe.DesiredSize;
    }
}
