using System.Numerics;
using LaLaLand.Controls;
using LaLaLand.Models;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Shapes;

namespace LaLaLand.Views;

public enum IslandState
{
    Collapsed,
    /// <summary>Hover card: artwork, title, transport controls.</summary>
    Expanded,
    /// <summary>Hover card plus lyrics.</summary>
    Full,
}

public sealed partial class IslandWindow
{
    private const double ExpandDurationMs = 560;
    private const double CollapseDurationMs = 460;
    private const double PeekDurationMs = 3200;

    // Shared by the WinUI clip and the system-compositor blur mask so both follow identical curves.
    private static readonly Vector2 ExpandCp1 = new(0.3f, 1.32f), ExpandCp2 = new(0.52f, 1f);
    private static readonly Vector2 CollapseCp1 = new(0.36f, 1.12f), CollapseCp2 = new(0.5f, 1f);

    private Compositor _compositor = null!;
    private CompositionPropertySet _shape = null!;
    private CompositionSpriteShape _edgeShape = null!;
    private CompositionSpriteShape _sheenShape = null!;
    private ShapeVisual _edgeVisual = null!;
    private ShapeVisual _sheenVisual = null!;
    private CompositionEasingFunction _expandEasing = null!;
    private CompositionEasingFunction _collapseEasing = null!;

    private DispatcherQueueTimer _hoverTimer = null!;
    private DispatcherQueueTimer _collapseTimer = null!;
    private DispatcherQueueTimer _regionTimer = null!;
    private DispatcherQueueTimer _hideTimer = null!;
    private DispatcherQueueTimer _leaveTimer = null!;

    private IslandState _state = IslandState.Collapsed;
    private bool _pointerInside;
    private bool _lyricsPinned;

    public bool IsLyricsPinned => _lyricsPinned;

    private void InitMotion()
    {
        var islandVisual = Anim.Visual(Island);
        _compositor = islandVisual.Compositor;

        // Slight overshoot on the way out, softer settle on the way back: the island "breathes" like on iPhone.
        _expandEasing = _compositor.CreateCubicBezierEasingFunction(ExpandCp1, ExpandCp2);
        _collapseEasing = _compositor.CreateCubicBezierEasingFunction(CollapseCp1, CollapseCp2);

        // One property set drives the clip, sheen and edge; animating W/H/R moves everything in lock-step.
        // The system-side blur mask (IslandBackdrop) mirrors the same values.
        var (w, h, r) = Metrics(IslandState.Collapsed);
        _shape = _compositor.CreatePropertySet();
        _shape.InsertScalar("W", (float)w);
        _shape.InsertScalar("H", (float)h);
        _shape.InsertScalar("R", (float)r);
        NoteShape("W", w, 0);
        NoteShape("H", h, 0);
        NoteShape("R", r, 0);
        _shape.InsertScalar("AX", 0.5f);
        _shape.InsertScalar("AY", 0f);
        _shape.InsertScalar("MaxW", (float)IslandMaxWidth);
        _shape.InsertScalar("MaxH", (float)IslandMaxHeight);
        _shape.InsertScalar("CW", (float)CollapsedMaxWidth);
        _shape.InsertScalar("CH", (float)CollapsedTwoLineHeight);
        // Audio-driven values: V0..V3 equaliser bands.
        foreach (var name in new[] { "V0", "V1", "V2", "V3" })
            _shape.InsertScalar(name, 0f);

        islandVisual.Clip = _rectClip = _compositor.CreateGeometricClip(CreateIslandGeometry(inset: 0f));

        // Glass edge (above content) and sheen (below content). Their gradients map to the shape's current
        // bounds, so highlights stay correct at every size during the animation.
        _edgeShape = _compositor.CreateSpriteShape(_edgeRectGeometry = CreateIslandGeometry(inset: 0.5f));
        _sheenShape = _compositor.CreateSpriteShape(_sheenRectGeometry = CreateIslandGeometry(inset: 0f));
        ElementCompositionPreview.SetElementChildVisual(EdgeHost, _edgeVisual = CreateShapeVisual(_edgeShape));
        ElementCompositionPreview.SetElementChildVisual(SheenHost, _sheenVisual = CreateShapeVisual(_sheenShape));

        // Initial layer state
        Anim.Visual(ExpandedLayer).Opacity = 0;
        Anim.Visual(ExpandedLayer).Scale = new Vector3(0.94f, 0.94f, 1);
        Anim.Visual(LyricsView).Opacity = 0;
        Anim.Visual(LyricFlow).Opacity = 0;
        ExpandedLayer.IsHitTestVisible = false;
        LyricsView.IsHitTestVisible = false;
        LyricFlow.IsHitTestVisible = false;

        _hoverTimer = CreateTimer(() => GoTo(IslandState.Expanded));
        _collapseTimer = CreateTimer(() =>
        {
            if (!_pointerInside && !_lyricsPinned)
                GoTo(IslandState.Collapsed);
        });
        _regionTimer = CreateTimer(() => UpdateRegion(_state));
        _hideTimer = CreateTimer(() =>
        {
            if (!_isShown)
                _appWindow.Hide();
        });
        _hideTimer.Interval = TimeSpan.FromMilliseconds(260);

        // Pointer-exit events can be lost (cursor flung off-screen, warped by another app); while the pointer is
        // considered inside, double-check its position at a low rate. The timer only runs during interaction.
        _leaveTimer = _dispatcher.CreateTimer();
        _leaveTimer.Interval = TimeSpan.FromMilliseconds(120);
        _leaveTimer.IsRepeating = true;
        _leaveTimer.Tick += (_, _) =>
        {
            if (_allowClose)
                return;
            if (!IsCursorOverIsland())
                OnPointerExitedIsland();
        };
    }

    private CompositionRoundedRectangleGeometry CreateIslandGeometry(float inset)
    {
        const string left = "p.AX * (p.MaxW - p.W)";
        const string top = "p.AY * (p.MaxH - p.H)";
        const string width = "p.W";
        const string height = "p.H";
        const string radius = "Min(p.R, p.H * 0.5)";
        string i = inset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string i2 = (2 * inset).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bind(geometry, "Offset", $"Vector2({left} + {i}, {top} + {i})");
        Bind(geometry, "Size", $"Vector2({width} - {i2}, {height} - {i2})");
        Bind(geometry, "CornerRadius", $"Vector2(Max({radius} - {i}, 0), Max({radius} - {i}, 0))");
        return geometry;
    }

    private ShapeVisual CreateShapeVisual(CompositionSpriteShape shape)
    {
        var visual = _compositor.CreateShapeVisual();
        visual.Size = new Vector2((float)IslandMaxWidth, (float)IslandMaxHeight);
        visual.Shapes.Add(shape);
        return visual;
    }

    /// <summary>
    /// The collapsed layer is laid out at its maximum size (CW × CH). Its pieces are pinned to the animated pill
    /// edges with expressions — cover and text to the leading edge, equaliser / buttons to the trailing edge,
    /// centred text to the centre — so they ride the width/height morph frame-by-frame on the compositor.
    /// </summary>
    private void BindCollapsedGroups()
    {
        const string dx = "(p.CW - Min(p.W, p.CW))";
        const string dy = "(p.CH - Min(p.H, p.CH)) * (p.AY - 0.5)";
        string leading = $"Vector3(p.AX * {dx}, {dy}, 0)";
        string trailing = $"Vector3((p.AX - 1) * {dx}, {dy}, 0)";
        string centre = $"Vector3((p.AX - 0.5) * {dx}, {dy}, 0)";
        string text = _settings.Current.CollapsedStyle == CollapsedStyle.LyricOnly ? centre : leading;

        BindTranslation(MiniCoverBorder, leading);
        BindTranslation(TickerHost, text);
        BindTranslation(VolumeHud, text);
        BindTranslation(MiniVisualizer, trailing);
        BindTranslation(MiniControls, trailing);
    }

    private void BindTranslation(Microsoft.UI.Xaml.UIElement element, string expression)
    {
        Anim.EnableTranslation(element);
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _shape);
        Anim.Visual(element).StartAnimation("Translation", animation);
    }

    private DispatcherQueueTimer CreateTimer(Action tick)
    {
        var timer = _dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (!_allowClose) tick(); };
        return timer;
    }

    private void Bind(CompositionObject target, string property, string expression)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _shape);
        target.StartAnimation(property, animation);
    }

    private void UpdateMotionAnchor()
    {
        if (_shape is null)
            return;
        _shape.InsertScalar("AX", _ax);
        _shape.InsertScalar("AY", _ay);
        _shape.InsertScalar("CW", (float)CollapsedMaxWidth);
        _shape.InsertScalar("CH", (float)CollapsedTwoLineHeight);
        _shape.InsertScalar("MaxW", (float)IslandMaxWidth);
        _shape.InsertScalar("MaxH", (float)IslandMaxHeight);
        _edgeVisual.Size = _sheenVisual.Size = new Vector2((float)IslandMaxWidth, (float)IslandMaxHeight);
        _backdrop.Set("AX", _ax);
        _backdrop.Set("AY", _ay);
        _backdrop.Set("MaxW", (float)IslandMaxWidth);
        _backdrop.Set("MaxH", (float)IslandMaxHeight);
        _backdrop.Set("Margin", (float)WindowMargin);

        // Width / radius may have changed with the style or the corner setting.
        var (w, h, r) = Metrics(_state);
        foreach (var (name, value) in new[] { ("W", w), ("H", h), ("R", r) })
        {
            _shape.InsertScalar(name, (float)value);
            _backdrop.Set(name, (float)value);
            NoteShape(name, value, 0);
        }
        ApplyConcentricCorners();
    }

    /// <summary>Artwork corners stay concentric with the island: inner radius = outer radius − inset.</summary>
    private void ApplyConcentricCorners()
    {
        double miniInset = (_collapsedH - MiniCoverBorder.Height) / 2;
        MiniCoverBorder.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(Math.Max(Metrics(IslandState.Collapsed).Radius - miniInset, 3));
        CoverBorder.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(Math.Max(Metrics(IslandState.Expanded).Radius - NowPlayingCard.Padding.Top, 4));
    }

    private double CornerRadiusSetting => Math.Clamp(_settings.Current.CornerRadius, 4, 48);

    private (double Width, double Height, double Radius) Metrics(IslandState state) => state switch
    {
        IslandState.Expanded => (ExpandedWidth, CardHeight, CornerRadiusSetting),
        IslandState.Full => (ExpandedWidth, CardHeight + LyricsHeight, CornerRadiusSetting),
        _ => (_collapsedW, _collapsedH, Math.Min(CornerRadiusSetting, _collapsedH / 2)),
    };

    // ---------------------------------------------------------------- state machine

    private void GoTo(IslandState target)
    {
        if (target == IslandState.Full && !_settings.Current.LyricsEnabled)
            target = IslandState.Expanded;
        if (target == _state)
            return;

        var from = _state;
        _state = target;
        var (w, h, r) = Metrics(target);
        bool growing = h >= Metrics(from).Height;
        double duration = growing ? ExpandDurationMs : CollapseDurationMs;

        // Grow the hit region immediately, shrink it once the animation is done.
        UpdateRegion(OvershootRoom, from, target);
        _regionTimer.Interval = TimeSpan.FromMilliseconds(duration + 40);
        _regionTimer.Start();

        AnimateShape("W", w, duration, growing);
        AnimateShape("H", h, duration, growing);
        AnimateShape("R", r, duration, growing);

        bool wasCollapsed = from == IslandState.Collapsed;
        bool isCollapsed = target == IslandState.Collapsed;
        if (wasCollapsed && !isCollapsed)
        {
            Anim.Fade(CollapsedLayer, 0, 140);
            Anim.Scale(CollapsedLayer, new Vector3(0.9f, 0.9f, 1), 220);
            Anim.Fade(ExpandedLayer, 1, 320, delayMs: 70);
            Anim.Scale(ExpandedLayer, Vector3.One, 520, delayMs: 30, easing: _expandEasing, from: new Vector3(0.9f, 0.9f, 1));
        }
        else if (!wasCollapsed && isCollapsed)
        {
            Anim.Fade(ExpandedLayer, 0, 150);
            Anim.Scale(ExpandedLayer, new Vector3(0.94f, 0.94f, 1), 240);
            Anim.Fade(CollapsedLayer, 1, 300, delayMs: 150);
            Anim.Scale(CollapsedLayer, Vector3.One, 420, delayMs: 120, from: new Vector3(0.88f, 0.88f, 1));
        }

        bool full = target == IslandState.Full;
        Anim.Fade(LyricsView, full ? 1 : 0, full ? 380 : 160, delayMs: full ? 140 : 0);
        Anim.Fade(LyricFlow, full ? 1 : 0, full ? 380 : 160, delayMs: full ? 140 : 0);

        CollapsedLayer.IsHitTestVisible = isCollapsed;
        ExpandedLayer.IsHitTestVisible = !isCollapsed;
        LyricsView.IsHitTestVisible = full;
        LyricFlow.IsHitTestVisible = full;

        OnStateChanged(from);
    }

    private void AnimateShape(string property, double value, double durationMs, bool growing)
    {
        NoteShape(property, value, durationMs);
        _backdrop.Animate(property, (float)value, TimeSpan.FromMilliseconds(durationMs),
            growing ? ExpandCp1 : CollapseCp1, growing ? ExpandCp2 : CollapseCp2);

        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, (float)value, growing ? _expandEasing : _collapseEasing);
        a.Duration = TimeSpan.FromMilliseconds(durationMs);
        _shape.StartAnimation(property, a);
    }

    // ---------------------------------------------------------------- pointer & commands

    private void OnPointerEnteredIsland()
    {
        if (_pointerInside)
            return;
        // The collapsed hit region is sized for the largest pill; only react once the cursor is on the real one.
        // (PointerMoved calls back in here, so entering the pill from the margin still works.)
        if (_state == IslandState.Collapsed && !IsCursorOverIsland())
            return;
        _pointerInside = true;
        _leaveTimer.Start();
        _collapseTimer.Stop();
        if (_state == IslandState.Collapsed)
        {
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.Current.HoverExpandDelayMs));
            _hoverTimer.Start();
        }
    }

    private void OnPointerExitedIsland()
    {
        _leaveTimer.Stop();
        if (!_pointerInside)
            return;
        _pointerInside = false;
        _hoverTimer.Stop();
        if (_state != IslandState.Collapsed && !_lyricsPinned)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.Current.CollapseDelayMs));
            _collapseTimer.Start();
        }
    }

    /// <summary>Briefly expands the island (track change, hotkey feedback) unless the user is interacting with it.</summary>
    public void Peek()
    {
        if (!_isShown || _pointerInside || _lyricsPinned || _state != IslandState.Collapsed)
            return;
        GoTo(IslandState.Expanded);
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(PeekDurationMs);
        _collapseTimer.Start();
    }

    /// <summary>
    /// Pin: the expanded / full panel stays open when the pointer leaves or the user clicks elsewhere. Shared by the
    /// pin button on the card and the "show lyrics" hotkey.
    /// </summary>
    private void SetPinned(bool pinned)
    {
        _lyricsPinned = pinned;
        PinGlyph.Glyph = char.ConvertFromUtf32(pinned ? 0xE840 : 0xE718);
        PinGlyph.Foreground = pinned ? _accentBrush : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(PinButton, pinned ? "取消置顶" : "置顶面板：移开鼠标也不收起");
        if (pinned)
            _collapseTimer.Stop();
        else if (!_pointerInside && _state != IslandState.Collapsed)
        {
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, _settings.Current.CollapseDelayMs));
            _collapseTimer.Start();
        }
    }

    private void OnPinClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => SetPinned(!_lyricsPinned);

    /// <summary>Pins the island fully open with lyrics (hotkey), or releases the pin.</summary>
    public void ToggleLyricsPinned()
    {
        SetPinned(!_lyricsPinned);
        if (_lyricsPinned)
        {
            if (!_isShown)
                _settings.Update(s => s.IsIslandVisible = true);
            GoTo(IslandState.Full);
        }
        else
        {
            GoTo(_pointerInside ? IslandState.Expanded : IslandState.Collapsed);
        }
    }

    private void ToggleFullFromCard()
    {
        if (_state == IslandState.Full)
        {
            GoTo(IslandState.Expanded); // a pinned panel stays pinned, just smaller
        }
        else
        {
            GoTo(IslandState.Full);
        }
    }

    // ---------------------------------------------------------------- equaliser

    private void UpdateVisualizer()
    {
        bool visible = MiniVisualizer.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
        // Moving bars force a recomposition every refresh: off in game mode.
        bool animate = visible && _isShown && _media.IsPlaying && _state == IslandState.Collapsed && !LightweightMode;

        for (int i = 0; i < MiniVisualizer.Children.Count; i++)
        {
            var bar = (Rectangle)MiniVisualizer.Children[i];
            var v = Anim.Visual(bar);
            v.CenterPoint = new Vector3(1.5f, 8f, 0);
            if (animate && _audioLive)
            {
                // Real spectrum: each bar follows one band, updated ~60×/s by the analyzer.
                var e = _compositor.CreateExpressionAnimation($"Max(0.22, Min(1.0, 0.18 + p.V{i} * 0.9))");
                e.SetReferenceParameter("p", _shape);
                v.StartAnimation("Scale.Y", e);
            }
            else if (animate)
            {
                // Fallback while audio capture is unavailable: irregular per-bar loops.
                var a = _compositor.CreateScalarKeyFrameAnimation();
                float[] pattern = i switch
                {
                    0 => [0.35f, 0.9f, 0.5f, 1f, 0.4f],
                    1 => [0.8f, 0.3f, 1f, 0.55f, 0.75f],
                    2 => [0.5f, 1f, 0.35f, 0.7f, 0.9f],
                    _ => [0.9f, 0.45f, 0.75f, 0.3f, 0.65f],
                };
                for (int k = 0; k < pattern.Length; k++)
                    a.InsertKeyFrame(k / (float)pattern.Length, pattern[k], Anim.EaseInOut(_compositor));
                a.InsertKeyFrame(1f, pattern[0], Anim.EaseInOut(_compositor));
                a.Duration = TimeSpan.FromMilliseconds(1300 + i * 170);
                a.IterationBehavior = AnimationIterationBehavior.Forever;
                v.StartAnimation("Scale.Y", a);
            }
            else
            {
                var a = _compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(1f, 0.3f, Anim.EaseOut(_compositor));
                a.Duration = TimeSpan.FromMilliseconds(300);
                v.StartAnimation("Scale.Y", a);
            }
        }
        UpdateAudioActivity();
    }
}
