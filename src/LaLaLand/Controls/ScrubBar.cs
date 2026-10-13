using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LaLaLand.Controls;

/// <summary>
/// Thin, draggable bar (progress / volume). Thickens and shows a knob on hover; while dragging it previews the
/// value and only commits on release. Fill and knob are composition-driven, and the knob follows the fill via an
/// expression, so a playing progress bar needs a single linear animation and no per-frame work.
/// </summary>
public sealed class ScrubBar : Grid
{
    private const float KnobSize = 12;
    private const float HoverThickness = 1.75f;

    private readonly Grid _bar;
    private readonly Rectangle _fill;
    private readonly Ellipse _knob;
    private readonly Visual _fillVisual;
    private ExpressionAnimation? _knobFollow;
    private bool _dragging;
    private bool _hovering;
    private double _value;

    /// <summary>Raised continuously while dragging, with the previewed value (0–1).</summary>
    public event Action<double>? Scrubbing;

    /// <summary>Raised once when the user releases the bar (click or drag).</summary>
    public event Action<double>? Committed;

    public bool IsDragging => _dragging;

    public double Value => _value;

    public Brush? FillBrush
    {
        get => _fill.Fill;
        set => _fill.Fill = value;
    }

    public ScrubBar()
    {
        Height = 16;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // whole height is hit-testable

        _bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center };
        _bar.Children.Add(new Rectangle { RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 255, 255, 255)) });
        _fill = new Rectangle { RadiusX = 2, RadiusY = 2 };
        _bar.Children.Add(_fill);
        Children.Add(_bar);

        _knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
            IsHitTestVisible = false,
        };
        Children.Add(_knob);

        _fillVisual = Anim.Visual(_fill);
        _fillVisual.Scale = new Vector3(0, 1, 1);
        Anim.EnableTranslation(_knob);
        var knobVisual = Anim.Visual(_knob);
        knobVisual.Opacity = 0;
        knobVisual.CenterPoint = new Vector3(KnobSize / 2, KnobSize / 2, 0);
        knobVisual.Scale = new Vector3(0.5f, 0.5f, 1);

        SizeChanged += (_, _) => BindKnob();
        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag(commit: false);
    }

    /// <summary>Shows a static value (stops any running animation). Ignored while the user is dragging.</summary>
    public void SetValue(double value)
    {
        _value = Math.Clamp(value, 0, 1);
        if (_dragging)
            return;
        _fillVisual.StopAnimation("Scale.X");
        _fillVisual.Scale = new Vector3((float)_value, 1, 1);
    }

    /// <summary>Animates linearly from <paramref name="from"/> to the end over <paramref name="duration"/>.</summary>
    public void PlayToEnd(double from, TimeSpan duration)
    {
        _value = Math.Clamp(from, 0, 1);
        if (_dragging)
            return;
        var c = _fillVisual.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0f, (float)_value);
        a.InsertKeyFrame(1f, 1f, Anim.Linear(c));
        a.Duration = duration;
        _fillVisual.StartAnimation("Scale.X", a);
    }

    private void BindKnob()
    {
        // Knob centre = fill width; follows the fill's (possibly animating) scale on the compositor.
        _knobFollow ??= _fillVisual.Compositor.CreateExpressionAnimation();
        _knobFollow.Expression = $"Vector3(f.Scale.X * {(float)ActualWidth} - {KnobSize / 2}, 0, 0)";
        _knobFollow.SetReferenceParameter("f", _fillVisual);
        Anim.Visual(_knob).StartAnimation("Translation", _knobFollow);
        Anim.Visual(_bar).CenterPoint = new Vector3(0, (float)_bar.Height / 2, 0);
    }

    private void SetHover(bool hover)
    {
        _hovering = hover;
        bool show = hover || _dragging;
        Anim.Scale(_bar, new Vector3(1, show ? HoverThickness : 1, 1), 220);
        Anim.Fade(_knob, show ? 1 : 0, 180);
        Anim.Scale(_knob, show ? Vector3.One : new Vector3(0.5f, 0.5f, 1), 260);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        _dragging = CapturePointer(e.Pointer);
        if (!_dragging)
            return;
        Anim.Scale(_knob, new Vector3(1.25f, 1.25f, 1), 160);
        Preview(e);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        e.Handled = true;
        Preview(e);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        e.Handled = true;
        Preview(e);
        EndDrag(commit: true); // before releasing capture, which raises PointerCaptureLost
        ReleasePointerCapture(e.Pointer);
    }

    private void Preview(PointerRoutedEventArgs e)
    {
        double width = ActualWidth;
        if (width <= 0)
            return;
        _value = Math.Clamp(e.GetCurrentPoint(this).Position.X / width, 0, 1);
        _fillVisual.StopAnimation("Scale.X");
        _fillVisual.Scale = new Vector3((float)_value, 1, 1);
        Scrubbing?.Invoke(_value);
    }

    private void EndDrag(bool commit)
    {
        if (!_dragging)
            return;
        _dragging = false;
        Anim.Scale(_knob, Vector3.One, 200);
        SetHover(_hovering);
        if (commit)
            Committed?.Invoke(_value);
    }
}
