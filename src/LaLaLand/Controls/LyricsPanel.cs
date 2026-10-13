using System.Numerics;
using LaLaLand.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Controls;

/// <summary>
/// Apple-Music-style lyric view: the active line sits slightly above centre and the whole column glides to it.
/// Built in code (no XAML) to keep it light; all motion is composition-driven.
/// </summary>
public sealed class LyricsPanel : Grid
{
    private const float AnchorRatio = 0.38f;
    private const float ActiveOpacity = 1f;
    private const float NearOpacity = 0.42f;
    private const float FarOpacity = 0.24f;
    private const float InactiveScale = 0.94f;

    private readonly ScrollViewer _scroller;
    private readonly StackPanel _stack;
    private readonly TextBlock _message;
    private readonly List<StackPanel> _lines = [];
    private Lyrics _lyrics = Lyrics.Empty;
    private int _active = -1;

    public event Action<LyricLine>? LineTapped;

    private LyricTypography _typography = new();
    private Brush _lyricBrush = new SolidColorBrush(Microsoft.UI.Colors.White);

    public void SetFonts(FontFamily? chinese, FontFamily? english)
    {
        _typography = new LyricTypography(chinese, english);
        foreach (var line in _lines)
            foreach (var text in line.Children.OfType<TextBlock>()) ApplyFont(text);
    }

    public void SetForeground(Brush foreground)
    {
        _lyricBrush = foreground;
        foreach (var line in _lines)
            foreach (var text in line.Children.OfType<TextBlock>()) text.Foreground = foreground;
    }

    private void ApplyFont(TextBlock text)
    {
        _typography.Apply(text, (string)text.Tag);
    }

    public LyricsPanel()
    {
        _stack = new StackPanel { Padding = new Thickness(22, 0, 22, 0) };
        _scroller = new ScrollViewer
        {
            Content = _stack,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
        };
        _message = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24, 16, 24, 16),
            FontSize = 14,
            Opacity = 0.7,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_scroller);
        Children.Add(_message);
        Anim.EnableTranslation(_stack);

        SizeChanged += (_, _) =>
        {
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight) };
            Recenter(animate: false);
        };
        _stack.SizeChanged += (_, _) => Recenter(animate: false);
    }

    public void ShowMessage(string text)
    {
        _lyrics = Lyrics.Empty;
        _active = -1;
        _lines.Clear();
        _stack.Children.Clear();
        _message.Text = text.Replace("，未找到可靠匹配；", "\n未找到可靠匹配\n").Replace("；", "\n");
        _message.Visibility = Visibility.Visible;
    }

    public void SetMessageInset(double left)
    {
        double inset = 24 + Math.Max(0, left);
        if (Math.Abs(_message.Margin.Left - inset) < 0.25) return;
        _message.Margin = new Thickness(inset, 16, 24, 16);
    }

    public void SetLyrics(Lyrics lyrics, bool showTranslation)
    {
        if (lyrics.IsEmpty)
        {
            ShowMessage("暂无歌词");
            return;
        }

        _lyrics = lyrics;
        _active = -1;
        _lines.Clear();
        _stack.Children.Clear();
        _message.Visibility = Visibility.Collapsed;

        bool synced = lyrics.IsSynced;
        _scroller.VerticalScrollMode = synced ? ScrollMode.Disabled : ScrollMode.Enabled;
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
        _stack.Padding = new Thickness(22, synced ? 0 : 12, 22, synced ? 0 : 24);

        foreach (var line in lyrics.Lines)
        {
            var panel = new StackPanel { Padding = new Thickness(0, 7, 0, 7), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            panel.Children.Add(new TextBlock
            {
                Text = line.DisplayText,
                Tag = line.DisplayText,
                FontSize = synced ? 18 : 15,
                FontWeight = synced ? FontWeights.Bold : FontWeights.SemiBold,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = _lyricBrush,
            });
            if (showTranslation && !string.IsNullOrWhiteSpace(line.Translation))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = line.Translation,
                    Tag = line.Translation,
                    FontSize = 13.5,
                    Margin = new Thickness(0, 3, 0, 0),
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Foreground = _lyricBrush,
                });
            }

            if (synced)
            {
                var captured = line;
                panel.PointerReleased += (_, e) =>
                {
                    e.Handled = true;
                    LineTapped?.Invoke(captured);
                };
                panel.SizeChanged += (s, _) =>
                {
                    var p = (StackPanel)s;
                    Anim.Visual(p).CenterPoint = new Vector3(0, (float)p.ActualHeight / 2, 0);
                };
                var v = Anim.Visual(panel);
                v.Opacity = FarOpacity;
                v.Scale = new Vector3(InactiveScale, InactiveScale, 1);
            }

            foreach (var text in panel.Children.OfType<TextBlock>())
                ApplyFont(text);
            _lines.Add(panel);
            _stack.Children.Add(panel);
        }

        Anim.SetTranslation(_stack, new Vector3(0, synced ? (float)ActualHeight * AnchorRatio : 0, 0));
    }

    public void SetActive(int index, bool animate = true)
    {
        if (!_lyrics.IsSynced || _lines.Count == 0)
            return;
        index = Math.Clamp(index, -1, _lines.Count - 1);
        if (index == _active)
            return;

        int previous = _active;
        _active = index;

        // Only touch lines whose emphasis may have changed.
        int lo = Math.Max(0, Math.Min(previous, index) - 2);
        int hi = Math.Min(_lines.Count - 1, Math.Max(previous, index) + 2);
        for (int i = lo; i <= hi; i++)
        {
            int distance = index < 0 ? int.MaxValue : Math.Abs(i - index);
            float opacity = distance == 0 ? ActiveOpacity : distance == 1 ? NearOpacity : FarOpacity;
            float scale = distance == 0 ? 1f : InactiveScale;
            if (animate)
            {
                Anim.Fade(_lines[i], opacity, 420);
                Anim.Scale(_lines[i], new Vector3(scale, scale, 1), 520);
            }
            else
            {
                var v = Anim.Visual(_lines[i]);
                v.StopAnimation("Opacity");
                v.StopAnimation("Scale");
                v.Opacity = opacity;
                v.Scale = new Vector3(scale, scale, 1);
            }
        }

        Recenter(animate);
    }

    private void Recenter(bool animate)
    {
        if (!_lyrics.IsSynced || _lines.Count == 0 || ActualHeight <= 0)
            return;

        float anchor = (float)ActualHeight * AnchorRatio;
        float y;
        if (_active < 0)
        {
            // Before the first line: let the first line rest just below the anchor.
            y = anchor;
        }
        else
        {
            var line = _lines[_active];
            float center = line.ActualOffset.Y + (float)line.ActualHeight / 2;
            y = anchor - center;
        }

        if (animate)
            Anim.Translate(_stack, new Vector3(0, y, 0), 560);
        else
            Anim.SetTranslation(_stack, new Vector3(0, y, 0));
    }
}
