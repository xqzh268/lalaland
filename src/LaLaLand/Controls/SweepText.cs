using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LaLaLand.Controls;

/// <summary>
/// Text rendered token by token at caller-supplied positions, with a karaoke sweep.
/// <para>
/// Each token is a dim copy with a bright copy on top; the bright copy is revealed by an inset clip whose right
/// edge is driven by a key-frame animation built from the per-character timings. Because every token knows its
/// own position, a line that wraps continues the sweep exactly at the start of the next row — and the whole
/// sweep runs on the compositor with no per-frame work.
/// </para>
/// </summary>
public sealed class SweepText : Canvas
{
    private sealed class TokenVisual
    {
        public required Grid Root;
        public required TextBlock Lit;
        public required InsetClip Clip;
        public Token Token;
        /// <summary>Explicit element width (measured width rounded up + slack) so clip maths has no layout surprises.</summary>
        public double ElementWidth;
        public float LastX = float.NaN, LastY = float.NaN;
        public double LastFraction = -1;
    }

    private readonly List<TokenVisual> _visuals = [];
    private readonly TextBlock _ellipsis = new() { Text = "…", Visibility = Visibility.Collapsed };
    private IReadOnlyList<Token> _tokens = [];
    private TextShaper? _shaper;
    private string _text = "";
    private double _dimOpacity = 0.45;
    private (TimeSpan Start, TimeSpan End)[]? _times;

    public SweepText()
    {
        IsHitTestVisible = false;
        Children.Add(_ellipsis);
    }

    internal IReadOnlyList<Token> Tokens => _tokens;

    public string Text => _text;

    /// <summary>Opacity of the not-yet-sung part.</summary>
    public double DimOpacity
    {
        get => _dimOpacity;
        set
        {
            _dimOpacity = value;
            foreach (var v in _visuals)
                ((TextBlock)v.Root.Children[0]).Opacity = value;
        }
    }

    internal void SetContent(string text, IReadOnlyList<Token> tokens, TextShaper shaper, Brush foreground)
    {
        _text = text;
        _tokens = tokens;
        _shaper = shaper;

        // Reuse token visuals; create more on demand.
        while (_visuals.Count < tokens.Count)
            _visuals.Add(CreateVisual());
        for (int i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            if (i >= tokens.Count)
            {
                v.Root.Visibility = Visibility.Collapsed;
                continue;
            }
            var t = tokens[i];
            v.Token = t;
            v.ElementWidth = Math.Ceiling(t.Width) + 2;
            var dim = (TextBlock)v.Root.Children[0];
            dim.Width = v.Lit.Width = v.ElementWidth;
            dim.Opacity = _dimOpacity;
            shaper.Style(dim, t.Text);
            shaper.Style(v.Lit, t.Text);
            v.Root.Visibility = Visibility.Visible;
            SetLit(v, 0);
        }
        shaper.Style(_ellipsis, "…");
        SetForeground(foreground);
        _ellipsis.Visibility = Visibility.Collapsed;
    }

    /// <summary>Changes ink without rebuilding glyphs, rewrapping, or restarting word timings.</summary>
    public void SetForeground(Brush foreground)
    {
        double x = 0, total = TokenLayout.LineWidth(_tokens, 0, _tokens.Count);
        for (int i = 0; i < _tokens.Count; i++)
        {
            var v = _visuals[i];
            var ink = LyricColors.ForToken(foreground, x, v.ElementWidth, total);
            ((TextBlock)v.Root.Children[0]).Foreground = v.Lit.Foreground = ink;
            x += _tokens[i].Width + _tokens[i].Gap;
        }
        _ellipsis.Foreground = foreground;
    }

    private TokenVisual CreateVisual()
    {
        var dim = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        var lit = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        var root = new Grid();
        root.Children.Add(dim);
        root.Children.Add(lit);
        Children.Add(root);
        var visual = Anim.Visual(lit);
        var clip = visual.Compositor.CreateInsetClip();
        visual.Clip = clip;
        return new TokenVisual { Root = root, Lit = lit, Clip = clip };
    }

    /// <summary>Positions token <paramref name="index"/> (no layout pass: uses the Translation facade).</summary>
    public void Place(int index, double x, double y)
    {
        var v = _visuals[index];
        float fx = (float)Math.Round(x, 1), fy = (float)Math.Round(y, 1);
        if (fx == v.LastX && fy == v.LastY)
            return; // unchanged: no composition traffic
        v.LastX = fx;
        v.LastY = fy;
        v.Root.Translation = new Vector3(fx, fy, 0);
    }

    /// <summary>The last submitted position, used to retarget a reflow without jumping mid-transition.</summary>
    internal (double X, double Y) PositionOf(int index) => (_visuals[index].LastX, _visuals[index].LastY);

    /// <summary>Hides tokens from <paramref name="index"/> on and shows an ellipsis at (x, y) instead.</summary>
    public void TruncateAt(int index, double x, double y)
    {
        for (int i = index; i < _tokens.Count; i++)
            _visuals[i].Root.Visibility = Visibility.Collapsed;
        _ellipsis.Visibility = Visibility.Visible;
        _ellipsis.Translation = new Vector3((float)x, (float)y, 0);
    }

    public double EllipsisWidth => _shaper?.Width("…") ?? 10;

    /// <summary>Whole text bright (true) or dim (false), without a sweep.</summary>
    public void SetLitAll(bool lit)
    {
        _times = null;
        for (int i = 0; i < _tokens.Count; i++)
            SetLit(_visuals[i], lit ? 1 : 0);
        _ellipsis.Opacity = lit ? 1 : _dimOpacity;
    }

    /// <summary>
    /// Reveals the bright copy up to <paramref name="fraction"/> of the token. Unlit tokens hide the bright copy
    /// entirely (a zero-width clip can still leak an anti-aliased sliver), fully lit ones show glyph overhang too.
    /// </summary>
    private static void SetLit(TokenVisual v, double fraction)
    {
        v.Clip.StopAnimation("RightInset");
        v.Lit.Opacity = fraction <= 0 ? 0 : 1;
        v.Clip.RightInset = (float)(fraction >= 1 ? -3 : RightInsetFor(v, v.Token.Width * fraction));
    }

    /// <summary>Inset that reveals the bright copy up to x (in token coordinates).</summary>
    private static double RightInsetFor(TokenVisual v, double x) => v.ElementWidth - x;

    /// <summary>
    /// Starts (or re-syncs) the karaoke sweep. <paramref name="charTimes"/> covers every character of the text.
    /// The sweep is advanced by <see cref="Tick"/> from a shared low-rate clock — no continuously running
    /// compositor animation, so the window is only redrawn when a token actually changes.
    /// </summary>
    public void StartSweep((TimeSpan Start, TimeSpan End)[] charTimes, TimeSpan now, bool playing)
    {
        if (charTimes.Length < _text.Length)
        {
            SetLitAll(true);
            return;
        }
        _ellipsis.Opacity = 1;
        _times = charTimes;
        for (int i = 0; i < _visuals.Count; i++)
            _visuals[i].LastFraction = -1;
        Tick(now);
    }

    /// <summary>True while some token of the current sweep is not fully lit yet.</summary>
    public bool IsSweeping => _times is not null;

    /// <summary>Advances the sweep to <paramref name="now"/>; returns false once everything is lit.</summary>
    public bool Tick(TimeSpan now)
    {
        if (_times is null)
            return false;
        bool pending = false;
        for (int i = 0; i < _tokens.Count; i++)
        {
            var v = _visuals[i];
            double fraction = FractionAt(_times, v.Token, now);
            if (fraction < 1)
                pending = true;
            if (Math.Abs(fraction - v.LastFraction) < 0.004)
                continue; // unchanged: no composition traffic
            v.LastFraction = fraction;
            SetLit(v, fraction);
        }
        if (!pending)
            _times = null;
        return pending;
    }

    private static double FractionAt((TimeSpan Start, TimeSpan End)[] times, Token t, TimeSpan now)
    {
        int done = 0;
        double partial = 0;
        for (int c = 0; c < t.Length; c++)
        {
            var ct = times[t.Start + c];
            if (now >= ct.End)
                done++;
            else if (now > ct.Start)
                partial = (now - ct.Start) / (ct.End - ct.Start);
        }
        return Math.Clamp((done + partial) / t.Length, 0, 1);
    }
}
