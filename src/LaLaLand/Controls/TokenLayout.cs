namespace LaLaLand.Controls;

/// <summary>An unbreakable piece of text: a CJK character or a word, with its trailing whitespace as <see cref="Gap"/>.</summary>
/// <param name="Start">Index of the first character in the source text.</param>
/// <param name="Width">Ink width without trailing whitespace.</param>
/// <param name="Gap">Width of the whitespace after the token (dropped at a line end).</param>
internal readonly record struct Token(string Text, int Start, int Length, double Width, double Gap);

/// <summary>Pure-arithmetic line breaking over prepared tokens (the Pretext "layout" step).</summary>
internal static class TokenLayout
{
    /// <summary>Width of the tokens [from, to) on one line (gaps between them, none after the last).</summary>
    public static double LineWidth(IReadOnlyList<Token> tokens, int from, int to)
    {
        double w = 0;
        for (int i = from; i < to; i++)
            w += tokens[i].Width + (i < to - 1 ? tokens[i].Gap : 0);
        return w;
    }

    /// <summary>
    /// Greedy wrap where every line may have its own slot (left offset, available width) — e.g. narrower beside an
    /// obstacle. Writes each token's x / line into the arrays and returns the number of lines.
    /// </summary>
    public static int Wrap(IReadOnlyList<Token> tokens, Func<int, (double Left, double Width)> slot, double[] x, int[] line)
    {
        int lineIndex = 0;
        var (left, width) = slot(0);
        double cursor = 0;
        bool lineEmpty = true;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!lineEmpty && cursor + t.Width > width)
            {
                lineIndex++;
                (left, width) = slot(lineIndex);
                cursor = 0;
                lineEmpty = true;
            }
            x[i] = left + cursor;
            line[i] = lineIndex;
            cursor += t.Width + t.Gap;
            lineEmpty = false;
        }
        return tokens.Count == 0 ? 0 : lineIndex + 1;
    }

    /// <summary>Wraps upwards from a fixed bottom edge, preserving token order and each row's circle clearance.</summary>
    public static int WrapFromBottom(IReadOnlyList<Token> tokens, Func<int, (double Left, double Width)> slot, double[] x, int[] line)
    {
        int end = tokens.Count, rows = 0;
        while (end > 0)
        {
            var (left, width) = slot(rows);
            int start = end - 1;
            double used = tokens[start].Width;
            while (start > 0 && used + tokens[start - 1].Width + tokens[start - 1].Gap <= width)
            {
                start--;
                used += tokens[start].Width + tokens[start].Gap;
            }
            double cursor = left;
            for (int i = start; i < end; i++)
            {
                x[i] = cursor;
                line[i] = -rows;
                cursor += tokens[i].Width + tokens[i].Gap;
            }
            end = start;
            rows++;
        }
        for (int i = 0; i < tokens.Count; i++) line[i] += rows - 1;
        return rows;
    }

    /// <summary>The break (first token of line 2) that minimises the wider of two lines, within maxWidth; -1 if none fits.</summary>
    public static (int BreakAt, double Width) BalancedTwoLines(IReadOnlyList<Token> tokens, double maxWidth)
    {
        int best = -1;
        double bestWidth = double.MaxValue;
        for (int k = 1; k < tokens.Count; k++)
        {
            double w = Math.Max(LineWidth(tokens, 0, k), LineWidth(tokens, k, tokens.Count));
            if (w <= maxWidth && w < bestWidth)
            {
                bestWidth = w;
                best = k;
            }
        }
        return (best, best < 0 ? 0 : bestWidth);
    }
}
