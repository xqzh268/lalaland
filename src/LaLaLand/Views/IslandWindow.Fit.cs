using LaLaLand.Controls;
using LaLaLand.Models;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Views;

/// <summary>
/// Collapsed island that shrink-wraps the current lyric line.
/// Every line is measured once when lyrics load ("prepare"); line changes are pure lookups, so the island can
/// start growing slightly <em>before</em> a longer line arrives and the text lands in a pill that already fits.
/// Lines too long for the maximum width wrap into a balanced two-line pill.
/// </summary>
public sealed partial class IslandWindow
{
    private const double CollapsedTwoLineHeight = 58;
    private const double MorphLeadMs = 160; // grow this long before a longer line appears

    private readonly TextFitter _fitter = new();
    private double _collapsedW = 320;
    private double _collapsedH = CollapsedHeight;
    private DispatcherQueueTimer _morphTimer = null!;
    private TextFitter.Fit? _pendingMorph;

    private CollapsedStyle Style => _settings.Current.CollapsedStyle;

    /// <summary>Upper bound for the collapsed width (the fixed width when fitting is off).</summary>
    private double CollapsedMaxWidth => Style == CollapsedStyle.CoverAndControls
        ? 250
        : Math.Clamp(_settings.Current.CollapsedWidth, 200, ExpandedWidth);

    private bool FitEnabled => _settings.Current.FitCollapsedToLyrics && Style != CollapsedStyle.CoverAndControls;

    /// <summary>Horizontal space taken by everything except the text (paddings, cover, spacing, equaliser / buttons).</summary>
    private double CollapsedChrome => Style switch
    {
        CollapsedStyle.LyricOnly => 18 + 18,
        CollapsedStyle.CoverAndControls => 6 + 26 + 10 + 10 + 90 - 6 + 12,
        _ => _settings.Current.ShowVisualizer && !LightweightMode ? 6 + 26 + 10 + 10 + 19.5 + 12 : 6 + 26 + 10 + 12,
    };

    /// <summary>Tallest collapsed pill possible with the current settings (used for the fixed hit region).</summary>
    private double CollapsedMaxHeight => FitEnabled && _settings.Current.CollapsedTwoLineLyrics ? CollapsedTwoLineHeight : CollapsedHeight;

    private double MinCollapsedWidth => Style == CollapsedStyle.LyricOnly ? 120 : 150;

    private void InitFit()
    {
        _morphTimer = CreateTimer(() =>
        {
            if (_pendingMorph is { } fit)
                ApplyCollapsedFit(fit, animate: true);
            _pendingMorph = null;
        });
        ConfigureFitter();
    }

    /// <summary>Must match the ticker's typography (SemiBold 13.5 in the lyric font).</summary>
    private void ConfigureFitter()
    {
        var s = _settings.Current;
        _fitter.Configure(LyricTypography.Family(s.LyricChineseFontFamily ?? s.LyricFontFamily),
            LyricTypography.Family(s.LyricEnglishFontFamily ?? s.LyricFontFamily), 13.5, FontWeights.SemiBold);
    }

    private TextFitter.Fit FitFor(string text) => FitWithTokens(text).Fit;

    private (TextFitter.Fit Fit, List<Token> Tokens) FitWithTokens(string text)
    {
        double maxText = CollapsedMaxWidth - CollapsedChrome;
        if (FitEnabled)
            return _fitter.Get(text, maxText, _settings.Current.CollapsedTwoLineLyrics);
        // Fixed width: single line, ellipsis if needed.
        var (fit, tokens) = _fitter.Get(text, maxText, allowTwoLines: false);
        return (fit with { Width = maxText }, tokens);
    }

    private (double Width, double Height) CollapsedSizeFor(TextFitter.Fit fit) => FitEnabled
        ? (Math.Clamp(Math.Ceiling(fit.Width + CollapsedChrome), MinCollapsedWidth, CollapsedMaxWidth),
           fit.TwoLines ? CollapsedTwoLineHeight : CollapsedHeight)
        : (CollapsedMaxWidth, CollapsedHeight);

    /// <summary>The "prepare" pass: tokenize and measure every lyric line once, right after the lyrics load.</summary>
    private void PrepareLyricFits()
    {
        if (!_lyrics.IsSynced)
            return;
        for (int i = 0; i < _lyrics.Lines.Count; i++)
            FitFor(LineDisplayText(i));
    }

    private string LineDisplayText(int index)
    {
        return _lyrics.Lines[index].DisplayText;
    }

    /// <summary>
    /// Lays a ticker out from the fit: rows split at <see cref="TextFitter.Fit.BreakAt"/>, each row centred for the
    /// lyric-only style, an ellipsis if the text was truncated. The outgoing ticker keeps its own layout.
    /// </summary>
    private void ShapeTicker(SweepText ticker, string text, TextFitter.Fit fit, List<Token> tokens)
    {
        var shaper = _fitter.Shaper;
        ticker.SetContent(text, tokens, shaper, _tickerBrush);
        bool centre = Style == CollapsedStyle.LyricOnly;
        double lineHeight = shaper.LineHeight;
        int visible = Math.Min(fit.VisibleCount, tokens.Count);
        int firstEnd = fit.BreakAt > 0 ? Math.Min(fit.BreakAt, visible) : visible;
        bool truncated = visible < tokens.Count;

        PlaceRow(0, firstEnd, 0, truncated && fit.BreakAt <= 0);
        int rows = 1;
        if (fit.BreakAt > 0 && visible > fit.BreakAt)
        {
            PlaceRow(fit.BreakAt, visible, lineHeight, truncated);
            rows = 2;
        }
        else if (fit.BreakAt > 0 && truncated)
        {
            rows = 2;
        }

        ticker.Width = fit.Width;
        ticker.Height = rows * lineHeight;

        void PlaceRow(int from, int to, double y, bool withEllipsis)
        {
            double rowWidth = TokenLayout.LineWidth(tokens, from, to) + (withEllipsis ? ticker.EllipsisWidth : 0);
            double x = centre ? Math.Max(0, (fit.Width - rowWidth) / 2) : 0;
            for (int i = from; i < to; i++)
            {
                ticker.Place(i, x, y);
                x += tokens[i].Width + tokens[i].Gap;
            }
            if (withEllipsis)
                ticker.TruncateAt(to, x - (to > from ? tokens[to - 1].Gap : 0), y);
        }
    }

    /// <summary>Arms an early grow when the upcoming line needs more room than the current pill.</summary>
    private void ScheduleMorphFor(int nextIndex, TimeSpan wait)
    {
        _morphTimer.Stop();
        _pendingMorph = null;
        if (!FitEnabled || _state != IslandState.Collapsed || Style == CollapsedStyle.CoverAndControls || nextIndex >= _lyrics.Lines.Count)
            return;

        var fit = FitFor(LineDisplayText(nextIndex));
        var (w, h) = CollapsedSizeFor(fit);
        bool grows = w > _collapsedW + 0.5 || h > _collapsedH;
        if (!grows || wait.TotalMilliseconds <= MorphLeadMs + 60)
            return; // shrinking happens together with the text swap

        _pendingMorph = fit;
        _morphTimer.Interval = wait - TimeSpan.FromMilliseconds(MorphLeadMs);
        _morphTimer.Start();
    }

    private void ApplyCollapsedFit(TextFitter.Fit fit, bool animate)
    {
        var (w, h) = CollapsedSizeFor(fit);
        if (Math.Abs(w - _collapsedW) < 0.5 && Math.Abs(h - _collapsedH) < 0.5)
            return;

        double oldW = _collapsedW, oldH = _collapsedH;
        _collapsedW = w;
        _collapsedH = h;
        ApplyConcentricCorners();

        if (_state != IslandState.Collapsed)
            return; // picked up by the next GoTo(Collapsed)

        var (_, _, r) = Metrics(IslandState.Collapsed);
        // No window-region updates here: the collapsed region already covers the largest pill (see UpdateRegion).
        if (animate && _isShown)
        {
            bool growing = w * h >= oldW * oldH;
            double duration = growing ? 460 : 400;
            AnimateShape("W", w, duration, growing);
            AnimateShape("H", h, duration, growing);
            AnimateShape("R", r, duration, growing);
        }
        else
        {
            foreach (var (name, value) in new[] { ("W", w), ("H", h), ("R", r) })
            {
                _shape.StopAnimation(name);
                _shape.InsertScalar(name, (float)value);
                _backdrop.Set(name, (float)value);
                NoteShape(name, value, 0);
            }
        }
    }
}
