using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using LaLaLand.Models;

namespace LaLaLand.Services;

public sealed partial class LyricsService
{
    private static string Normalize(string value)
    {
        var decomposed = SimplifyChinese(value).Normalize(NormalizationForm.FormKD);
        return string.Concat(decomposed.Where(c => char.IsLetterOrDigit(c) &&
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out] char[]? destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);

    private static string SimplifyChinese(string value)
    {
        if (!OperatingSystem.IsWindows() || !value.Any(c => c is >= '\u3400' and <= '\u9fff')) return value;
        const uint simplifiedChinese = 0x02000000;
        int size = LCMapStringEx("zh-CN", simplifiedChinese, value, value.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (size <= 0) return value;
        var buffer = new char[size];
        int written = LCMapStringEx("zh-CN", simplifiedChinese, value, value.Length, buffer, size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return written > 0 ? new string(buffer, 0, written) : value;
    }

    // This artist has both spellings in current provider metadata for the same album recording.
    private static string ArtistIdentity(string artist)
    {
        var normalized = Normalize(artist);
        return normalized is "jaychou" or "周杰伦" ? "周杰伦" : normalized;
    }

    [GeneratedRegex(@"[（(\[【]([^）)\]】]+)[）)\]】]")]
    private static partial Regex DecorationRegex();
    [GeneratedRegex(@"\s+(?:-|–|—)\s+(.+)$")]
    private static partial Regex SuffixRegex();
    [GeneratedRegex(@"\s*(?:feat\.?|ft\.?|featuring)\s+.+$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatureRegex();
    [GeneratedRegex(@"\s*(?:/|&|,|;|、|，|；|\bfeat\.?\s|\bft\.?\s|\bfeaturing\s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparatorRegex();

    private static HashSet<string> Versions(string title)
    {
        var labels = DecorationRegex().Matches(title).Select(m => m.Groups[1].Value).ToList();
        var suffix = SuffixRegex().Match(title);
        if (suffix.Success) labels.Add(suffix.Groups[1].Value);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var value = label.ToLowerInvariant();
            if (Regex.IsMatch(value, @"\blive\b") || value.Contains("现场") || value.Contains("演唱会")) versions.Add("live");
            if (value.Contains("acoustic") || value.Contains("unplugged") || value.Contains("不插电")) versions.Add("acoustic");
            if (value.Contains("remix") || value.Contains("混音")) versions.Add("remix");
            if (value.Contains("cover") || value.Contains("翻唱")) versions.Add("cover");
            if (value.Contains("instrumental") || value.Contains("伴奏") || value.Contains("纯音乐")) versions.Add("instrumental");
            if (value.Contains("radio edit") || value.Contains("电台版")) versions.Add("radio");
            if (value.Contains("sped up") || value.Contains("加速")) versions.Add("sped-up");
            if (value.Contains("slowed") || value.Contains("降速")) versions.Add("slowed");
        }
        return versions;
    }

    private static bool IsDecoration(string label)
    {
        var value = SimplifyChinese(label).ToLowerInvariant();
        return Versions("(" + label + ")").Count > 0 || value.Contains("remaster") || value.Contains("重制") ||
            value.Contains("original version") || value.Contains("album version") || value.Contains("专辑版") ||
            value.Contains("feat.") || value.Contains("featuring") || value.Contains("ft.") ||
            Regex.IsMatch(value, @"(?:电视剧|电影|影视|剧集|网剧|动画|动漫|《[^》]+》).*(?:片头曲|片尾曲|主题曲|插曲|原声带)") ||
            Regex.IsMatch(value, @"\b(?:ost|original soundtrack)\b|\bfrom\b.+\b(?:movie|film|series|drama|soundtrack)\b");
    }

    private static string SearchTitle(string title)
    {
        var clean = DecorationRegex().Replace(title, m => IsDecoration(m.Groups[1].Value) ? "" : m.Value);
        var suffix = SuffixRegex().Match(clean);
        if (suffix.Success && IsDecoration(suffix.Groups[1].Value)) clean = clean[..suffix.Index];
        return FeatureRegex().Replace(clean, "").Trim();
    }

    private static string MainArtist(string artist) => ArtistSeparatorRegex().Split(artist).FirstOrDefault(a => a.Length > 0) ?? artist;

    private static bool ArtistMatches(string expected, string actual)
    {
        if (ArtistIdentity(expected) == ArtistIdentity(actual)) return true;
        var expectedParts = ArtistSeparatorRegex().Split(expected).Select(ArtistIdentity).Where(p => p.Length > 0).ToHashSet();
        var actualParts = ArtistSeparatorRegex().Split(actual).Select(ArtistIdentity).Where(p => p.Length > 0).ToHashSet();
        // Do not accept a different primary singer merely because the requested artist appears in the credits.
        return expectedParts.Count > 0 && actualParts.Count > 0 &&
            (ArtistIdentity(MainArtist(expected)) == ArtistIdentity(MainArtist(actual)) ||
                expectedParts.Count > 1 && expectedParts.SetEquals(actualParts));
    }

    [GeneratedRegex(@"(?:feat\.?|ft\.?|featuring)\s+([^)\]）】]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TitleFeatureRegex();

    // Soundtrack tracks are often credited to the franchise in the player but to the singer in lyric providers.
    private static bool FeaturedArtistMatches(string title, string actual)
    {
        var main = ArtistIdentity(MainArtist(actual));
        return main.Length > 0 && TitleFeatureRegex().Matches(title).Any(m =>
            ArtistSeparatorRegex().Split(m.Groups[1].Value).Select(ArtistIdentity).Contains(main));
    }

    private static double Score(TrackInfo track, string title, string artist, string album, TimeSpan duration)
    {
        var expectedTitle = Normalize(track.Title);
        var actualTitle = Normalize(title);
        if (actualTitle.Length == 0 || !Versions(track.Title).SetEquals(Versions(title))) return double.NegativeInfinity;
        bool exact = expectedTitle == actualTitle;
        if (!exact && Normalize(SearchTitle(track.Title)) != Normalize(SearchTitle(title))) return double.NegativeInfinity;
        double score = exact ? 4 : 3.6;
        bool knownArtist = !string.IsNullOrWhiteSpace(track.Artist);
        if (knownArtist)
        {
            bool listed = ArtistMatches(track.Artist, artist);
            if (!listed && !FeaturedArtistMatches(track.Title, artist)) return double.NegativeInfinity;
            score += listed && ArtistIdentity(track.Artist) == ArtistIdentity(artist) ? 3 : 2.6;
        }
        bool knownDuration = track.Duration.TotalSeconds > 0 && duration.TotalSeconds > 0;
        if (knownDuration)
        {
            var delta = Math.Abs((track.Duration - duration).TotalSeconds);
            if (delta > Math.Max(12, track.Duration.TotalSeconds * 0.06)) return double.NegativeInfinity;
            score += delta <= 3 ? 2 : delta <= 8 ? 1.2 : 0.2;
        }
        if (!string.IsNullOrWhiteSpace(track.Album) && Normalize(track.Album) == Normalize(album)) score += 1;
        // Without an artist, require an exact title and a nearby recording duration.
        if (!knownArtist && (!exact || !knownDuration || Math.Abs((track.Duration - duration).TotalSeconds) > 3))
            return double.NegativeInfinity;
        return score;
    }
}
