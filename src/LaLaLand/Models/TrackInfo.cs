namespace LaLaLand.Models;

public sealed record TrackInfo(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string SourceAppId)
{
    public bool IsAppleMusic => SourceAppId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identity used to detect real track changes (metadata events fire repeatedly for the same song).</summary>
    public string Key => $"{Title}\u001f{Artist}\u001f{Album}";
}

/// <summary>A timed word (or character, for CJK) inside a word-synced lyric line.</summary>
public sealed record LyricWord(TimeSpan Start, TimeSpan Duration, string Text);

public sealed record LyricLine(TimeSpan Time, string Text, string? Translation, IReadOnlyList<LyricWord>? Words = null)
{
    /// <summary>Empty or symbol-only lines ("……", "-----") mark instrumental breaks.</summary>
    public bool IsInterlude => !Text.Any(char.IsLetterOrDigit);

    public string DisplayText => IsInterlude ? "♪" : Text;

    /// <summary>
    /// Start/end time of every character of <see cref="DisplayText"/>. Word-synced lines spread each word's
    /// duration over its characters; line-synced lines spread the line evenly up to <paramref name="lineEnd"/>.
    /// </summary>
    public (TimeSpan Start, TimeSpan End)[] CharTimes(TimeSpan lineEnd)
    {
        var text = DisplayText;
        var times = new (TimeSpan, TimeSpan)[text.Length];
        if (times.Length == 0)
            return times;

        if (Words is { Count: > 0 } words && !IsInterlude && string.Concat(words.Select(w => w.Text)) == Text)
        {
            int c = 0;
            foreach (var w in words)
            {
                var step = w.Text.Length > 0 ? w.Duration / w.Text.Length : TimeSpan.Zero;
                for (int i = 0; i < w.Text.Length && c < times.Length; i++, c++)
                    times[c] = (w.Start + step * i, w.Start + step * (i + 1));
            }
            return times;
        }

        // Line-synced: sweep the line over its gap (at most ~0.35 s per character, ending a little early).
        var span = lineEnd - Time - TimeSpan.FromMilliseconds(250);
        var max = TimeSpan.FromMilliseconds(350) * text.Length;
        if (span > max) span = max;
        if (span < TimeSpan.FromMilliseconds(300)) span = TimeSpan.FromMilliseconds(300);
        var each = span / text.Length;
        for (int i = 0; i < text.Length; i++)
            times[i] = (Time + each * i, Time + each * (i + 1));
        return times;
    }
}

public sealed record LyricsMatchInfo(string Title, string Artist, TimeSpan Duration, double Score, string ProviderId);

public sealed class Lyrics
{
    public static readonly Lyrics Empty = new([], false, "");

    public Lyrics(IReadOnlyList<LyricLine> lines, bool isSynced, string source, LyricsMatchInfo? match = null)
    {
        Lines = lines;
        IsSynced = isSynced;
        Source = source;
        Match = match;
    }

    public IReadOnlyList<LyricLine> Lines { get; }
    public bool IsSynced { get; }
    public bool HasWordTiming => Lines.Any(l => l.Words is { Count: > 0 });
    public string Source { get; }
    public LyricsMatchInfo? Match { get; }
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Index of the line active at <paramref name="position"/>, or -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        if (!IsSynced || Lines.Count == 0)
            return -1;

        int lo = 0, hi = Lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (Lines[mid].Time <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }
}
