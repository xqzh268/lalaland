using System.Diagnostics;
using System.Numerics;
using LaLaLand.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Views;

/// <summary>
/// "Water droplet" edge ripples. On a beat a drop lands at a random point of the island's rim and a small wave
/// packet runs along the edge in both directions, damped in time and distance — the outline itself stays put.
/// <para>
/// Normally the island is a compositor-animated rounded rectangle. While ripples are alive and the shape is at
/// rest, the rim is sampled every few DIPs, displaced along its normal, and the resulting path temporarily
/// replaces the clip, the glass edge, the sheen and the system blur mask. With no ripple left (or as soon as a
/// morph starts) the rounded rectangle comes back; both coincide at zero amplitude, so the swap is invisible.
/// </para>
/// </summary>
public sealed partial class IslandWindow
{
    private readonly record struct Ripple(double Origin, double Amplitude, double Born, double Wavelength, double Speed, double Phase);

    private const double RippleLife = 1.1;      // s
    private const double RippleDecay = 0.32;    // s, temporal damping
    private const double RippleReach = 60;      // DIP, spatial damping
    private const double SampleStep = 3;        // DIP between rim samples

    private readonly List<Ripple> _ripples = [];
    private readonly Random _random = new();
    private readonly Stopwatch _rippleClock = Stopwatch.StartNew();
    private readonly Dictionary<string, double> _shapeTarget = new() { ["W"] = 0, ["H"] = 0, ["R"] = 0 };
    private double _shapeSettledAt;              // _rippleClock seconds when the last shape animation ends
    private bool _rippleRendering;
    private bool _rippleShown;

    private CompositionGeometricClip _rectClip = null!;
    private CompositionGeometry _edgeRectGeometry = null!;
    private CompositionGeometry _sheenRectGeometry = null!;
    private CompositionPathGeometry? _ripplePathGeometry;
    private CompositionGeometricClip? _rippleClip;
    private CanvasDevice? _canvasDevice;

    /// <summary>Remembers where the shape is heading so ripples can be drawn around its resting outline.</summary>
    private void NoteShape(string property, double value, double durationMs)
    {
        _shapeTarget[property] = value;
        _shapeSettledAt = Math.Max(_shapeSettledAt, _rippleClock.Elapsed.TotalSeconds + durationMs / 1000.0);
        if (durationMs > 0)
            HideRipplePath(); // morphs are compositor-driven; ripples pause until the shape rests
    }

    private void AddRipple(float strength)
    {
        if (!_settings.Current.DropletEffect || LightweightMode || !_isShown)
            return;
        bool collapsed = _state == IslandState.Collapsed;
        double now = _rippleClock.Elapsed.TotalSeconds;
        // Irregular on purpose: random landing point, wavelength, speed and phase.
        _ripples.Add(new Ripple(
            Origin: _random.NextDouble(),
            Amplitude: (collapsed ? 0.6 : 0.9) + strength * (collapsed ? 0.9 : 1.4),
            Born: now,
            Wavelength: (collapsed ? 16 : 22) * (0.75 + _random.NextDouble() * 0.5),
            Speed: (collapsed ? 120 : 160) * (0.8 + _random.NextDouble() * 0.4),
            Phase: _random.NextDouble() * Math.PI * 2));
        if (_ripples.Count > 6)
            _ripples.RemoveAt(0);

        // Rendered by the shared frame clock (see IslandWindow.Frames), in step with the bars and sweeps.
        _rippleRendering = true;
        EnsureFrames();
    }

    private void StopRipples()
    {
        _ripples.Clear();
        HideRipplePath();
        if (_rippleRendering)
        {
            _rippleRendering = false;
        }
    }

    /// <summary>Called by the frame clock while ripples are alive.</summary>
    private void RippleFrame()
    {
        double now = _rippleClock.Elapsed.TotalSeconds;
        _ripples.RemoveAll(r => now - r.Born > RippleLife);
        if (_ripples.Count == 0 || !_settings.Current.DropletEffect || LightweightMode || !_isShown)
        {
            StopRipples();
            return;
        }
        if (now < _shapeSettledAt)
        {
            HideRipplePath();
            return;
        }

        try
        {
            ShowRipplePath(now);
        }
        catch (Exception ex)
        {
            Services.Log.Error("Ripple rendering failed; disabling ripples for this session", ex);
            StopRipples();
            _settings.Current.DropletEffect = false;
        }
    }

    private void ShowRipplePath(double now)
    {
        double w = _shapeTarget["W"], h = _shapeTarget["H"];
        double r = Math.Min(_shapeTarget["R"], h / 2);
        double left = _ax * (IslandMaxWidth - w), top = _ay * (IslandMaxHeight - h);

        _canvasDevice ??= CanvasDevice.GetSharedDevice();
        using var builder = new CanvasPathBuilder(_canvasDevice);
        var samples = SampleRim(left, top, w, h, r);
        double perimeter = samples[^1].S;
        for (int i = 0; i < samples.Count - 1; i++)
        {
            var (p, n, s) = samples[i];
            var q = p + n * (float)Displacement(s, perimeter, now);
            if (i == 0) builder.BeginFigure(q);
            else builder.AddLine(q);
        }
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var geometry = CanvasGeometry.CreatePath(builder);

        if (_ripplePathGeometry is null)
        {
            _ripplePathGeometry = _compositor.CreatePathGeometry();
            _rippleClip = _compositor.CreateGeometricClip(_ripplePathGeometry);
        }
        _ripplePathGeometry.Path = new CompositionPath(geometry);

        // The system-compositor blur mask lives in window pixels.
        float scale = _backdrop.Scale, margin = _backdrop.Margin;
        using var pixels = geometry.Transform(Matrix3x2.CreateTranslation(margin, margin) * Matrix3x2.CreateScale(scale));
        _backdrop.SetRipplePath(pixels);

        if (!_rippleShown)
        {
            _rippleShown = true;
            Anim.Visual(Island).Clip = _rippleClip;
            _edgeShape.Geometry = _ripplePathGeometry;
            _sheenShape.Geometry = _ripplePathGeometry;
        }
    }

    private void HideRipplePath()
    {
        if (!_rippleShown)
            return;
        _rippleShown = false;
        Anim.Visual(Island).Clip = _rectClip;
        _edgeShape.Geometry = _edgeRectGeometry;
        _sheenShape.Geometry = _sheenRectGeometry;
        _backdrop.SetRipplePath(null);
    }

    /// <summary>Outward displacement at arc length <paramref name="s"/>: travelling, damped wave packets.</summary>
    private double Displacement(double s, double perimeter, double now)
    {
        double d = 0;
        foreach (var rp in _ripples)
        {
            double age = now - rp.Born;
            double origin = rp.Origin * perimeter;
            double dist = Math.Abs(s - origin);
            dist = Math.Min(dist, perimeter - dist);           // around the loop, both directions
            double front = rp.Speed * age;
            if (dist > front + rp.Wavelength)
                continue;                                       // the wave has not reached this point yet
            double envelope = rp.Amplitude * Math.Exp(-age / RippleDecay) * Math.Exp(-dist / RippleReach);
            double edge = Math.Clamp((front + rp.Wavelength - dist) / rp.Wavelength, 0, 1); // soft wavefront
            d += envelope * edge * Math.Sin(2 * Math.PI * (dist - front) / rp.Wavelength + rp.Phase);
        }
        return d;
    }

    /// <summary>Points, outward normals and arc lengths around a rounded rectangle, clockwise from the top-left.</summary>
    private static List<(Vector2 P, Vector2 N, double S)> SampleRim(double left, double top, double w, double h, double r)
    {
        var result = new List<(Vector2, Vector2, double)>();
        double s = 0;
        double straightW = Math.Max(0, w - 2 * r), straightH = Math.Max(0, h - 2 * r);

        void Line(double x0, double y0, double x1, double y1, Vector2 normal)
        {
            double length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = Math.Max(1, (int)(length / SampleStep));
            for (int i = 0; i < steps; i++)
            {
                double t = (double)i / steps;
                result.Add((new Vector2((float)(x0 + (x1 - x0) * t), (float)(y0 + (y1 - y0) * t)), normal, s + length * t));
            }
            s += length;
        }

        void Arc(double cx, double cy, double from)
        {
            double length = Math.PI / 2 * r;
            int steps = Math.Max(2, (int)(length / SampleStep));
            for (int i = 0; i < steps; i++)
            {
                double a = from + Math.PI / 2 * i / steps;
                var n = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
                result.Add((new Vector2((float)(cx + r * n.X), (float)(cy + r * n.Y)), n, s + length * i / steps));
            }
            s += length;
        }

        Line(left + r, top, left + r + straightW, top, new Vector2(0, -1));
        Arc(left + w - r, top + r, -Math.PI / 2);
        Line(left + w, top + r, left + w, top + r + straightH, new Vector2(1, 0));
        Arc(left + w - r, top + h - r, 0);
        Line(left + w - r, top + h, left + r, top + h, new Vector2(0, 1));
        Arc(left + r, top + h - r, Math.PI / 2);
        Line(left, top + h - r, left, top + r, new Vector2(-1, 0));
        Arc(left + r, top + r, Math.PI);
        result.Add((result[0].Item1, result[0].Item2, s)); // closing sample carries the total length
        return result;
    }
}
