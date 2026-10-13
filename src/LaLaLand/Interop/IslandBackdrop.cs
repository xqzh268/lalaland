using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;
using WinComp = Windows.UI.Composition;

namespace LaLaLand.Interop;

/// <summary>
/// Backdrop of the island window.
/// <list type="bullet">
/// <item>As a <see cref="SystemBackdrop"/> it paints nothing, so everything outside the island is transparent.</item>
/// <item>Underneath the WinUI content it owns a system-compositor visual (via a DesktopWindowTarget) filled with the
/// host-backdrop brush — the real, system-blurred desktop that Windows Acrylic is built from — clipped to the
/// island's rounded rectangle.</item>
/// </list>
/// The clip lives in the <em>system</em> compositor and mirrors the island's WinUI-side clip: same property names,
/// same expressions, same easing curves, so both shapes move in lock-step.
/// </summary>
internal sealed class IslandBackdrop : SystemBackdrop
{
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        void EnsureOnThread(uint threadId);
    }

    private static IntPtr s_dispatcherQueueController;

    private WinComp.Compositor? _compositor;
    private WinComp.CompositionBrush? _clearBrush;
    private WinComp.Desktop.DesktopWindowTarget? _windowTarget;
    private WinComp.SpriteVisual? _blurVisual;
    private WinComp.CompositionPropertySet? _props;
    private WinComp.CompositionGeometricClip? _rectClip;
    private WinComp.CompositionGeometricClip? _pathClip;
    private WinComp.CompositionPathGeometry? _pathGeometry;

    // Values remembered until the layer is attached
    private readonly Dictionary<string, float> _values = new()
    {
        ["W"] = 0, ["H"] = 0, ["R"] = 0, ["AX"] = 0.5f, ["AY"] = 0, ["MaxW"] = 0, ["MaxH"] = 0,
        ["Margin"] = 0, ["Scale"] = 1, ["Pop"] = 1,
    };
    private bool _blurEnabled;

    public bool BlurEnabled
    {
        get => _blurEnabled;
        set
        {
            _blurEnabled = value;
            if (_blurVisual is not null)
                _blurVisual.IsVisible = value;
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        EnsureCompositor();
        connectedTarget.SystemBackdrop = _clearBrush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    /// <summary>Creates the blur layer behind the window's XAML content.</summary>
    public void Attach(IntPtr hwnd)
    {
        if (_windowTarget is not null)
            return;
        EnsureCompositor();
        var c = _compositor!;

        try
        {
            var interop = c.As<ICompositorDesktopInterop>();
            interop.CreateDesktopWindowTarget(hwnd, false, out var targetPtr);
            _windowTarget = WinComp.Desktop.DesktopWindowTarget.FromAbi(targetPtr);
            Marshal.Release(targetPtr);
        }
        catch (Exception ex)
        {
            Services.Log.Error("Creating the blur layer failed; glass materials will fall back to tint only", ex);
            return;
        }

        _props = c.CreatePropertySet();
        foreach (var (name, value) in _values)
            _props.InsertScalar(name, value);

        // Geometry in physical pixels: (margin + anchored offset) * scale, scaled by Pop around the anchor point.
        const string ax = "(p.Margin + p.AX * p.MaxW)";
        const string ay = "(p.Margin + p.AY * p.MaxH)";
        const string left = "(p.Margin + p.AX * (p.MaxW - p.W))";
        const string top = "(p.Margin + p.AY * (p.MaxH - p.H))";
        const string width = "p.W";
        const string height = "p.H";
        const string radius = "Min(p.R, p.H * 0.5)";

        var geometry = c.CreateRoundedRectangleGeometry();
        Bind(geometry, "Offset", $"Vector2(({ax} + p.Pop * ({left} - {ax})) * p.Scale, ({ay} + p.Pop * ({top} - {ay})) * p.Scale)");
        Bind(geometry, "Size", $"Vector2({width} * p.Pop * p.Scale, {height} * p.Pop * p.Scale)");
        Bind(geometry, "CornerRadius", $"Vector2({radius} * p.Pop * p.Scale, {radius} * p.Pop * p.Scale)");

        _blurVisual = c.CreateSpriteVisual();
        _blurVisual.RelativeSizeAdjustment = Vector2.One;
        _blurVisual.Brush = c.CreateHostBackdropBrush();
        _blurVisual.Clip = _rectClip = c.CreateGeometricClip(geometry);
        _blurVisual.IsVisible = _blurEnabled;
        _windowTarget.Root = _blurVisual;
    }

    private void EnsureCompositor()
    {
        if (_compositor is not null)
            return;
        EnsureSystemDispatcherQueue();
        _compositor = new WinComp.Compositor();
        _clearBrush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    private void Bind(WinComp.CompositionObject target, string property, string expression)
    {
        var animation = _compositor!.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _props!);
        target.StartAnimation(property, animation);
    }

    // ---------------------------------------------------------------- shape control (mirrors IslandWindow.Motion)

    public void Set(string name, float value)
    {
        _values[name] = value;
        _props?.InsertScalar(name, value);
    }

    public void Animate(string name, float value, TimeSpan duration, Vector2 cp1, Vector2 cp2)
    {
        _values[name] = value;
        if (_props is null || _compositor is null)
            return;
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, value, _compositor.CreateCubicBezierEasingFunction(cp1, cp2));
        a.Duration = duration;
        _props.StartAnimation(name, a);
    }

    /// <summary>Pixels per DIP of the window (DPI × user zoom) and the transparent margin, for ripple paths.</summary>
    public float Scale => _values["Scale"];
    public float Margin => _values["Margin"];

    /// <summary>
    /// Temporarily shapes the blur with a free-form path (edge ripples) in window pixels; null restores the
    /// animated rounded rectangle.
    /// </summary>
    public void SetRipplePath(Windows.Graphics.IGeometrySource2D? source)
    {
        if (_blurVisual is null || _compositor is null)
            return;
        if (source is null)
        {
            _blurVisual.Clip = _rectClip;
            return;
        }
        _pathGeometry ??= _compositor.CreatePathGeometry();
        _pathClip ??= _compositor.CreateGeometricClip(_pathGeometry);
        _pathGeometry.Path = new WinComp.CompositionPath(source);
        _blurVisual.Clip = _pathClip;
    }

    /// <summary>Fades the blurred area in/out (used when the island pops in or hides).</summary>
    public void AnimateOpacity(float to, TimeSpan duration)
    {
        if (_blurVisual is null || _compositor is null)
            return;
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to);
        a.Duration = duration;
        _blurVisual.StartAnimation("Opacity", a);
    }

    private static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || s_dispatcherQueueController != IntPtr.Zero)
            return;

        var options = new Native.DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<Native.DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 0, // DQTAT_COM_NONE
        };
        Native.CreateDispatcherQueueController(options, out s_dispatcherQueueController);
    }

    /// <summary>Release the separate system compositor when the panel is destroyed for hotkey-only mode.</summary>
    public void Release()
    {
        if (_windowTarget is not null)
            _windowTarget.Root = null;
        if (_blurVisual is not null)
        {
            _blurVisual.Brush?.Dispose();
            _blurVisual.Brush = null;
            _blurVisual.Clip = null;
        }
        _blurVisual?.Dispose();
        _windowTarget?.Dispose();
        _rectClip?.Geometry?.Dispose();
        _rectClip?.Dispose();
        _pathClip?.Dispose();
        _pathGeometry?.Dispose();
        _props?.Dispose();
        _clearBrush?.Dispose();
        _compositor?.Dispose();
        _blurVisual = null;
        _windowTarget = null;
        _rectClip = null;
        _pathClip = null;
        _pathGeometry = null;
        _props = null;
        _clearBrush = null;
        _compositor = null;
    }
}
