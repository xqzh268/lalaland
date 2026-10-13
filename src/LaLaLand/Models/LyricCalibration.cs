using System.Security.Cryptography;
using System.Text;

namespace LaLaLand.Models;

/// <summary>Maps the player's recording clock to a lyric clock, and inversely for line seeking.</summary>
public sealed record LyricCalibration(int OffsetMs = 0, double TimeScale = 1)
{
    public static readonly LyricCalibration Default = new();
    public double SafeScale => double.IsFinite(TimeScale) ? Math.Clamp(TimeScale, 0.9, 1.1) : 1;
    public int SafeOffsetMs => Math.Clamp(OffsetMs, -30000, 30000);
    public TimeSpan Apply(TimeSpan mediaPosition, int globalOffsetMs) =>
        mediaPosition * SafeScale + TimeSpan.FromMilliseconds(SafeOffsetMs + Math.Clamp(globalOffsetMs, -2000, 2000));
    public TimeSpan ToMedia(TimeSpan lyricPosition, int globalOffsetMs) =>
        (lyricPosition - TimeSpan.FromMilliseconds(SafeOffsetMs + Math.Clamp(globalOffsetMs, -2000, 2000))) / SafeScale;
    public TimeSpan RealDelay(TimeSpan lyricGap, double playbackRate) =>
        lyricGap / (SafeScale * (double.IsFinite(playbackRate) && playbackRate > 0 ? playbackRate : 1));

    public static string Key(TrackInfo track)
    {
        var value = track.Key.Normalize(NormalizationForm.FormKC).ToLowerInvariant() + "|" + Math.Round(track.Duration.TotalSeconds);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    }
}
