using System.Globalization;
using Windows.UI;
using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LaLaLand.Controls;

internal static class LyricColors
{
    public static Color Parse(string? value)
    {
        string hex = value?.Trim().TrimStart('#') ?? "";
        if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Microsoft.UI.Colors.White;
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;

    public static Brush Create(LyricColorMode mode, string custom, ArtworkData? artwork,
        double gradientStrength = 1, double opacity = 1)
    {
        opacity = Unit(opacity);
        if (mode == LyricColorMode.Custom) return new SolidColorBrush(Parse(custom)) { Opacity = opacity };
        if (artwork?.LyricPalette is not { Count: > 0 } colors)
            return new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = opacity };
        double strength = Unit(gradientStrength);
        double red = colors.Average(c => c.R), green = colors.Average(c => c.G), blue = colors.Average(c => c.B);
        Color Mix(Color color) => Color.FromArgb(255,
            (byte)Math.Round(red + (color.R - red) * strength),
            (byte)Math.Round(green + (color.G - green) * strength),
            (byte)Math.Round(blue + (color.B - blue) * strength));
        if (strength == 0 || colors.Count == 1) return new SolidColorBrush(Mix(colors[0])) { Opacity = opacity };
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), Opacity = opacity };
        for (int i = 0; i < colors.Count; i++)
            gradient.GradientStops.Add(new GradientStop { Color = Mix(colors[i]), Offset = i / (double)(colors.Count - 1) });
        return gradient;
    }

    public static Brush ForToken(Brush foreground, double sourceX, double elementWidth, double totalWidth)
    {
        if (foreground is not LinearGradientBrush source) return foreground;
        // Map one gradient along the original line, rather than repeating the entire palette inside every word.
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(-sourceX / elementWidth, 0),
            EndPoint = new Point((Math.Max(1, totalWidth) - sourceX) / elementWidth, 0),
            Opacity = source.Opacity,
        };
        foreach (var stop in source.GradientStops)
            gradient.GradientStops.Add(new GradientStop { Color = stop.Color, Offset = stop.Offset });
        return gradient;
    }
}
