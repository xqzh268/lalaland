using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Controls;

/// <summary>
/// Fits a line of text into the collapsed island. Built on <see cref="TextShaper"/> (prepare once) and
/// <see cref="TokenLayout"/> (arithmetic), so the fit and the rendered line breaks can never disagree.
/// </summary>
internal sealed class TextFitter
{
    /// <param name="Width">Width the text needs (two lines: the balanced width).</param>
    /// <param name="BreakAt">First token of the second line, or -1 for a single line.</param>
    /// <param name="VisibleCount">Tokens shown; fewer than the total means the text ends in an ellipsis.</param>
    public readonly record struct Fit(double Width, bool TwoLines, int BreakAt, int VisibleCount);

    private readonly Dictionary<(string Text, int MaxWidth, bool TwoLines), (Fit Fit, List<Token> Tokens)> _cache = new();

    public TextShaper Shaper { get; } = new(null, 13.5, FontWeights.SemiBold);

    public void Configure(FontFamily? chinese, FontFamily? english, double fontSize, Windows.UI.Text.FontWeight weight)
    {
        Shaper.Configure(chinese, english, fontSize, weight);
        _cache.Clear();
    }

    public (Fit Fit, List<Token> Tokens) Get(string text, double maxWidth, bool allowTwoLines)
    {
        var key = (text, (int)maxWidth, allowTwoLines);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var tokens = Shaper.Tokenize(text);
        int n = tokens.Count;
        double single = TokenLayout.LineWidth(tokens, 0, n);
        double ellipsis = Shaper.Width("…");
        Fit fit;

        if (single <= maxWidth)
        {
            fit = new Fit(Math.Ceiling(single) + 1, false, -1, n);
        }
        else if (!allowTwoLines)
        {
            fit = new Fit(maxWidth, false, -1, CountFitting(tokens, 0, maxWidth - ellipsis));
        }
        else
        {
            var (breakAt, width) = TokenLayout.BalancedTwoLines(tokens, maxWidth);
            if (breakAt > 0)
            {
                fit = new Fit(Math.Ceiling(width) + 1, true, breakAt, n);
            }
            else
            {
                // Too long even for two lines: fill the first, ellipsise the second.
                int first = Math.Max(1, CountFitting(tokens, 0, maxWidth));
                int second = CountFitting(tokens, first, maxWidth - ellipsis);
                fit = new Fit(maxWidth, true, first, first + second);
            }
        }

        _cache[key] = (fit, tokens);
        return (fit, tokens);
    }

    /// <summary>How many tokens starting at <paramref name="from"/> fit on one line of <paramref name="width"/>.</summary>
    private static int CountFitting(List<Token> tokens, int from, double width)
    {
        int count = 0;
        for (int i = from; i < tokens.Count && TokenLayout.LineWidth(tokens, from, i + 1) <= width; i++)
            count++;
        return count;
    }
}
