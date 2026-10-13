using System.Numerics;
using LaLaLand.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace LaLaLand.Views;

/// <summary>
/// Full view "vinyl": the album art becomes a record that sits half off the left edge, vertically centred across the
/// whole expanded island, shared by the lyrics and the controls. Lyrics (and the card rows) flow around its arc;
/// the record spins (on the shared frame clock) while playing.
/// </summary>
public sealed partial class IslandWindow
{
    private const double DiscRadius = 118;
    private const double DiscCenterX = DiscRadius * 0.25;   // a bit more than half of the record shows
    private const double DiscFlowMargin = 12;

    private Grid _disc = null!;
    private Grid _discSpinner = null!;
    private ImageBrush _discArt = null!;
    private Ellipse _discLabel = null!;
    private bool _vinylOn;
    private readonly Dictionary<FrameworkElement, Thickness> _cardMargins = new();

    private double DiscCenterY => FullHeight / 2;

    private void InitVinyl()
    {
        double d = DiscRadius * 2;
        _discSpinner = new Grid { Width = d, Height = d };
        _discSpinner.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(255, 14, 14, 16)) });
        foreach (var ratio in new[] { 0.96, 0.9, 0.84, 0.78, 0.72, 0.66 })
        {
            _discSpinner.Children.Add(new Ellipse
            {
                Width = d * ratio,
                Height = d * ratio,
                Stroke = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)),
                StrokeThickness = 0.7,
            });
        }
        _discArt = new ImageBrush { Stretch = Stretch.UniformToFill };
        _discLabel = new Ellipse { Width = d * 0.58, Height = d * 0.58, Fill = _accentBrush };
        _discSpinner.Children.Add(_discLabel);
        _discSpinner.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromArgb(255, 14, 14, 16)) });

        // Fixed specular sheen on top: light stays put while the record turns underneath.
        var sheen = new Ellipse
        {
            Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Offset = 0.2, Color = Color.FromArgb(0, 255, 255, 255) },
                    new GradientStop { Offset = 0.45, Color = Color.FromArgb(0x1E, 255, 255, 255) },
                    new GradientStop { Offset = 0.55, Color = Color.FromArgb(0x08, 255, 255, 255) },
                    new GradientStop { Offset = 0.8, Color = Color.FromArgb(0, 255, 255, 255) },
                },
            },
        };

        _disc = new Grid
        {
            Width = d,
            Height = d,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(DiscCenterX - DiscRadius, DiscCenterY - DiscRadius, 0, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _disc.Children.Add(_discSpinner);
        _disc.Children.Add(sheen);
        Grid.SetRowSpan(_disc, 2);
        ExpandedLayer.Children.Insert(0, _disc);

        Anim.Visual(_disc).CenterPoint = new Vector3((float)DiscRadius, (float)DiscRadius, 0);
        Anim.Visual(_discSpinner).CenterPoint = new Vector3((float)DiscRadius, (float)DiscRadius, 0);
        Anim.Visual(_disc).Opacity = 0;
    }

    private void SetDiscArt(ImageSource? art)
    {
        _discArt.ImageSource = art;
        _discLabel.Fill = art is null ? _accentBrush : _discArt;
    }

    /// <summary>Shows / hides the record when entering / leaving the full state (or toggling the setting).</summary>
    private void UpdateVinyl()
    {
        bool on = _state == IslandState.Full && _settings.Current.LyricFlowAroundCover;
        if (on == _vinylOn)
        {
            UpdateSpin();
            return;
        }
        _vinylOn = on;

        if (on)
        {
            _disc.Visibility = Visibility.Visible;
            Anim.Fade(_disc, 1, 420, delayMs: 80);
            Anim.Scale(_disc, Vector3.One, 620, delayMs: 40, easing: _expandEasing, from: new Vector3(0.8f, 0.8f, 1));
            Anim.Fade(CoverBorder, 0, 160);
        }
        else
        {
            Anim.Fade(_disc, 0, 160);
            Anim.Fade(CoverBorder, 1, 260, delayMs: 80);
        }

        // The card rows step aside for the record; hide the layout jump with a quick fade of the card content.
        Anim.SetOpacity(NowPlayingCard, 0);
        ApplyCardInsets(on);
        Anim.Fade(NowPlayingCard, 1, 280, delayMs: 60);

        UpdateDiscObstacle();
        UpdateSpin();
    }

    /// <summary>The record spins on the frame clock while playing; pause freezes it where it is.</summary>
    private void UpdateSpin() => EnsureFrames();

    /// <summary>Card rows are inset by the record's chord at their own height, so they hug the arc too.</summary>
    private void ApplyCardInsets(bool on)
    {
        var header = (Grid)NowPlayingCard.Children[0];
        header.ColumnDefinitions[0].Width = new GridLength(on ? 0 : 64);
        header.ColumnSpacing = on ? 0 : 14;
        ExpandedLayer.UpdateLayout();

        foreach (FrameworkElement row in NowPlayingCard.Children)
        {
            if (!_cardMargins.ContainsKey(row))
                _cardMargins[row] = row.Margin;
            var original = _cardMargins[row];
            double inset = 0;
            if (on)
            {
                double y = row.TransformToVisual(ExpandedLayer).TransformPoint(new Windows.Foundation.Point(0, row.ActualHeight / 2)).Y;
                double r = DiscRadius + DiscFlowMargin;
                double dy = Math.Abs(y - DiscCenterY);
                if (dy < r)
                    inset = Math.Max(0, DiscCenterX + Math.Sqrt(r * r - dy * dy) - NowPlayingCard.Padding.Left);
            }
            row.Margin = new Thickness(original.Left + inset, original.Top, original.Right, original.Bottom);
        }
    }

    /// <summary>Tells the lyric panel where the record is, in its own coordinates.</summary>
    private void UpdateDiscObstacle()
    {
        double panelTop = _ay == 0f ? CardHeight : 0; // lyrics sit below the card when anchored at the top
        LyricFlow.SetObstacle(_vinylOn, DiscCenterX, DiscCenterY - panelTop, DiscRadius);
        LyricsView.SetMessageInset(_vinylOn ? DiscCenterX + DiscRadius + DiscFlowMargin : 0);
    }
}
