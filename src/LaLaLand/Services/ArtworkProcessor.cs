using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace LaLaLand.Services;

/// <summary>Album artwork plus derived assets: a tiny pre-blurred backdrop and an accent colour.</summary>
/// <param name="Hash">Content hash of <see cref="Encoded"/>, used to detect real artwork changes.</param>
public sealed record ArtworkData(byte[] Encoded, string Hash, byte[] BackdropPixels, int BackdropSize, Color Accent)
{
    public IReadOnlyList<Color> LyricPalette { get; init; } = [];
}

public static class ArtworkProcessor
{
    private const int BackdropSize = 40;

    /// <summary>Reads the raw (encoded) thumbnail bytes, or null if there is no usable thumbnail.</summary>
    public static async Task<byte[]?> TryReadAsync(IRandomAccessStreamReference? reference, CancellationToken ct = default)
    {
        if (reference is null)
            return null;

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(ct);
            if (stream.Size == 0 || stream.Size > 32 * 1024 * 1024)
                return null;
            var buffer = await stream.ReadAsync(new Windows.Storage.Streams.Buffer((uint)stream.Size), (uint)stream.Size, InputStreamOptions.None).AsTask(ct);
            return buffer.ToArray();
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Error("Reading artwork failed", ex);
            return null;
        }
    }

    public static string HashOf(byte[] encoded) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(encoded));

    public static async Task<ArtworkData?> TryProcessAsync(byte[] encoded, string hash, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => ProcessAsync(encoded, hash, ct), ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Log.Error("Processing artwork failed", ex);
            return null;
        }
    }

    private static async Task<ArtworkData> ProcessAsync(byte[] encoded, string hash, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(encoded.AsBuffer()).AsTask(ct);
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        var transform = new BitmapTransform
        {
            ScaledWidth = BackdropSize,
            ScaledHeight = BackdropSize,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        var pixels = pixelData.DetachPixelData();

        var accent = ComputeAccent(pixels);
        Saturate(pixels, 1.35);
        CapLuminance(pixels, 0.42);
        for (int pass = 0; pass < 3; pass++)
            BoxBlur(pixels, BackdropSize, radius: 4);

        return new ArtworkData(encoded, hash, pixels, BackdropSize, accent) { LyricPalette = ComputeLyricPalette(pixels) };
    }

    private static IReadOnlyList<Color> ComputeLyricPalette(byte[] glow)
    {
        // Reuse the exact blurred/saturated glow pixels; lift their regional hues for readable foreground text.
        var colors = new List<Color>();
        for (int region = 0; region < 3; region++)
        {
            var samples = new List<byte>();
            for (int y = 0; y < BackdropSize; y++)
            for (int x = region * BackdropSize / 3; x < (region + 1) * BackdropSize / 3; x++)
            {
                int index = (y * BackdropSize + x) * 4;
                if (glow[index + 3] < 8) continue;
                for (int c = 0; c < 4; c++) samples.Add(glow[index + c]);
            }
            if (samples.Count > 0) colors.Add(ComputeAccent(samples.ToArray()));
        }
        return colors;
    }

    /// <summary>Saturation/brightness-weighted average so vivid regions dominate over greys.</summary>
    private static Color ComputeAccent(byte[] bgra)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double bb = bgra[i] / 255.0, gg = bgra[i + 1] / 255.0, rr = bgra[i + 2] / 255.0;
            double max = Math.Max(rr, Math.Max(gg, bb)), min = Math.Min(rr, Math.Min(gg, bb));
            double sat = max <= 0 ? 0 : (max - min) / max;
            double weight = 0.05 + sat * sat * max;
            r += rr * weight; g += gg * weight; b += bb * weight; total += weight;
        }
        r /= total; g /= total; b /= total;

        // Lift into a range that reads well on a dark island.
        RgbToHsl(r, g, b, out var h, out var s, out var l);
        l = Math.Clamp(l, 0.62, 0.78);
        s = s < 0.12 ? s : Math.Clamp(s * 1.15, 0.45, 0.9);
        HslToRgb(h, s, l, out r, out g, out b);
        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static void Saturate(byte[] bgra, double amount)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double gray = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
            for (int c = 0; c < 3; c++)
                bgra[i + c] = (byte)Math.Clamp(gray + (bgra[i + c] - gray) * amount, 0, 255);
        }
    }

    /// <summary>Keeps bright artwork from washing out white text: pixels brighter than <paramref name="max"/> are scaled down.</summary>
    private static void CapLuminance(byte[] bgra, double max)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double lum = (0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2]) / 255.0;
            double factor = lum > max ? max / lum : 1.0;
            // Also apply a gentle global dim so mid-tones sit comfortably under the text.
            factor *= 0.85;
            for (int c = 0; c < 3; c++)
                bgra[i + c] = (byte)(bgra[i + c] * factor);
        }
    }

    /// <summary>Separable box blur with clamped edges; three passes approximate a Gaussian.</summary>
    private static void BoxBlur(byte[] bgra, int size, int radius)
    {
        var tmp = new byte[bgra.Length];
        BlurPass(bgra, tmp, size, radius, horizontal: true);
        BlurPass(tmp, bgra, size, radius, horizontal: false);
    }

    private static void BlurPass(byte[] src, byte[] dst, int size, int radius, bool horizontal)
    {
        int window = radius * 2 + 1;
        for (int line = 0; line < size; line++)
        {
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++)
                    sum += src[Index(line, Math.Clamp(k, 0, size - 1)) + c];

                for (int x = 0; x < size; x++)
                {
                    dst[Index(line, x) + c] = (byte)(sum / window);
                    sum += src[Index(line, Math.Min(x + radius + 1, size - 1)) + c];
                    sum -= src[Index(line, Math.Max(x - radius, 0)) + c];
                }
            }
        }

        int Index(int lineIndex, int pos) => horizontal ? (lineIndex * size + pos) * 4 : (pos * size + lineIndex) * 4;
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min)
        {
            h = s = 0;
            return;
        }
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s == 0)
        {
            r = g = b = l;
            return;
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        r = Hue(p, q, h + 1.0 / 3);
        g = Hue(p, q, h);
        b = Hue(p, q, h - 1.0 / 3);

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
