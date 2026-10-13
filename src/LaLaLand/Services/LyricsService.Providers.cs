using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaLaLand.Models;

namespace LaLaLand.Services;

public sealed partial class LyricsService
{
    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString() : "";
    private static double Number(JsonElement element, string property) =>
        double.TryParse(Text(element, property), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;
    private static string NestedText(JsonElement element, string property, string child) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? Text(value, child) : "";

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument?> QueryJsonAsync(string url, List<Exception> failures, CancellationToken ct)
    {
        try { return await GetJsonAsync(url, ct); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            failures.Add(ex);
            return null;
        }
    }

    private static void ReportUnavailable(List<Exception> failures)
    {
        if (failures.Count > 0) throw new HttpRequestException("歌词来源的部分查询不可用：" + failures[^1].Message, failures[^1],
            (failures[^1] as HttpRequestException)?.StatusCode);
    }

    private async Task<Candidate?> FromNeteaseAsync(TrackInfo track, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var failures = new List<Exception>();
        var queries = new[] { track.Title + " " + MainArtist(track.Artist), SearchTitle(track.Title) + " " + MainArtist(track.Artist), SearchTitle(track.Title) }.Distinct();
        foreach (var query in queries)
        {
            using var search = await QueryJsonAsync("https://music.163.com/api/search/get/web?s=" + Uri.EscapeDataString(query.Trim()) + "&type=1&offset=0&limit=20", failures, ct);
            if (search is null || !search.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) continue;
            var candidates = songs.EnumerateArray().Select(song =>
            {
                var title = Text(song, "name");
                var artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                    ? string.Join(" / ", artists.EnumerateArray().Select(a => Text(a, "name"))) : "";
                var album = NestedText(song, "album", "name");
                var duration = TimeSpan.FromMilliseconds(Math.Max(0, Number(song, "duration")));
                return new Candidate("网易云音乐", null, null, null, Score(track, title, artist, album, duration), title, artist, duration, Text(song, "id"));
            }).Where(c => double.IsFinite(c.Score) && c.Id.Length > 0 && !seen.Contains(c.Id)).OrderByDescending(c => c.Score).Take(3).ToList();
            foreach (var candidate in candidates)
            {
                seen.Add(candidate.Id);
                Candidate? lyrics;
                try { lyrics = await NeteaseLyricsAsync(candidate, ct); }
                catch (Exception ex) when (ex is HttpRequestException or JsonException) { failures.Add(ex); continue; }
                if (lyrics is not null && !ToLyrics(lyrics).IsEmpty) return lyrics;
            }
        }
        ReportUnavailable(failures);
        return null;
    }

    private async Task<Candidate?> NeteaseLyricsAsync(Candidate candidate, CancellationToken ct)
    {
        try
        {
            const string path = "/api/song/lyric/v1";
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["id"] = candidate.Id, ["cp"] = "false", ["tv"] = "0", ["lv"] = "0", ["rv"] = "0",
                ["kv"] = "0", ["yv"] = "0", ["ytv"] = "0", ["yrv"] = "0",
            });
            var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"nobody{path}use{json}md5forencrypt"))).ToLowerInvariant();
            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes("e82ckenh8dichen8");
            var encrypted = aes.EncryptEcb(Encoding.UTF8.GetBytes($"{path}-36cd479b6b5-{json}-36cd479b6b5-{digest}"), PaddingMode.PKCS7);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://interface3.music.163.com/eapi/song/lyric/v1");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["params"] = Convert.ToHexString(encrypted) });
            request.Headers.Referrer = new Uri("https://music.163.com/");
            request.Headers.TryAddWithoutValidation("Cookie", "os=pc; appver=3.0.0");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var lyrics = WithNeteaseLyrics(candidate, document.RootElement);
            if (!ToLyrics(lyrics).IsEmpty) return lyrics;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Error("Netease word lyrics unavailable, trying LRC", ex); }
        using var fallback = await GetJsonAsync($"https://music.163.com/api/song/lyric?id={Uri.EscapeDataString(candidate.Id)}&lv=1&tv=-1", ct);
        return fallback is null ? null : WithNeteaseLyrics(candidate, fallback.RootElement);
    }

    private static Candidate WithNeteaseLyrics(Candidate candidate, JsonElement root) => candidate with
    {
        Lrc = NestedText(root, "lrc", "lyric"), Translation = NestedText(root, "tlyric", "lyric"), Yrc = NestedText(root, "yrc", "lyric"),
    };

    private async Task<Candidate?> FromLrclibAsync(TrackInfo track, CancellationToken ct)
    {
        string Parameters(string title, string artist) => "track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist);
        Candidate? plainFallback = null;
        var failures = new List<Exception>();
        using (var exact = await QueryJsonAsync("https://lrclib.net/api/get?" + Parameters(track.Title, track.Artist) +
            "&album_name=" + Uri.EscapeDataString(track.Album) + "&duration=" + track.Duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture), failures, ct))
        {
            if (exact is not null && LrclibCandidate(track, exact.RootElement) is { } found && !ToLyrics(found).IsEmpty)
            {
                if (ToLyrics(found).IsSynced) return found;
                plainFallback = found;
            }
        }
        var queries = new[] { Parameters(track.Title, MainArtist(track.Artist)), Parameters(SearchTitle(track.Title), MainArtist(track.Artist)),
            "q=" + Uri.EscapeDataString(SearchTitle(track.Title) + " " + MainArtist(track.Artist)),
            "q=" + Uri.EscapeDataString(SearchTitle(track.Title)) }.Distinct();
        foreach (var query in queries)
        {
            JsonDocument? searchResult;
            try { searchResult = await QueryJsonAsync("https://lrclib.net/api/search?" + query, failures, ct); }
            catch (Exception) when (plainFallback is not null) { return plainFallback; }
            using var search = searchResult;
            if (search is null || search.RootElement.ValueKind != JsonValueKind.Array) continue;
            var best = search.RootElement.EnumerateArray().Select(song => LrclibCandidate(track, song)).Where(c => c is not null)
                .Select(c => c!).Where(c => !ToLyrics(c).IsEmpty).OrderByDescending(c => c.Score + Quality(c)).FirstOrDefault();
            if (best is not null)
            {
                if (ToLyrics(best).IsSynced) return best;
                if (plainFallback is null || best.Score > plainFallback.Score) plainFallback = best;
            }
        }
        if (plainFallback is null) ReportUnavailable(failures);
        return plainFallback;
    }

    private static Candidate? LrclibCandidate(TrackInfo track, JsonElement song)
    {
        var title = Text(song, "trackName");
        var artist = Text(song, "artistName");
        var duration = TimeSpan.FromSeconds(Math.Max(0, Number(song, "duration")));
        var score = Score(track, title, artist, Text(song, "albumName"), duration);
        if (!double.IsFinite(score)) return null;
        var synced = Text(song, "syncedLyrics");
        return new Candidate("LRCLIB", string.IsNullOrWhiteSpace(synced) ? Text(song, "plainLyrics") : synced,
            null, null, score, title, artist, duration, Text(song, "id"));
    }

    private async Task<Candidate?> FromKugouAsync(TrackInfo track, CancellationToken ct)
    {
        var seen = new HashSet<string>();
        var failures = new List<Exception>();
        var queries = new[] { track.Title + " " + MainArtist(track.Artist), SearchTitle(track.Title) + " " + MainArtist(track.Artist), SearchTitle(track.Title) }.Distinct();
        foreach (var query in queries)
        {
            using var search = await QueryJsonAsync("https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" +
                Uri.EscapeDataString(query.Trim()) + "&duration=" + Math.Round(track.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), failures, ct);
            if (search is null || !search.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) continue;
            var matches = candidates.EnumerateArray().Select(song =>
            {
                var title = Text(song, "song");
                var artist = Text(song, "singer");
                var duration = TimeSpan.FromMilliseconds(Math.Max(0, Number(song, "duration")));
                return (Candidate: new Candidate("酷狗音乐", null, null, null, Score(track, title, artist, "", duration), title, artist, duration, Text(song, "id")),
                    AccessKey: Text(song, "accesskey"));
            }).Where(c => double.IsFinite(c.Candidate.Score) && c.Candidate.Id.Length > 0 && !seen.Contains(c.Candidate.Id))
                .OrderByDescending(c => c.Candidate.Score).Take(3).ToList();
            foreach (var match in matches)
            {
                seen.Add(match.Candidate.Id);
                using var download = await QueryJsonAsync("https://lyrics.kugou.com/download?ver=1&client=pc&id=" + Uri.EscapeDataString(match.Candidate.Id) +
                    "&accesskey=" + Uri.EscapeDataString(match.AccessKey) + "&fmt=lrc&charset=utf8", failures, ct);
                if (download is null) continue;
                var content = Text(download.RootElement, "content");
                if (content.Length == 0) continue;
                try
                {
                    var candidate = match.Candidate with { Lrc = Encoding.UTF8.GetString(Convert.FromBase64String(content)) };
                    if (!ToLyrics(candidate).IsEmpty) return candidate;
                }
                catch (FormatException) { /* Malformed candidate; another matching recording may work. */ }
            }
        }
        ReportUnavailable(failures);
        return null;
    }
}
