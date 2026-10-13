using System.Globalization;
using System.Text.RegularExpressions;
using LaLaLand.Models;

namespace LaLaLand.Services;

/// <summary>
/// NetEase "YRC" word-synced lyrics: <c>[lineStartMs,lineDurMs](wordStartMs,wordDurMs,0)word(…)word…</c>.
/// Lines starting with '{' are JSON credits and are skipped.
/// </summary>
public static partial class YrcParser
{
    [GeneratedRegex(@"^\[(\d+),(\d+)\]")]
    private static partial Regex LineTagRegex();

    [GeneratedRegex(@"\((\d+),(\d+),\d+\)([^(]*)")]
    private static partial Regex WordRegex();

    public static Lyrics Parse(string yrc, string? translationLrc, string source)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in yrc.Split('\n'))
        {
            var line = raw.Trim('\r');
            var head = LineTagRegex().Match(line);
            if (!head.Success)
                continue;

            var start = TimeSpan.FromMilliseconds(long.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture));
            var words = new List<LyricWord>();
            foreach (Match m in WordRegex().Matches(line, head.Length))
            {
                var text = m.Groups[3].Value;
                if (text.Length == 0)
                    continue;
                words.Add(new LyricWord(
                    TimeSpan.FromMilliseconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)),
                    TimeSpan.FromMilliseconds(long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)),
                    text));
            }

            // Keep the words aligned with the (trimmed) display text.
            var trimmed = TrimWords(words);
            lines.Add(new LyricLine(start, string.Concat(trimmed.Select(w => w.Text)), null, trimmed.Count > 0 ? trimmed : null));
        }

        if (!lines.Any(l => l.Text.Length > 0))
            return Lyrics.Empty;

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Translations come as a regular LRC whose timestamps may differ slightly from the YRC line starts.
        if (!string.IsNullOrWhiteSpace(translationLrc))
        {
            var translations = LrcParser.Parse(translationLrc, null, source).Lines;
            var used = new HashSet<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var match = translations
                    .Select((line, index) => (Line: line, Index: index))
                    .Where(t => !used.Contains(t.Index) && (t.Line.Time - lines[i].Time).Duration() < TimeSpan.FromMilliseconds(600) && t.Line.Text.Length > 0)
                    .OrderBy(t => (t.Line.Time - lines[i].Time).Duration())
                    .FirstOrDefault();
                if (match.Line is not null && match.Line.Text != lines[i].Text)
                {
                    used.Add(match.Index);
                    lines[i] = lines[i] with { Translation = match.Line.Text };
                }
            }
        }

        return new Lyrics(lines, true, source);
    }

    /// <summary>Drops leading/trailing whitespace while keeping word timings attached to the remaining text.</summary>
    internal static List<LyricWord> TrimWords(List<LyricWord> words)
    {
        var result = new List<LyricWord>(words);
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[0].Text))
            result.RemoveAt(0);
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1].Text))
            result.RemoveAt(result.Count - 1);
        if (result.Count > 0)
        {
            result[0] = result[0] with { Text = result[0].Text.TrimStart() };
            result[^1] = result[^1] with { Text = result[^1].Text.TrimEnd() };
        }
        return result;
    }
}
