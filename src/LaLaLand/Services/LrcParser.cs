using System.Globalization;
using System.Text.RegularExpressions;
using LaLaLand.Models;

namespace LaLaLand.Services;

public static partial class LrcParser
{
    // [mm:ss], [mm:ss.xx], [mm:ss.xxx], [mm:ss:xx]
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetRegex();

    // Enhanced ("A2") LRC word tags: <mm:ss.xx>
    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTagRegex();

    /// <summary>
    /// Extracts enhanced-LRC word timings (<c>&lt;00:12.34&gt;word &lt;00:12.80&gt;word</c>) and strips the tags
    /// from <paramref name="text"/>. Returns null for ordinary lines.
    /// </summary>
    private static List<LyricWord>? ParseEnhancedWords(ref string text)
    {
        var tags = WordTagRegex().Matches(text);
        if (tags.Count == 0)
            return null;

        var words = new List<LyricWord>();
        for (int i = 0; i < tags.Count; i++)
        {
            int from = tags[i].Index + tags[i].Length;
            int to = i + 1 < tags.Count ? tags[i + 1].Index : text.Length;
            var piece = text[from..to];
            var start = ToTime(tags[i]);
            var end = i + 1 < tags.Count ? ToTime(tags[i + 1]) : start + TimeSpan.FromMilliseconds(600);
            if (piece.Length > 0)
                words.Add(new LyricWord(start, end > start ? end - start : TimeSpan.FromMilliseconds(100), piece));
        }

        var trimmed = YrcParser.TrimWords(words);
        text = string.Concat(trimmed.Select(w => w.Text));
        return trimmed.Count > 0 ? trimmed : null;

        static TimeSpan ToTime(Match m)
        {
            int ms = 0;
            if (m.Groups[3].Success)
            {
                var frac = m.Groups[3].Value;
                ms = int.Parse(frac, CultureInfo.InvariantCulture) * (frac.Length switch { 1 => 100, 2 => 10, _ => 1 });
            }
            return TimeSpan.FromMilliseconds(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60_000 +
                                             int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 1000 + ms);
        }
    }

    /// <summary>Parses LRC text, optionally merging a translation LRC by timestamp.</summary>
    public static Lyrics Parse(string? lrc, string? translationLrc, string source)
    {
        var lines = ParseLines(lrc);
        if (lines.Count == 0)
        {
            // Plain (unsynced) lyrics
            var plain = (lrc ?? "")
                .Split('\n')
                .Select(l => TimeTagRegex().Replace(l, "").Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('['))
                .Select(l => new LyricLine(TimeSpan.Zero, l, null))
                .ToList();
            return plain.Count == 0 ? Lyrics.Empty : new Lyrics(plain, false, source);
        }

        if (!string.IsNullOrWhiteSpace(translationLrc))
        {
            var translations = ParseLines(translationLrc);
            var used = new HashSet<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var match = translations.Select((line, index) => (Line: line, Index: index, Delta: (line.Time - lines[i].Time).Duration()))
                    .Where(t => !used.Contains(t.Index) && t.Delta <= TimeSpan.FromMilliseconds(400) && t.Line.Text.Length > 0)
                    .OrderBy(t => t.Delta).FirstOrDefault();
                if (match.Line is not null && match.Line.Text != lines[i].Text)
                {
                    used.Add(match.Index);
                    lines[i] = lines[i] with { Translation = match.Line.Text };
                }
            }
        }

        return new Lyrics(lines, true, source);
    }

    private static List<LyricLine> ParseLines(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc))
            return result;

        // The tag applies to the entire file regardless of its position; the last tag wins.
        long offsetMs = 0;
        foreach (var raw in lrc.Split('\n'))
        {
            var offset = OffsetRegex().Match(raw.Trim());
            if (offset.Success)
                long.TryParse(offset.Groups[1].Value, out offsetMs);
        }
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            var offsetMatch = OffsetRegex().Match(line);
            if (offsetMatch.Success)
            {
                continue;
            }

            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0)
                continue;

            var text = line[(matches[^1].Index + matches[^1].Length)..].Trim();
            var words = ParseEnhancedWords(ref text);
            var firstTag = Timestamp(matches[0]);
            foreach (Match m in matches)
            {
                // A positive [offset:] means lyrics should appear earlier.
                var taggedTime = Timestamp(m);
                var shift = taggedTime - firstTag - TimeSpan.FromMilliseconds(offsetMs);
                var time = taggedTime - TimeSpan.FromMilliseconds(offsetMs);
                var shiftedWords = words?.Select(w => w with { Start = MaxZero(w.Start + shift) }).ToList();
                result.Add(new LyricLine(MaxZero(time), text, null, shiftedWords));
            }
        }

        result = result.OrderBy(l => l.Time).ToList(); // stable ordering for bilingual timestamp pairs

        // Drop empty trailing lines and collapse runs of blank lines into one "breathing" gap.
        var cleaned = new List<LyricLine>(result.Count);
        foreach (var l in result)
        {
            if (l.Text.Length == 0 && (cleaned.Count == 0 || cleaned[^1].Text.Length == 0))
                continue;
            if (cleaned.Count > 0 && cleaned[^1].Time == l.Time && cleaned[^1].Text.Length > 0)
            {
                var previous = cleaned[^1];
                if (previous.Text == l.Text)
                    cleaned[^1] = previous with { Words = previous.Words ?? l.Words };
                else if (ContainsCjk(previous.Text) != ContainsCjk(l.Text))
                    cleaned[^1] = previous with { Translation = l.Text };
                else
                    cleaned[^1] = previous with { Text = previous.Text + " / " + l.Text, Words = null };
                continue;
            }
            cleaned.Add(l);
        }
        while (cleaned.Count > 0 && cleaned[^1].Text.Length == 0)
            cleaned.RemoveAt(cleaned.Count - 1);
        return cleaned;
    }

    private static TimeSpan MaxZero(TimeSpan time) => time < TimeSpan.Zero ? TimeSpan.Zero : time;
    private static bool ContainsCjk(string text) => text.Any(c => c is >= '\u3400' and <= '\u9fff');
    private static TimeSpan Timestamp(Match match)
    {
        var fraction = match.Groups[3].Value;
        int ms = fraction.Length == 0 ? 0 : int.Parse(fraction, CultureInfo.InvariantCulture) * (fraction.Length switch { 1 => 100, 2 => 10, _ => 1 });
        return TimeSpan.FromMilliseconds(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60_000L +
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 1000L + ms);
    }
}
