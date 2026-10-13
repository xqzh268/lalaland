using System.Numerics;
using LaLaLand.Controls;
using LaLaLand.Interop;
using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace LaLaLand.Views;

/// <summary>
/// The floating "dynamic island". Partial class split:
/// this file = window plumbing &amp; placement, .Motion.cs = state machine &amp; animation, .Playback.cs = media binding.
/// </summary>
public sealed partial class IslandWindow : Window
{
    // Layout metrics in DIPs (before the user zoom factor)
    private const double WindowMargin = 18; // transparent slack around the island for overshoot
    private const double CollapsedHeight = 38;
    private const double CardHeight = 212;
    private const double LyricsHeight = 236;
    private const double MinExpandedWidth = 340, MaxExpandedWidth = 600;
    private const double FullHeight = CardHeight + LyricsHeight;
    // The island box is larger than the largest state so the springy overshoot is never cut off.
    private const double OvershootRoom = 24;
    private double ExpandedWidth => Math.Clamp(_settings.Current.ExpandedWidth, MinExpandedWidth, MaxExpandedWidth);
    private double IslandMaxWidth => ExpandedWidth + 2 * OvershootRoom;
    private const double IslandMaxHeight = FullHeight + 2 * OvershootRoom;

    private readonly SettingsService _settings;
    private readonly MediaService _media;
    private readonly LyricsService _lyricsService;
    private readonly AppleMusicAutomation _appleMusic;
    private readonly VolumeService _volume;
    private readonly DispatcherQueue _dispatcher;
    private readonly IslandBackdrop _backdrop = new();

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueueTimer _watchTimer;   // periodic z-order / full-screen check
    private readonly DispatcherQueueTimer _settleTimer;  // one-shot check after the foreground window changed
    private readonly Native.WinEventProc _foregroundProc; // must stay referenced while hooked
    private IntPtr _foregroundHook;
    private bool _d3dFullscreen;           // some app is in exclusive Direct3D full-screen (any display)
    private bool _exclusiveOnOurDisplay;   // ...and it is on the island's display, so the island steps aside

    private float _ax, _ay; // anchor of the island inside its box: 0 / 0.5 / 1
    private double _dpiScale = 1;
    private bool _isShown;
    private bool _allowClose;

    public IslandWindow(SettingsService settings, MediaService media, LyricsService lyrics, AppleMusicAutomation appleMusic, VolumeService volume)
    {
        _settings = settings;
        _media = media;
        _lyricsService = lyrics;
        _appleMusic = appleMusic;
        _volume = volume;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        SystemBackdrop = _backdrop;

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        ConfigureWindow();
        _backdrop.Attach(_hwnd);

        _watchTimer = _dispatcher.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromSeconds(1.5);
        _watchTimer.Tick += (_, _) => CheckZOrder(foregroundChanged: false);
        _settleTimer = _dispatcher.CreateTimer();
        _settleTimer.IsRepeating = false;
        _settleTimer.Interval = TimeSpan.FromMilliseconds(600);
        _settleTimer.Tick += (_, _) => CheckZOrder(foregroundChanged: true);
        // A game becomes foreground first and switches to exclusive mode a moment later. Touching the z-order
        // in between can knock it out of exclusive mode (flash to desktop), so only react once things settle.
        _foregroundProc = (_, _, _, _, _, _, _) =>
        {
            if (_allowClose)
                return;
            _settleTimer.Stop();
            _settleTimer.Start();
        };
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _watchTimer.Start();

        InitMotion();
        InitFit();
        InitGame();
        InitAudio();
        InitVinyl();
        InitFrames();
        InitPlayback();

        Root.PointerEntered += (_, _) => OnPointerEnteredIsland();
        Root.PointerMoved += (_, _) => OnPointerEnteredIsland();
        Root.PointerExited += (_, _) => OnPointerExitedIsland();
        Root.Loaded += (_, _) => UpdatePlacement();
        Root.XamlRootChanged(_ => UpdatePlacement());
        _appWindow.Closing += (_, e) => e.Cancel = !_allowClose;
        _settings.Changed += ApplySettings;
        _settings.LyricPaintChanged += ApplyLyricForeground;

        ApplySettings();

#if DEBUG
        // Test hook: open straight into the full lyrics view (lets UI checks run without mouse input).
        if (Environment.GetEnvironmentVariable("LALALAND_TEST_STATE") == "full")
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => { SetPinned(true); GoTo(IslandState.Full); });
#endif
    }

    // ---------------------------------------------------------------- window plumbing

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;

        long style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
        style |= Native.WS_POPUP;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, (IntPtr)style);

        // Tool window: no taskbar button / Alt-Tab entry. No-activate: clicking never steals focus from a game.
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)ex);

        int corner = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        uint border = Native.DWMWA_COLOR_NONE;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(uint));

        // Enables per-pixel alpha for the window surface (the blur region is an empty off-screen rect, so nothing is blurred).
        var blur = new Native.DWM_BLURBEHIND
        {
            dwFlags = Native.DWM_BB_ENABLE | Native.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = Native.CreateRectRgn(-2, -2, -1, -1),
        };
        Native.DwmEnableBlurBehindWindow(_hwnd, ref blur);
        Native.DeleteObject(blur.hRgnBlur); // DWM copies the region; repeated panel recreation must not leak it.

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    /// <summary>Re-assert topmost so we stay above windows (e.g. games) that grabbed the top of the z-order later.</summary>
    private void AssertTopmost()
    {
        // Never touch the z-order while an exclusive full-screen app runs: it can drop out of exclusive mode.
        if (!_isShown || _d3dFullscreen)
            return;
        _lastAssertTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Re-evaluates the full-screen state, then (if allowed) reclaims the top of the z-order.</summary>
    private void CheckZOrder(bool foregroundChanged = true)
    {
        if (_allowClose)
            return;
        UpdateFullscreenState();
        UpdateGameState();
        if (_settings.Current.TopmostGuard && (foregroundChanged || PeriodicAssertDue()))
            AssertTopmost();
    }

    private void UpdateFullscreenState()
    {
        bool d3d = Native.SHQueryUserNotificationState(out var state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN;
        bool onOurDisplay = false;
        if (d3d && _settings.Current.YieldToExclusiveFullscreen)
        {
            var foreground = Native.GetForegroundWindow();
            onOurDisplay = foreground == IntPtr.Zero ||
                Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST) == Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        }

        _d3dFullscreen = d3d;
        if (onOurDisplay != _exclusiveOnOurDisplay)
        {
            _exclusiveOnOurDisplay = onOurDisplay;
            Log.Info(onOurDisplay ? "Exclusive full-screen app detected: island steps aside" : "Exclusive full-screen ended");
            UpdateVisibility();
        }
    }

    public void Shutdown()
    {
        if (_allowClose)
            return;
        _allowClose = true;
        _isShown = false;
        _appWindow.Hide();
        _settings.Changed -= ApplySettings;
        _settings.LyricPaintChanged -= ApplyLyricForeground;
        _media.TrackChanged -= OnTrackChanged;
        _media.ArtworkChanged -= OnArtworkChanged;
        _media.PlaybackStateChanged -= OnPlaybackStateChanged;
        _media.TimelineChanged -= OnTimelineChanged;
        _lyricsService.Changed -= OnLyricsChanged;
        _lyricsCts?.Cancel();
        _pendingVolume = null;
        _analyzer.Dispose();
        foreach (var timer in new[] { _watchTimer, _settleTimer, _hoverTimer, _collapseTimer, _regionTimer,
                     _hideTimer, _leaveTimer, _gamePeekTimer, _frameTimer, _lyricTimer, _clockTimer, _morphTimer, _hudTimer })
            timer.Stop();
        LyricFlow.SetLive(false);
        StopRipples();
        if (_foregroundHook != IntPtr.Zero)
            Native.UnhookWinEvent(_foregroundHook);
        _foregroundHook = IntPtr.Zero;
        SystemBackdrop = null;
        _backdrop.Release();
        Close();
    }

    // ---------------------------------------------------------------- settings

    private void ApplySettings()
    {
        if (_allowClose)
            return;
        var s = _settings.Current;
        (_ax, _ay) = s.Position switch
        {
            IslandPosition.TopLeft => (0f, 0f),
            IslandPosition.TopRight => (1f, 0f),
            IslandPosition.BottomLeft => (0f, 1f),
            IslandPosition.BottomCenter => (0.5f, 1f),
            IslandPosition.BottomRight => (1f, 1f),
            _ => (0.5f, 0f),
        };

        ApplyLayout();
        ApplyMaterial();
        ApplyCollapsedStyle();
        ApplyLyricSettings();
        UpdatePlacement();
        UpdateVisibility();
        CheckZOrder();
    }

    private void ApplyLayout()
    {
        var hAlign = _ax switch { 0f => HorizontalAlignment.Left, 1f => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        var vAlign = _ay == 0f ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        Host.Width = IslandMaxWidth + 2 * WindowMargin;
        Host.Height = IslandMaxHeight + 2 * WindowMargin;
        Island.Margin = EdgeHost.Margin = new Thickness(WindowMargin);
        Island.Width = EdgeHost.Width = IslandMaxWidth;
        Island.Height = EdgeHost.Height = IslandMaxHeight;
        UserScaleTransform.ScaleX = UserScaleTransform.ScaleY = Math.Clamp(_settings.Current.Scale, 0.7, 1.6);

        // The collapsed layer is laid out at its largest size; its contents ride the animated pill edges (see BindCollapsedGroups).
        CollapsedLayer.Width = CollapsedMaxWidth;
        CollapsedLayer.Height = CollapsedTwoLineHeight;
        ExpandedLayer.Width = ExpandedWidth;
        CollapsedLayer.HorizontalAlignment = ExpandedLayer.HorizontalAlignment = hAlign;
        CollapsedLayer.VerticalAlignment = ExpandedLayer.VerticalAlignment = vAlign;
        ExpandedLayer.Height = FullHeight;

        // When anchored at the bottom the island grows upwards, so the card sits below the lyrics.
        bool top = _ay == 0f;
        ExpandedLayer.RowDefinitions[0].Height = new GridLength(top ? CardHeight : LyricsHeight);
        ExpandedLayer.RowDefinitions[1].Height = new GridLength(top ? LyricsHeight : CardHeight);
        Grid.SetRow(NowPlayingCard, top ? 0 : 1);
        Grid.SetRow(LyricsView, top ? 1 : 0);
        Grid.SetRow(LyricFlow, top ? 1 : 0);

        Anim.Visual(CollapsedLayer).CenterPoint = new Vector3((float)(_ax * CollapsedMaxWidth), (float)(_ay * CollapsedTwoLineHeight), 0);
        Anim.Visual(ExpandedLayer).CenterPoint = new Vector3((float)(_ax * ExpandedWidth), (float)(_ay * FullHeight), 0);

        UpdateMotionAnchor();
    }

    // ---------------------------------------------------------------- placement

    private double TotalScale => _dpiScale * UserScaleTransform.ScaleX;

    /// <summary>Positions the window on the chosen display according to the preset and offsets.</summary>
    public void UpdatePlacement()
    {
        if (_allowClose)
            return;
        var display = PickDisplay();
        if (display is null)
            return;

        _dpiScale = GetDpiScale(display);
        double scale = TotalScale;
        _backdrop.Set("Scale", (float)scale);
        var work = display.WorkArea;
        int width = (int)Math.Ceiling(Host.Width * scale);
        int height = (int)Math.Ceiling(Host.Height * scale);
        double margin = WindowMargin * scale;
        double offX = _settings.Current.OffsetX * _dpiScale;
        double offY = _settings.Current.OffsetY * _dpiScale;

        double x = _ax switch
        {
            0f => work.X + Math.Abs(offX) - margin,
            1f => work.X + work.Width - Math.Abs(offX) + margin - width,
            _ => work.X + (work.Width - width) / 2.0 + offX,
        };
        double y = _ay == 0f
            ? work.Y + offY - margin
            : work.Y + work.Height - offY + margin - height;

        _appWindow.MoveAndResize(new RectInt32((int)Math.Round(x), (int)Math.Round(y), width, height));
        UpdateRegion(_state);
    }

    private DisplayArea? PickDisplay()
    {
        try
        {
            var all = DisplayArea.FindAll();
            int index = _settings.Current.DisplayIndex;
            if (index >= 0 && index < all.Count)
                return all[index];
            return DisplayArea.Primary;
        }
        catch (Exception ex)
        {
            Log.Error("Enumerating displays failed", ex);
            return DisplayArea.Primary;
        }
    }

    private static double GetDpiScale(DisplayArea display)
    {
        var monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        return Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>
    /// Restricts hit-testing (and painting) to the island's bounds for the given states,
    /// so the transparent part of the window never swallows clicks.
    /// </summary>
    private void UpdateRegion(params IslandState[] states) => UpdateRegion(0, states);

    /// <param name="extraSlack">Additional margin while an animation (and its overshoot) is running.</param>
    private void UpdateRegion(double extraSlack, params IslandState[] states)
    {
        double w = 0, h = 0;
        foreach (var state in states)
        {
            // Collapsed: always the largest pill the current settings allow. The pill shrink-wraps every lyric line,
            // and rewriting the window region per line makes DWM recompose the whole window (visible flicker).
            var (sw, sh) = state == IslandState.Collapsed ? (CollapsedMaxWidth, CollapsedMaxHeight) : (Metrics(state).Width, Metrics(state).Height);
            w = Math.Max(w, sw);
            h = Math.Max(h, sh);
        }

        UpdateRegionForSize(w, h, extraSlack);
    }

    private void UpdateRegionForSize(double w, double h, double extraSlack)
    {
        var r = IslandRectInWindow(w, h, slack: 6 + extraSlack); // room for the overshoot and the hairline edge
        var region = Native.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
        Native.SetWindowRgn(_hwnd, region, true); // the system owns the region afterwards
    }

    /// <summary>The island's bounds for a given size, in physical pixels relative to the window.</summary>
    private Native.RECT IslandRectInWindow(double width, double height, double slack)
    {
        double scale = TotalScale;
        double left = WindowMargin + _ax * (IslandMaxWidth - width) - slack;
        double top = WindowMargin + _ay * (IslandMaxHeight - height) - slack;
        return new Native.RECT
        {
            Left = (int)Math.Floor(left * scale),
            Top = (int)Math.Floor(top * scale),
            Right = (int)Math.Ceiling((left + width + 2 * slack) * scale),
            Bottom = (int)Math.Ceiling((top + height + 2 * slack) * scale),
        };
    }

    private bool IsCursorOverIsland()
    {
        if (!Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(_hwnd, out var window))
            return false;
        var (w, h, _) = Metrics(_state);
        var r = IslandRectInWindow(w, h, slack: 2);
        int x = cursor.X - window.Left, y = cursor.Y - window.Top;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // ---------------------------------------------------------------- visibility

    private bool ShouldShow =>
        _settings.Current.IsIslandVisible && !_exclusiveOnOurDisplay && !HiddenByGame &&
        (_media.Track is not null || !_settings.Current.HideWhenIdle);

    private void UpdateVisibility()
    {
        if (_allowClose)
            return;
        bool show = ShouldShow;
        if (show == _isShown)
            return;
        _isShown = show;

        var hostVisual = Anim.Visual(Host);
        hostVisual.CenterPoint = new Vector3(
            (float)(WindowMargin + _ax * IslandMaxWidth),
            (float)(WindowMargin + _ay * IslandMaxHeight), 0);

        if (show)
        {
            _hideTimer.Stop();
            hostVisual.Opacity = 0;
            _appWindow.Show(false);
            AssertTopmost();
            Anim.Fade(Host, 1, 260);
            Anim.Scale(Host, Vector3.One, 520, easing: _expandEasing, from: new Vector3(0.6f, 0.6f, 1));
            _backdrop.Set("Pop", 0.6f);
            _backdrop.Animate("Pop", 1f, TimeSpan.FromMilliseconds(520), ExpandCp1, ExpandCp2);
            _backdrop.AnimateOpacity(1f, TimeSpan.FromMilliseconds(260));
        }
        else if (_exclusiveOnOurDisplay)
        {
            // Step aside instantly: no animation frames, no z-order changes over the game.
            GoTo(IslandState.Collapsed);
            _hideTimer.Stop();
            _appWindow.Hide();
        }
        else
        {
            GoTo(IslandState.Collapsed);
            Anim.Fade(Host, 0, 200);
            Anim.Scale(Host, new Vector3(0.7f, 0.7f, 1), 240, easing: Anim.EaseInOut(_compositor));
            _backdrop.Animate("Pop", 0.7f, TimeSpan.FromMilliseconds(240), new Vector2(0.45f, 0f), new Vector2(0.2f, 1f));
            _backdrop.AnimateOpacity(0f, TimeSpan.FromMilliseconds(200));
            _hideTimer.Start();
        }

        UpdateVisualizer();
    }

    public void ToggleVisibility() => _settings.Update(s => s.IsIslandVisible = !s.IsIslandVisible);
}

internal static class FrameworkElementExtensions
{
    /// <summary>Invokes <paramref name="handler"/> whenever the element's XamlRoot changes size or DPI.</summary>
    public static void XamlRootChanged(this FrameworkElement element, Action<XamlRoot> handler)
    {
        double lastScale = 0;
        element.Loaded += (_, _) =>
        {
            if (element.XamlRoot is not { } root)
                return;
            lastScale = root.RasterizationScale;
            root.Changed += (r, _) =>
            {
                if (Math.Abs(r.RasterizationScale - lastScale) > 0.001)
                {
                    lastScale = r.RasterizationScale;
                    handler(r);
                }
            };
        };
    }
}
