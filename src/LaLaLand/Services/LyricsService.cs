using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaLaLand.Models;

namespace LaLaLand.Services;

/// <summary>Bounded multi-provider lookup, recording-specific cache and explicit local overrides.</summary>
public sealed partial class LyricsService
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private const int CacheVersion = 4;
    private sealed record CacheEntry(string Source, string? Lrc, string? Translation, string? Yrc,
        double Score, string Title, string Artist, double Duration, string Id, int Version = CacheVersion);
    private sealed record Candidate(string Source, string? Lrc, string? Translation, string? Yrc,
        double Score, string Title, string Artist, TimeSpan Duration, string Id);
    private sealed record LocalEntry(string? Lrc, string? Yrc, string FileName);

    public event Action<TrackInfo?>? Changed;
    public string LastLookupMessage { get; private set; } = "";

    public LyricsService(HttpClient? http = null, string? cacheDirectory = null)
    {
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = TimeSpan.FromSeconds(7) };
        if (http is null)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("LaLaLand/0.1 (Windows desktop lyrics)");
        _cacheDir = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaLaLand", "lyrics");
    }

    public async Task<Lyrics> GetAsync(TrackInfo track, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastLookupMessage = "";
        if (TryReadLocal(track) is { } local)
            return local;
        var cachePath = Path.Combine(_cacheDir, CacheKey(track) + ".json");
        if (TryReadCache(cachePath) is { } cached)
        {
            var parsed = ToLyrics(new Candidate(cached.Source, cached.Lrc, cached.Translation, cached.Yrc,
                cached.Score, cached.Title, cached.Artist, TimeSpan.FromSeconds(cached.Duration), cached.Id));
            if (!parsed.IsEmpty) return parsed;
        }

        async Task<(Candidate? Candidate, bool Failed)> Lookup(string provider, Func<CancellationToken, Task<Candidate?>> fetch)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { return (await fetch(budget.Token), false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log.Info($"Lyrics {provider} timed out for {track.Title} · {track.Artist} ({track.Duration.TotalSeconds:F1}s)");
                    return (null, true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"Lyrics {provider} unavailable for {track.Title} · {track.Artist} ({track.Duration.TotalSeconds:F1}s)", ex);
                    if (attempt > 0 || !IsTransient(ex)) return (null, true);
                    try { await Task.Delay(350, budget.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, true); }
                }
            }
            return (null, true);
        }

        var results = await Task.WhenAll(
            Lookup("网易云音乐", token => FromNeteaseAsync(track, token)),
            Lookup("LRCLIB", token => FromLrclibAsync(track, token)),
            Lookup("酷狗音乐", token => FromKugouAsync(track, token)),
            Lookup("QQ音乐", token => FromQqMusicAsync(track, token)));
        ct.ThrowIfCancellationRequested();
        var best = results.Where(r => r.Candidate is not null).Select(r => r.Candidate!)
            .Where(c => !ToLyrics(c).IsEmpty)
            // Identity confidence is worth more than the small timing-quality bonus.
            .OrderByDescending(c => c.Score + Quality(c)).FirstOrDefault();
        if (best is null)
        {
            // Never persist an outage or miss for days. A manual retry must always reach the providers.
            LastLookupMessage = results.Any(r => r.Failed)
                ? "部分歌词来源暂时不可用，未找到可靠匹配；可重试或导入本地歌词"
                : "未找到匹配的歌词；可重新搜索或导入本地LRC/YRC歌词";
            return Lyrics.Empty;
        }
        var entry = new CacheEntry(best.Source, best.Lrc, best.Translation, best.Yrc, best.Score,
            best.Title, best.Artist, best.Duration.TotalSeconds, best.Id);
        WriteJson(cachePath, entry);
        var lyrics = ToLyrics(best);
        Log.Info($"Lyrics matched: {best.Source}, score {best.Score:F2}, duration delta {(track.Duration - best.Duration).TotalSeconds:F1}s");
        return lyrics;
    }

    private static bool IsTransient(Exception exception) => exception is JsonException ||
        exception is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)http.StatusCode.Value >= 500);

    private static double Quality(Candidate candidate)
    {
        var parsed = ToLyrics(candidate);
        double timing = parsed.HasWordTiming ? 0.35 : parsed.IsSynced ? 0.2 : 0;
        // A translated recording is worth more than a matching album name, which only separates reissues of one recording.
        bool translated = parsed.Lines.Count > 0 &&
            parsed.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Translation)) >= Math.Max(1, parsed.Lines.Count / 3);
        return timing + (translated ? 1.1 : 0);
    }

    private static Lyrics ToLyrics(Candidate candidate)
    {
        var lyrics = !string.IsNullOrWhiteSpace(candidate.Yrc)
            ? YrcParser.Parse(candidate.Yrc, candidate.Translation, candidate.Source) : Lyrics.Empty;
        if (lyrics.IsEmpty)
            lyrics = LrcParser.Parse(candidate.Lrc, candidate.Translation, candidate.Source);
        return new Lyrics(lyrics.Lines, lyrics.IsSynced, lyrics.Source,
            new LyricsMatchInfo(candidate.Title, candidate.Artist, candidate.Duration, candidate.Score, candidate.Id));
    }

    public void Refresh(TrackInfo track)
    {
        var path = Path.Combine(_cacheDir, CacheKey(track) + ".json");
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke(track);
    }

    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
                foreach (var path in Directory.EnumerateFiles(_cacheDir, "*.json", SearchOption.TopDirectoryOnly))
                    File.Delete(path);
            Changed?.Invoke(null);
        }
        catch (Exception ex) { Log.Error("Clearing lyric cache failed", ex); }
    }

    private string LocalPath(TrackInfo track) => Path.Combine(_cacheDir, "local", CacheKey(track) + ".json");
    public bool HasLocalLyrics(TrackInfo track) => File.Exists(LocalPath(track));

    public async Task ImportAsync(TrackInfo track, string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("歌词文件不能超过2MB");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".lrc" or ".yrc" or ".txt"))
            throw new InvalidOperationException("请选择LRC、YRC或TXT歌词文件");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        var entry = new LocalEntry(extension == ".yrc" ? null : text, extension == ".yrc" ? text : null, Path.GetFileName(path));
        var parsed = extension == ".yrc" ? YrcParser.Parse(text, null, "本地歌词") : LrcParser.Parse(text, null, "本地歌词");
        if (parsed.IsEmpty)
            throw new InvalidOperationException("文件中没有可读取的歌词");
        ct.ThrowIfCancellationRequested();
        WriteJson(LocalPath(track), entry, throwOnFailure: true);
        Changed?.Invoke(track);
    }

    public void RemoveLocalLyrics(TrackInfo track)
    {
        var path = LocalPath(track);
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke(track);
    }

    private Lyrics? TryReadLocal(TrackInfo track)
    {
        try
        {
            var path = LocalPath(track);
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<LocalEntry>(File.ReadAllText(path));
            if (entry is null) return null;
            var lyrics = entry.Yrc is { Length: > 0 } yrc
                ? YrcParser.Parse(yrc, null, "本地歌词") : LrcParser.Parse(entry.Lrc, null, "本地歌词");
            return lyrics.IsEmpty ? null : lyrics;
        }
        catch (Exception ex) { Log.Error("Reading local lyrics failed", ex); return null; }
    }

    private static string CacheKey(TrackInfo track)
    {
        var identity = $"{Normalize(track.Title)}|{Normalize(track.Artist)}|{Normalize(track.Album)}|{Math.Round(track.Duration.TotalSeconds)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    private static CacheEntry? TryReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
            return entry is { Version: CacheVersion } && entry.Source != "none" ? entry : null;
        }
        catch { return null; }
    }

    private static void WriteJson<T>(string path, T entry, bool throwOnFailure = false)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Writing lyric cache failed", ex);
            if (throwOnFailure) throw;
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
