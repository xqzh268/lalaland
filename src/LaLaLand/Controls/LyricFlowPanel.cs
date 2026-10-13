using LaLaLand.Models;
using System.Diagnostics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Controls;

/// <summary>
/// Word-synced lyrics that flow around a circular obstacle (the vinyl disc), Pretext-style.
/// <para>
/// Every line is tokenized and measured once when lyrics load ("prepare"). Layout is plain arithmetic: each row
/// gets the slot to the right of the circle's chord at that height, so the column of text hugs the arc. Tokens are
/// positioned through the Translation facade (no XAML layout) and only re-submitted when they actually move.
/// A finite transition blends token reflow and scroll together on a 60 Hz timer only while travelling.
/// The prepared layout stays fixed throughout the transition, and the timer stops after settling.
/// </para>
/// </summary>
public sealed class LyricFlowPanel : Grid
{
    private const double SidePadding = 22;
    private const double LineGap = 14;
    private const double TranslationGap = 4;
    private const double AnchorRatio = 0.38;
    private const double ObstacleMargin = 12;
    private const int MaterializeRadius = 7;   // lines around the active one that get real visuals
    private const double EdgeBand = 48;        // lines fade (and blur) in/out over this distance from the edges

    private sealed class LineView
    {
        public required LyricLine Line;
        public required List<Token> Tokens;
        public List<Token>? TranslationTokens;
        public SweepText? Main;
        public SweepText? Translation;
        public double Top;          // document y, recomputed by every layout
        public double Height;
        public bool Visible;
        public double[] X = [];
        public int[] Row = [];
        public double[] TX = [];
        public int[] TRow = [];
        public double[] ReflowX = [], ReflowY = [], ReflowTX = [], ReflowTY = [];
        public double DisplayTop, DisplayHeight, ReflowTop, ReflowHeight;
    }

    private sealed record EdgeLayer(SpriteVisual Sprite, CompositionVisualSurface Surface, CompositionLinearGradientBrush Mask);

    private readonly Canvas _canvas = new();
    private readonly Grid _source = new();
    private readonly TextShaper _mainShaper = new(null, 18, FontWeights.Bold);
    private readonly TextShaper _translationShaper = new(null, 13.5, FontWeights.Normal);
    private Brush _foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
    private readonly List<LineView> _lines = [];
    private readonly DispatcherQueueTimer _scrollTimer;
    private readonly Canvas _edgeHost = new() { IsHitTestVisible = false };
    private EdgeLayer? _sharp, _blurTop, _blurBottom;

    private Lyrics _lyrics = Lyrics.Empty;
    private int _active = -1;
    private double _scroll;
    private double _scrollTarget;
    private double _scrollFrom, _scrollTo;
    private readonly LyricLayoutTransition _transition = new();
    private bool _motionDirty = true;
    private long _lastScrollFrame;
    private double _documentHeight;
    private bool _layoutDirty = true;
    private bool _scrollInitialized;
    private bool _live;
    private bool _sweepEnabled = true;
    private bool _plain;              // lyrics without timestamps: no current line, scroll follows playback progress
    private double _plainFocus;       // document y the plain view centres on

    // Circular obstacle in panel coordinates
    private bool _obstacle;
    private double _obstacleX, _obstacleY, _obstacleRadius;

    public event Action<LyricLine>? LineTapped;

    public LyricFlowPanel()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // hit-testable for taps
        _source.Children.Add(_canvas);
        Children.Add(_source);
        Children.Add(_edgeHost);
        CreateEdgeBlur();

        _scrollTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _scrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _scrollTimer.IsRepeating = true;
        _scrollTimer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastScrollFrame) / (double)Stopwatch.Frequency;
            _lastScrollFrame = now;
            if (!Relayout(snap: false, dt: dt))
                _scrollTimer.Stop(); // arrived: go idle
        };

        SizeChanged += (_, _) =>
        {
            _layoutDirty = true;
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight) };
            UpdateEdgeBands();
            if (Relayout(snap: !_live || !_scrollInitialized)) StartScroll();
        };
        PointerReleased += OnPointerReleased;
    }

    /// <summary>
    /// Edge softening, independent of the window material. The lines live in a hidden source canvas; what the user
    /// sees are composition sprites that draw that canvas through a visual surface: the whole view sharp, faded out
    /// towards both edges, plus a Gaussian-blurred copy of just the edge bands that fades in as the sharp one leaves.
    /// Only the lyrics are ever sampled (never the backdrop), so acrylic, frosted and liquid glass all look alike
    /// and a line enters as a soft blur that sharpens, not as a ghost that pops.
    /// </summary>
    private void CreateEdgeBlur()
    {
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            var source = ElementCompositionPreview.GetElementVisual(_source);
            source.Opacity = 0; // shown only through the sprites below

            var factory = compositor.CreateEffectFactory(new GaussianBlurEffect
            {
                BlurAmount = 5f,
                BorderMode = EffectBorderMode.Soft,
                Source = new CompositionEffectSourceParameter("src"),
            });

            EdgeLayer Layer(bool blurred)
            {
                var surface = compositor.CreateVisualSurface();
                surface.SourceVisual = source;
                var surfaceBrush = compositor.CreateSurfaceBrush(surface);
                surfaceBrush.Stretch = CompositionStretch.None;
                surfaceBrush.HorizontalAlignmentRatio = 0;
                surfaceBrush.VerticalAlignmentRatio = 0;
                CompositionBrush content = surfaceBrush;
                if (blurred)
                {
                    var effect = factory.CreateBrush();
                    effect.SetSourceParameter("src", surfaceBrush);
                    content = effect;
                }
                var mask = compositor.CreateLinearGradientBrush();
                mask.StartPoint = new System.Numerics.Vector2(0, 0);
                mask.EndPoint = new System.Numerics.Vector2(0, 1);
                var masked = compositor.CreateMaskBrush();
                masked.Source = content;
                masked.Mask = mask;
                var sprite = compositor.CreateSpriteVisual();
                sprite.Brush = masked;
                return new EdgeLayer(sprite, surface, mask);
            }

            _sharp = Layer(blurred: false);
            _blurTop = Layer(blurred: true);
            _blurBottom = Layer(blurred: true);
            var container = compositor.CreateContainerVisual();
            container.Children.InsertAtTop(_sharp.Sprite);
            container.Children.InsertAtTop(_blurTop.Sprite);
            container.Children.InsertAtTop(_blurBottom.Sprite);
            ElementCompositionPreview.SetElementChildVisual(_edgeHost, container);
        }
        catch (Exception ex)
        {
            Services.Log.Error("Lyric edge blur unavailable", ex);
            _sharp = _blurTop = _blurBottom = null;
            ElementCompositionPreview.GetElementVisual(_source).Opacity = 1; // plain, unfaded lines
        }
    }

    private void UpdateEdgeBands()
    {
        if (_sharp is not { } sharp || _blurTop is not { } top || _blurBottom is not { } bottom ||
            ActualWidth <= 0 || ActualHeight <= 0)
            return;
        float w = (float)ActualWidth, h = (float)ActualHeight;
        float band = (float)Math.Min(EdgeBand, h / 2);

        sharp.Surface.SourceSize = new System.Numerics.Vector2(w, h);
        sharp.Sprite.Size = new System.Numerics.Vector2(w, h);
        SetStops(sharp, (0f, 0f), (band / h, 1f), (1f - band / h, 1f), (1f, 0f));

        // Blurred copy of each band: invisible at the very edge, strongest where the sharp line is half gone.
        top.Surface.SourceOffset = new System.Numerics.Vector2(0, 0);
        top.Surface.SourceSize = new System.Numerics.Vector2(w, band);
        top.Sprite.Offset = new System.Numerics.Vector3(0, 0, 0);
        top.Sprite.Size = new System.Numerics.Vector2(w, band);
        SetStops(top, (0f, 0f), (0.4f, 1f), (1f, 0f));

        bottom.Surface.SourceOffset = new System.Numerics.Vector2(0, h - band);
        bottom.Surface.SourceSize = new System.Numerics.Vector2(w, band);
        bottom.Sprite.Offset = new System.Numerics.Vector3(0, h - band, 0);
        bottom.Sprite.Size = new System.Numerics.Vector2(w, band);
        SetStops(bottom, (0f, 0f), (0.6f, 1f), (1f, 0f));

        static void SetStops(EdgeLayer layer, params (float Offset, float Alpha)[] stops)
        {
            layer.Mask.ColorStops.Clear();
            foreach (var (offset, alpha) in stops)
                layer.Mask.ColorStops.Add(layer.Mask.Compositor.CreateColorGradientStop(
                    offset, Windows.UI.Color.FromArgb((byte)(alpha * 255), 255, 255, 255)));
        }
    }

    // ---------------------------------------------------------------- configuration

    public void SetFont(FontFamily? family) => SetFonts(family, family);

    public void SetFonts(FontFamily? chinese, FontFamily? english)
    {
        _mainShaper.Configure(chinese, english, 18, FontWeights.Bold);
        _translationShaper.Configure(chinese, english, 13.5, FontWeights.Normal);
        if (!_lyrics.IsEmpty)
        {
            int active = _active;
            SetLyrics(_lyrics, _lines.Any(l => l.TranslationTokens is not null));
            if (active >= 0 && active < _lines.Count) SetActive(active, _lines[active].Line.Time, false);
        }
    }

    public void SetForeground(Brush foreground)
    {
        _foreground = foreground;
        foreach (var line in _lines)
        {
            line.Main?.SetForeground(foreground);
            line.Translation?.SetForeground(foreground);
        }
    }

    public void SetWordSweep(bool enabled) => _sweepEnabled = enabled;

    /// <summary>
    /// Circle the text flows around (panel coordinates). Cheap to call at the beat rate: it only re-lays out when
    /// the circle actually changed.
    /// </summary>
    public void SetObstacle(bool enabled, double centerX, double centerY, double radius)
    {
        if (enabled == _obstacle && Math.Abs(centerX - _obstacleX) < 0.25 && Math.Abs(centerY - _obstacleY) < 0.25 &&
            Math.Abs(radius - _obstacleRadius) < 0.25)
            return;
        _obstacle = enabled;
        (_obstacleX, _obstacleY, _obstacleRadius) = (centerX, centerY, radius);
        _layoutDirty = true;
        if (_live && Relayout(snap: false))
            StartScroll();
    }

    /// <summary>The panel is on screen (full state).</summary>
    public void SetLive(bool live)
    {
        _live = live;
        if (live)
            Relayout(snap: true);
        else
        {
            _scrollTimer.Stop();
            Relayout(snap: true);
        }
    }

    // ---------------------------------------------------------------- content

    public void SetLyrics(Lyrics lyrics, bool showTranslation)
    {
        foreach (var l in _lines)
            Release(l);
        _lines.Clear();
        _lyrics = lyrics;
        _active = -1;
        _scrollInitialized = false;
        _layoutDirty = true;
        _motionDirty = true;
        _transition.Snap();
        _scrollTimer.Stop();
        _plain = !lyrics.IsSynced;
        _plainFocus = 0;

        // Prepare: tokenize and measure every line once.
        foreach (var line in lyrics.Lines)
        {
            var view = new LineView { Line = line, Tokens = _mainShaper.Tokenize(line.DisplayText) };
            if (showTranslation && !string.IsNullOrWhiteSpace(line.Translation))
                view.TranslationTokens = _translationShaper.Tokenize(line.Translation!);
            view.X = new double[view.Tokens.Count];
            view.Row = new int[view.Tokens.Count];
            view.TX = new double[view.TranslationTokens?.Count ?? 0];
            view.TRow = new int[view.TX.Length];
            view.ReflowX = new double[view.X.Length];
            view.ReflowY = new double[view.X.Length];
            view.ReflowTX = new double[view.TX.Length];
            view.ReflowTY = new double[view.TX.Length];
            _lines.Add(view);
        }
        for (int i = 0; i < _lines.Count && i <= MaterializeRadius; i++)
            Materialize(_lines[i]);
        Relayout(snap: true);
    }

    public void Clear() => SetLyrics(Lyrics.Empty, false);

    public void SetActive(int index, TimeSpan now, bool playing)
    {
        if (index == _active)
            return;
        int previous = _active;
        _active = index;
        _layoutDirty = true;

        // Visuals live only around the current line.
        for (int i = 0; i < _lines.Count; i++)
        {
            bool near = index >= 0 ? Math.Abs(i - index) <= MaterializeRadius : i <= MaterializeRadius;
            if (near) Materialize(_lines[i]);
            else Release(_lines[i]);
        }

        for (int i = Math.Max(0, Math.Min(previous, index) - MaterializeRadius); i < _lines.Count && i <= Math.Max(previous, index) + MaterializeRadius; i++)
            ApplyEmphasis(i, animate: true);

        SyncSweep(now, playing);
        if (_live)
            StartScroll();
        else
            Relayout(snap: true);
    }

    /// <summary>Advances the current line's sweep (called by the island's frame clock); false when done.</summary>
    public bool TickSweep(TimeSpan now) =>
        _active >= 0 && _active < _lines.Count && _lines[_active].Main is { IsSweeping: true } main && main.Tick(now);

    /// <summary>
    /// Lyrics without timestamps: scroll in proportion to the playback progress (0–1), so the visible part roughly
    /// follows the song. Called about once a second.
    /// </summary>
    public void SetPlainProgress(double fraction)
    {
        if (!_plain || _lines.Count == 0)
            return;
        var last = _lines[^1];
        double focus = Math.Clamp(fraction, 0, 1) * (last.Top + last.Height);
        if (Math.Abs(focus - _plainFocus) < 1)
            return;
        _plainFocus = focus;
        _motionDirty = true;

        // Materialize the lines around the focus point.
        int near = 0;
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Top <= focus) near = i;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (Math.Abs(i - near) <= MaterializeRadius) Materialize(_lines[i]);
            else Release(_lines[i]);
        }
        if (_live)
            StartScroll();
        else
            Relayout(snap: true);
    }

    /// <summary>(Re)starts the karaoke sweep on the current line (line change, play/pause, seek).</summary>
    public void SyncSweep(TimeSpan now, bool playing)
    {
        if (_active < 0 || _active >= _lines.Count || _lines[_active].Main is not { } main)
            return;
        var line = _lines[_active].Line;
        if (!_sweepEnabled || line.IsInterlude)
        {
            main.SetLitAll(true);
            return;
        }
        var end = _active + 1 < _lines.Count ? _lines[_active + 1].Line.Time : line.Time + TimeSpan.FromSeconds(5);
        main.StartSweep(line.CharTimes(end), now, playing);
    }

    private void Materialize(LineView view)
    {
        if (view.Main is not null)
            return;
        view.Main = new SweepText { DimOpacity = 0.42 };
        view.Main.SetContent(view.Line.DisplayText, view.Tokens, _mainShaper, _foreground);
        _canvas.Children.Add(view.Main);
        if (view.TranslationTokens is not null)
        {
            view.Translation = new SweepText { DimOpacity = 1, Opacity = 0.7 };
            view.Translation.SetContent(view.Line.Translation!, view.TranslationTokens, _translationShaper, _foreground);
            view.Translation.SetLitAll(true);
            _canvas.Children.Add(view.Translation);
        }
        ApplyEmphasis(_lines.IndexOf(view), animate: false);
        view.Visible = true;
        PlaceLine(view);
    }

    private void Release(LineView view)
    {
        if (view.Main is not null)
            _canvas.Children.Remove(view.Main);
        if (view.Translation is not null)
            _canvas.Children.Remove(view.Translation);
        view.Main = view.Translation = null;
    }

    /// <summary>Current line: sweep; neighbours fade with distance; sung lines fall back to dim.</summary>
    private void ApplyEmphasis(int index, bool animate)
    {
        if (index < 0 || index >= _lines.Count || _lines[index].Main is not { } main)
            return;
        if (_plain)
        {
            // No timing: every line equally readable, nothing is "current".
            main.SetLitAll(true);
            Anim.SetOpacity(main, 0.82f);
            return;
        }
        int distance = _active < 0 ? index + 1 : Math.Abs(index - _active);
        float opacity = distance == 0 ? 1f : distance == 1 ? 0.75f : distance == 2 ? 0.55f : 0.4f;
        if (index != _active)
            main.SetLitAll(false);
        if (animate)
            Anim.Fade(main, opacity, 380);
        else
            Anim.SetOpacity(main, opacity);
    }

    // ---------------------------------------------------------------- layout

    private void StartScroll()
    {
        if (_scrollTimer.IsRunning) return;
        _lastScrollFrame = Stopwatch.GetTimestamp();
        _scrollTimer.Start();
    }

    /// <returns>True while the scroll is still travelling.</returns>
    private bool Relayout(bool snap, double dt = 0)
    {
        if (_lines.Count == 0 || ActualWidth <= 0)
            return false;

        double width = ActualWidth - 2 * SidePadding;
        double anchor = ActualHeight * AnchorRatio;
        double lineHeight = _mainShaper.LineHeight;
        if (_layoutDirty)
        {
            PrepareLayout(width, anchor);
            _motionDirty = true;
        }

        // Scroll so the current line's centre sits on the anchor.
        double target = _plain
            ? Math.Clamp(_plainFocus - anchor, -anchor + lineHeight, Math.Max(-anchor + lineHeight, _documentHeight - anchor))
            : _scrollTarget;
        if (snap || !_scrollInitialized)
        {
            _scroll = target;
            _scrollFrom = _scrollTo = target;
            _scrollInitialized = true;
            _transition.Snap();
            _motionDirty = false;
        }
        else
        {
            if (_motionDirty)
            {
                _scrollFrom = _scroll;
                _scrollTo = target;
                // The target wrapping remains immutable. Remember the actually displayed token positions,
                // including an interrupted animation, then blend their residuals out with the same scroll curve.
                foreach (var view in _lines.Where(v => v.Main is not null)) CaptureReflow(view);
                _transition.Begin();
                _motionDirty = false;
            }
            _transition.Advance(dt);
            _scroll = _scrollTo + (_scrollFrom - _scrollTo) * _transition.Remaining;
        }
        bool moving = _transition.IsMoving;

        // Pass 2: place what is on screen.
        foreach (var view in _lines)
        {
            double top = view.Top - _scroll + view.ReflowTop * _transition.Remaining;
            double height = view.Height + view.ReflowHeight * _transition.Remaining;
            bool onScreen = top + height > -40 && top < ActualHeight + 40;
            if (view.Main is null)
                continue;
            if (onScreen != view.Visible)
            {
                view.Visible = onScreen;
                view.Main.Visibility = onScreen ? Visibility.Visible : Visibility.Collapsed;
                if (view.Translation is not null)
                    view.Translation.Visibility = view.Main.Visibility;
            }
            PlaceLine(view);
        }
        return moving;
    }

    /// <summary>
    /// Prepare wrapping for the destination viewport, independently of the animated scroll. Otherwise changing
    /// scroll changes circle slots, changing wrapped heights moves the destination, and the next frame repeats it.
    /// </summary>
    private void PrepareLayout(double width, double anchor)
    {
        double lineHeight = _mainShaper.LineHeight, translationHeight = _translationShaper.LineHeight;
        int center = Math.Clamp(_active, 0, _lines.Count - 1);
        double oldTop = _lines[center].Top;
        if (_plain)
        {
            // Plain progress has no fixed active line. Reserve the circle's maximum inset so its changing scroll
            // cannot alter document height and hence its own progress target.
            var slot = SlotForBand(0, ActualHeight, width);
            double y = 0;
            foreach (var view in _lines)
            {
                view.Top = y;
                view.Height = Forward(view, (_, _) => slot);
                y += view.Height + LineGap;
            }
            _documentHeight = Math.Max(0, y - LineGap);
        }
        else
        {
            var current = _lines[center];
            // A centred bilingual block gets one conservative inset for its full band. Its height converges
            // monotonically as the band grows, rather than feeding row-break discontinuities back into the clock.
            double height = Forward(current, (_, _) => (0, width));
            for (int attempt = 0; attempt <= current.Tokens.Count + (current.TranslationTokens?.Count ?? 0); attempt++)
            {
                var slot = SlotForBand(anchor - height / 2, height, width);
                double measured = Forward(current, (_, _) => slot);
                if (Math.Abs(measured - height) < 0.01) break;
                height = measured;
            }
            current.Height = height;
            current.Top = _active < 0 ? anchor - lineHeight : anchor - height / 2;

            for (int i = center + 1; i < _lines.Count; i++)
            {
                var view = _lines[i];
                view.Top = _lines[i - 1].Top + _lines[i - 1].Height + LineGap;
                view.Height = Forward(view, (top, rowHeight) => SlotForBand(top, rowHeight, width));
            }
            for (int i = center - 1; i >= 0; i--)
            {
                var view = _lines[i];
                double bottom = _lines[i + 1].Top - LineGap;
                int translatedRows = 0;
                if (view.TranslationTokens is { } tt)
                    translatedRows = TokenLayout.WrapFromBottom(tt,
                        row => SlotForBand(bottom - (row + 1) * translationHeight, translationHeight, width), view.TX, view.TRow);
                double translatedHeight = translatedRows > 0 ? TranslationGap + translatedRows * translationHeight : 0;
                double mainBottom = bottom - translatedHeight;
                int rows = TokenLayout.WrapFromBottom(view.Tokens,
                    row => SlotForBand(mainBottom - (row + 1) * lineHeight, lineHeight, width), view.X, view.Row);
                view.Height = Math.Max(1, rows) * lineHeight + translatedHeight;
                view.Top = bottom - view.Height;
            }
            double origin = _lines[0].Top;
            foreach (var view in _lines) view.Top -= origin;
            _scrollTarget = -origin;
            _documentHeight = _lines[^1].Top + _lines[^1].Height;
            // Preserve the new current line's screen position when document coordinates change at a line switch.
            if (_scrollInitialized) _scroll += _lines[center].Top - oldTop;
        }
        _layoutDirty = false;

        double Forward(LineView view, Func<double, double, (double Left, double Width)> slot)
        {
            int rows = TokenLayout.Wrap(view.Tokens, row => slot(view.Top + row * lineHeight, lineHeight), view.X, view.Row);
            double h = Math.Max(1, rows) * lineHeight;
            if (view.TranslationTokens is { } tt)
            {
                double translationTop = view.Top + h + TranslationGap;
                int translatedRows = TokenLayout.Wrap(tt, row => slot(translationTop + row * translationHeight, translationHeight), view.TX, view.TRow);
                if (translatedRows > 0) h += TranslationGap + translatedRows * translationHeight;
            }
            return h;
        }
    }

    private (double Left, double Width) SlotForBand(double top, double height, double width)
    {
        if (!_obstacle) return (0, width);
        double bottom = top + height;
        double dy = _obstacleY < top ? top - _obstacleY : _obstacleY > bottom ? _obstacleY - bottom : 0;
        double radius = _obstacleRadius + ObstacleMargin;
        if (dy >= radius) return (0, width);
        double right = _obstacleX + Math.Sqrt(radius * radius - dy * dy);
        double left = Math.Max(0, right - SidePadding);
        return (left, Math.Max(60, width - left));
    }

    private void PlaceLine(LineView view)
    {
        if (view.Main is null)
            return;
        double lineHeight = _mainShaper.LineHeight;
        double top = view.Top - _scroll;
        double remaining = _transition.Remaining;
        view.DisplayTop = top + view.ReflowTop * remaining;
        view.DisplayHeight = view.Height + view.ReflowHeight * remaining;
        for (int i = 0; i < view.Tokens.Count; i++)
            Place(view.Main, i, SidePadding + view.X[i] + view.ReflowX[i] * remaining,
                top + view.Row[i] * lineHeight + view.ReflowY[i] * remaining, lineHeight);

        if (view.Translation is not null && view.TranslationTokens is { } tt)
        {
            int rows = view.Row.Length > 0 ? view.Row[^1] + 1 : 1;
            double tTop = top + rows * lineHeight + TranslationGap;
            for (int i = 0; i < tt.Count; i++)
                Place(view.Translation, i, SidePadding + view.TX[i] + view.ReflowTX[i] * remaining,
                    tTop + view.TRow[i] * _translationShaper.LineHeight + view.ReflowTY[i] * remaining, _translationShaper.LineHeight);
        }

        void Place(SweepText text, int token, double x, double y, double height)
        {
            // A straight interpolation can cut through the circular obstacle. Follow its continuous outer arc
            // without changing wrapping or feeding the display coordinates back into the prepared layout.
            double displayedY = Math.Round(y, 1);
            double left = SlotForBand(displayedY, height, ActualWidth - 2 * SidePadding).Left + SidePadding;
            text.Place(token, Math.Max(x, Math.Ceiling(left * 10) / 10), displayedY);
        }
    }

    private void CaptureReflow(LineView view)
    {
        double top = view.Top - _scroll, lineHeight = _mainShaper.LineHeight;
        view.ReflowTop = view.DisplayTop - top;
        view.ReflowHeight = view.DisplayHeight - view.Height;
        for (int i = 0; i < view.Tokens.Count; i++)
        {
            var (x, y) = view.Main!.PositionOf(i);
            view.ReflowX[i] = double.IsFinite(x) ? x - SidePadding - view.X[i] : 0;
            view.ReflowY[i] = double.IsFinite(y) ? y - top - view.Row[i] * lineHeight : 0;
        }
        if (view.Translation is not { } translation || view.TranslationTokens is not { } tt) return;
        int rows = view.Row.Length > 0 ? view.Row[^1] + 1 : 1;
        double tTop = top + rows * lineHeight + TranslationGap;
        for (int i = 0; i < tt.Count; i++)
        {
            var (x, y) = translation.PositionOf(i);
            view.ReflowTX[i] = double.IsFinite(x) ? x - SidePadding - view.TX[i] : 0;
            view.ReflowTY[i] = double.IsFinite(y) ? y - tTop - view.TRow[i] * _translationShaper.LineHeight : 0;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        double y = e.GetCurrentPoint(this).Position.Y;
        foreach (var view in _lines)
        {
            if (view.Main is not null && view.Visible && y >= view.DisplayTop && y <= view.DisplayTop + view.DisplayHeight)
            {
                e.Handled = true;
                LineTapped?.Invoke(view.Line);
                return;
            }
        }
    }
}
