using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using LaLaLand.Controls;
using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace LaLaLand.Views;

public sealed partial class IslandWindow
{
    private static readonly string PlayGlyphChar = char.ConvertFromUtf32(0xE768);
    private static readonly string PauseGlyphChar = char.ConvertFromUtf32(0xE769);
    private static readonly string HeartOutline = char.ConvertFromUtf32(0xEB51);
    private static readonly string HeartFilled = char.ConvertFromUtf32(0xEB52);
    private static readonly Color DefaultAccent = Color.FromArgb(255, 235, 235, 245);
    private static readonly Color FavoriteColor = Color.FromArgb(255, 255, 77, 109);

    private readonly SolidColorBrush _accentBrush = new(DefaultAccent);
    private DispatcherQueueTimer _lyricTimer = null!;
    private DispatcherQueueTimer _clockTimer = null!;
    private CancellationTokenSource? _lyricsCts;
    private string? _lyricLookupTrackKey;
    private TimeSpan _lyricLookupDuration;
    private Lyrics _lyrics = Lyrics.Empty;
    private int _lyricIndex = -2;
    private bool _tickerFlip;
    private string _tickerText = "";
    private bool _backdropFlip;
    private bool? _isFavorite;
    private bool _lastShowTranslation;
    private bool _lastLyricsEnabled = true;
    private string? _appliedChineseLyricFont, _appliedEnglishLyricFont, _appliedLyricColor;

    private void InitPlayback()
    {
        _lyricTimer = CreateTimer(SyncLyrics);
        _clockTimer = _dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => UpdateClock();

        ProgressBar.FillBrush = _accentBrush;
        ProgressBar.Scrubbing += OnProgressScrubbing;
        ProgressBar.Committed += OnProgressCommitted;
        VolumeBar.FillBrush = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255));
        VolumeBar.Scrubbing += v => SetVolumeCoalesced((float)v);
        VolumeBar.Committed += v => SetVolumeCoalesced((float)v);
        Root.PointerWheelChanged += OnWheel;
        _hudTimer = CreateTimer(HideVolumeHud);
        _hudTimer.Interval = TimeSpan.FromMilliseconds(1400);
        Anim.Visual(VolumeHud).Opacity = 0;
        Anim.Visual(HudFill).Scale = new Vector3(0, 1, 1);
        foreach (var bar in MiniVisualizer.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>())
            bar.Fill = _accentBrush;

        Anim.EnableTranslation(TickerA);
        Anim.EnableTranslation(TickerB);
        Anim.Visual(TickerB).Opacity = 0;
        Anim.Visual(BackdropA).Opacity = 0;
        Anim.Visual(BackdropB).Opacity = 0;

        _media.TrackChanged += OnTrackChanged;
        _media.ArtworkChanged += OnArtworkChanged;
        _media.PlaybackStateChanged += OnPlaybackStateChanged;
        _media.TimelineChanged += OnTimelineChanged;
        _lyricsService.Changed += OnLyricsChanged;
        LyricsView.LineTapped += SeekLyricLine;
        LyricFlow.LineTapped += SeekLyricLine;

        _lastShowTranslation = _settings.Current.ShowTranslation;
        _lastLyricsEnabled = _settings.Current.LyricsEnabled;
        OnTrackChanged();
        // Monitoring may have resumed before this replacement window was constructed.
        OnPlaybackStateChanged();
        OnArtworkChanged();
        OnTimelineChanged();
    }

    private void OnStateChanged(IslandState from)
    {
        bool expanded = _state != IslandState.Collapsed;
        if (expanded)
        {
            HideVolumeHud();
            _ = RefreshVolumeAsync();
        }
        if (expanded)
            App.Trimmer?.Cancel();
        else
            App.Trimmer?.Schedule(TimeSpan.FromSeconds(30));
        UpdateProgress();
        if (expanded)
        {
            HideVolumeHud();
            _ = RefreshVolumeAsync();
        }
        if (expanded)
        {
            UpdateClock();
            if (_media.IsPlaying)
                _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }

        // Snap the lyric column to the current line right before it becomes visible.
        if (_state == IslandState.Full && from != IslandState.Full)
            LyricsView.SetActive(_lyricIndex, animate: false);

        // The flowing lyric view only runs its per-frame loop while it is on screen.
        LyricFlow.SetLive(_state == IslandState.Full);
        UpdateVinyl();

        // Re-arm the frame clock at the rate this state needs (20 Hz collapsed, 30 Hz expanded).
        _frameTimer?.Stop();
        EnsureFrames();

        UpdateTicker(animate: false);
        UpdateVisualizer();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// Material recipes. Glass materials sit on top of the system-blurred desktop (see <see cref="Interop.IslandBackdrop"/>):
    /// tint for legibility, optional frost and grain, and size-aware composition highlights (sheen + rim).
    /// </summary>
    private void ApplyMaterial()
    {
        var s = _settings.Current;
        var material = s.Material;
        double dim = Math.Clamp(s.BackdropDim, 0, 0.85);

        bool glass = material is IslandMaterial.Acrylic or IslandMaterial.Frosted or IslandMaterial.LiquidGlass;
        // Game mode: no live blur (DWM would re-blur the moving game every frame); glass turns into a static dark tint.
        _backdrop.BlurEnabled = glass && !LightweightMode;
        BaseFill.Visibility = material is IslandMaterial.Solid or IslandMaterial.AlbumGlow ? Visibility.Visible : Visibility.Collapsed;
        BaseFill.Fill = new SolidColorBrush(material == IslandMaterial.Solid ? ParseColor(s.SolidColor) : Microsoft.UI.Colors.Black);
        BackdropA.Visibility = BackdropB.Visibility = material == IslandMaterial.AlbumGlow ? Visibility.Visible : Visibility.Collapsed;

        (Color tint, double tintOpacity, double frost, double noise) = material switch
        {
            IslandMaterial.Acrylic => (Color.FromArgb(255, 28, 28, 32), 0.40 + dim * 0.5, 0.0, 0.03),
            IslandMaterial.Frosted => (Color.FromArgb(255, 16, 16, 20), 0.12 + dim * 0.5, 0.13, 0.07),
            IslandMaterial.LiquidGlass => (Color.FromArgb(255, 0, 0, 0), dim * 0.55, 0.02, 0.0),
            IslandMaterial.AlbumGlow => (Color.FromArgb(255, 0, 0, 0), dim, 0.0, 0.025),
            _ => (Color.FromArgb(255, 0, 0, 0), 0.0, 0.0, 0.0),
        };
        Tint.Fill = new SolidColorBrush(tint);
        Tint.Opacity = glass && LightweightMode ? Math.Max(tintOpacity, 0.9) : tintOpacity;
        Frost.Opacity = frost;
        Noise.Opacity = noise;
        if (noise > 0 && Noise.Source is null)
            Noise.Source = CreateNoiseTexture(420);

        // Rim light (above content) and sheen (below content)
        var c = _compositor;
        var (start, end) = (new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(0, 1));
        var diagonal = (new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(1, 1));
        (_edgeShape.StrokeBrush, _edgeShape.StrokeThickness) = material switch
        {
            IslandMaterial.Acrylic => (Gradient(start, end, (0f, 0x30), (0.5f, 0x10), (1f, 0x18)), 1f),
            IslandMaterial.Frosted => (Gradient(start, end, (0f, 0x58), (0.5f, 0x22), (1f, 0x32)), 1f),
            IslandMaterial.LiquidGlass => (Gradient(diagonal.Item1, diagonal.Item2, (0f, 0xC0), (0.3f, 0x24), (0.7f, 0x14), (1f, 0x80)), 1.4f),
            IslandMaterial.AlbumGlow => (Gradient(start, end, (0f, 0x3A), (0.5f, 0x12), (1f, 0x1C)), 1f),
            _ => (Gradient(start, end, (0f, 0x1E), (1f, 0x0C)), 1f),
        };
        _sheenShape.FillBrush = material switch
        {
            IslandMaterial.Acrylic => Gradient(start, end, (0f, 0x0E), (0.4f, 0x00)),
            IslandMaterial.Frosted => Gradient(start, end, (0f, 0x1C), (0.5f, 0x06), (1f, 0x00)),
            // Bright upper lens highlight, clear middle, faint caustic along the bottom edge
            IslandMaterial.LiquidGlass => Gradient(start, end, (0f, 0x2C), (0.16f, 0x0A), (0.55f, 0x00), (0.9f, 0x06), (1f, 0x18)),
            IslandMaterial.AlbumGlow => Gradient(start, end, (0f, 0x14), (0.35f, 0x00)),
            _ => Gradient(start, end, (0f, 0x08), (0.3f, 0x00)),
        };

        Microsoft.UI.Composition.CompositionLinearGradientBrush Gradient(
            System.Numerics.Vector2 from, System.Numerics.Vector2 to, params (float Offset, byte Alpha)[] stops)
        {
            var brush = c.CreateLinearGradientBrush();
            brush.StartPoint = from;
            brush.EndPoint = to;
            foreach (var (offset, alpha) in stops)
                brush.ColorStops.Add(c.CreateColorGradientStop(offset, Color.FromArgb(alpha, 255, 255, 255)));
            return brush;
        }
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 6)
                h = "FF" + h;
            uint v = Convert.ToUInt32(h, 16);
            return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        catch
        {
            return Microsoft.UI.Colors.Black;
        }
    }

    /// <summary>Monochrome grain, generated once; gives glass materials their tactile, non-plastic look.</summary>
    private static WriteableBitmap CreateNoiseTexture(int size)
    {
        var bitmap = new WriteableBitmap(size, size);
        var pixels = new byte[size * size * 4];
        var random = new Random(7);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte v = (byte)random.Next(256);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = 255;
        }
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private void ApplyCollapsedStyle()
    {
        var style = _settings.Current.CollapsedStyle;
        MiniCoverBorder.Visibility = style == CollapsedStyle.LyricOnly ? Visibility.Collapsed : Visibility.Visible;
        MiniVisualizer.Visibility = style == CollapsedStyle.CoverAndLyric && _settings.Current.ShowVisualizer && !LightweightMode ? Visibility.Visible : Visibility.Collapsed;
        MiniControls.Visibility = style == CollapsedStyle.CoverAndControls ? Visibility.Visible : Visibility.Collapsed;
        CollapsedLayer.Padding = style == CollapsedStyle.LyricOnly ? new Thickness(18, 0, 18, 0) : new Thickness(6, 0, 12, 0);
        // Tickers have exact per-line widths; pin them so a wider outgoing line can't re-centre the incoming one.
        TickerA.HorizontalAlignment = TickerB.HorizontalAlignment =
            style == CollapsedStyle.LyricOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;

        // Lyric-only text is centred across the whole pill; otherwise it follows the cover.
        bool lyricOnly = style == CollapsedStyle.LyricOnly;
        foreach (var element in new FrameworkElement[] { TickerHost, VolumeHud })
        {
            Grid.SetColumn(element, lyricOnly ? 0 : 1);
            Grid.SetColumnSpan(element, lyricOnly ? 3 : 2);
            element.HorizontalAlignment = lyricOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }
        if (_shape is not null)
            BindCollapsedGroups();
        UpdateTicker(animate: false, force: true);
        UpdateVisualizer();
    }

    private void ApplyLyricSettings()
    {
        // A user calibration change must be able to move backwards by less than the clock hysteresis.
        if (CurrentCalibration != _lastCalibration || _settings.Current.LyricOffsetMs != _lastGlobalOffset)
        {
            _lastCalibration = CurrentCalibration;
            _lastGlobalOffset = _settings.Current.LyricOffsetMs;
            _lyricIndex = -2;
        }
        var s = _settings.Current;
        LyricsButton.Visibility = s.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
        LyricFlow.SetWordSweep(s.WordSweep);
        if (_disc is not null)
            UpdateVinyl();
        ApplyTickerSweep();

        string chineseFont = s.LyricChineseFontFamily ?? s.LyricFontFamily;
        string englishFont = s.LyricEnglishFontFamily ?? s.LyricFontFamily;
        ApplyLyricForeground();
        // Measurement and every renderer share the same script-aware fonts.
        if (chineseFont != _appliedChineseLyricFont || englishFont != _appliedEnglishLyricFont)
        {
            _appliedChineseLyricFont = chineseFont;
            _appliedEnglishLyricFont = englishFont;
            var chinese = LyricTypography.Family(chineseFont);
            var english = LyricTypography.Family(englishFont);
            LyricsView.SetFonts(chinese, english);
            LyricFlow.SetFonts(chinese, english);
            LyricFlow.SyncSweep(LyricClock, _media.IsPlaying);
            ConfigureFitter();
            PrepareLyricFits();
            UpdateTicker(animate: false, force: true);
        }

        if (s.LyricsEnabled != _lastLyricsEnabled)
        {
            _lastLyricsEnabled = s.LyricsEnabled;
            if (!s.LyricsEnabled && _state == IslandState.Full)
                GoTo(IslandState.Expanded);
            LoadLyrics(_media.Track);
        }
        else if (s.ShowTranslation != _lastShowTranslation)
        {
            _lastShowTranslation = s.ShowTranslation;
            if (!_lyrics.IsEmpty)
            {
                ShowLyricsContent();
                _lyricIndex = -2;
            }
        }
        SyncLyrics();
    }

    // ---------------------------------------------------------------- media events

    private void OnTrackChanged()
    {
        if (_allowClose)
            return;
        var track = _media.Track;
        TitleText.Text = track?.Title ?? "未在播放";
        ArtistText.Text = track is null ? "" : string.IsNullOrEmpty(track.Album) ? track.Artist : $"{track.Artist} — {track.Album}";
        SourceText.Text = SourceLabel();

        _isFavorite = null;
        FavoriteButton.Visibility = track?.IsAppleMusic == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateFavoriteGlyph(pop: false);
        if (track?.IsAppleMusic == true)
            _ = RefreshFavoriteAsync(track);

        LoadLyrics(track);
        UpdateVisibility();
        UpdateAudioActivity(); // the player (and its audio process) may have changed
        UpdateTicker(animate: true, force: true);
        UpdateClock();

        if (track is not null && _gameOnOurDisplay && GamePolicy == GamePresentation.PeekOnly)
            RevealInGame(expand: true); // the only moment the island appears over the game
        else if (track is not null && _settings.Current.PeekOnTrackChange)
            Peek();
    }

    private void OnPlaybackStateChanged()
    {
        if (_allowClose)
            return;
        PlayGlyph.Glyph = MiniPlayGlyph.Glyph = _media.IsPlaying ? PauseGlyphChar : PlayGlyphChar;
        // Freeze or resume the word sweeps exactly where the music is.
        ApplyTickerSweep();
        LyricFlow.SyncSweep(LyricClock, _media.IsPlaying);
        EnsureFrames();
        UpdateSpin();
        if (_media.IsPlaying)
            _ = RefreshVolumeAsync(); // the player may have (re)created its audio session
        if (_media.IsPlaying && _state != IslandState.Collapsed)
            _clockTimer.Start();
        else
            _clockTimer.Stop();
        UpdateVisualizer();
    }

    private void OnTimelineChanged()
    {
        if (_allowClose)
            return;
        UpdateProgress();
        UpdateClock();
        SyncLyrics();
        if (_media.Track is { } track && _lyricLookupTrackKey == track.Key && _media.Duration > TimeSpan.Zero &&
            Math.Abs((_media.Duration - _lyricLookupDuration).TotalSeconds) > 1)
        {
            // Metadata and timeline are separate SMTC events. A new title can arrive with the preceding song's
            // duration; use the corrected recording length once it arrives, even if the first lookup missed.
            LoadLyrics(track);
        }
    }

    private async void OnArtworkChanged()
    {
        if (_allowClose)
            return;
        var art = _media.Artwork;
        ApplyLyricForeground();
        if (art is null)
        {
            Cover.Source = MiniCover.Source = null;
            SetDiscArt(null);
            CoverPlaceholder.Visibility = Visibility.Visible;
            Anim.Fade(BackdropA, 0, 500);
            Anim.Fade(BackdropB, 0, 500);
            AnimateAccent(DefaultAccent);
            return;
        }

        try
        {
            var cover = new BitmapImage { DecodePixelWidth = (int)Math.Ceiling(64 * TotalScale * 1.5) };
            using (var stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(art.Encoded.AsBuffer());
                stream.Seek(0);
                await cover.SetSourceAsync(stream);
            }
            if (_allowClose || !ReferenceEquals(_media.Artwork, art))
                return;

            Cover.Source = MiniCover.Source = cover;
            SetDiscArt(cover);
            CoverPlaceholder.Visibility = Visibility.Collapsed;

            var backdrop = new WriteableBitmap(art.BackdropSize, art.BackdropSize);
            using (var pixels = backdrop.PixelBuffer.AsStream())
                pixels.Write(art.BackdropPixels, 0, art.BackdropPixels.Length);
            backdrop.Invalidate();

            // Cross-fade the glow so colours bleed into each other between songs.
            var incoming = _backdropFlip ? BackdropA : BackdropB;
            var outgoing = _backdropFlip ? BackdropB : BackdropA;
            _backdropFlip = !_backdropFlip;
            incoming.Source = backdrop;
            Anim.Visual(incoming).Opacity = 0;
            Anim.Fade(incoming, 1, 700, easing: Anim.EaseInOut(_compositor));
            Anim.Fade(outgoing, 0, 900, delayMs: 300, easing: Anim.EaseInOut(_compositor));

            AnimateAccent(art.Accent);
        }
        catch (Exception ex)
        {
            Log.Error("Showing artwork failed", ex);
        }
    }

    private void AnimateAccent(Color to)
    {
        var animation = new ColorAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(600),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, _accentBrush);
        Storyboard.SetTargetProperty(animation, "Color");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    // ---------------------------------------------------------------- progress & clock

    private void UpdateProgress()
    {
        var duration = _media.Duration;
        var position = _media.Position;
        double progress = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) : 0;

        // While playing, the shared 30 Hz frame clock keeps it moving (no continuously running animation).
        ProgressBar.SetValue(progress);
        if (_state != IslandState.Collapsed)
            EnsureFrames();
    }

    private void UpdateClock()
    {
        if (ProgressBar.IsDragging)
            return; // the scrub preview owns the labels
        ShowClock(_media.Position, _media.Duration);

        // Lyrics without timestamps scroll with the playback progress (this runs ~1×/s while the card is open).
        if (!_lyrics.IsEmpty && !_lyrics.IsSynced && _media.Duration > TimeSpan.Zero)
            LyricFlow.SetPlainProgress(_media.Position / _media.Duration);
    }

    private void ShowClock(TimeSpan position, TimeSpan duration)
    {
        ElapsedText.Text = Format(position);
        RemainingText.Text = duration > TimeSpan.Zero ? "-" + Format(duration - position) : "";

        static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void OnProgressScrubbing(double value)
    {
        if (_media.Duration > TimeSpan.Zero)
            ShowClock(_media.Duration * value, _media.Duration);
    }

    private void OnProgressCommitted(double value)
    {
        if (_media.Duration > TimeSpan.Zero)
            _ = _media.SeekAsync(_media.Duration * value);
    }

    // ---------------------------------------------------------------- volume

    private float? _pendingVolume;
    private bool _volumeBusy;
    private DispatcherQueueTimer _hudTimer = null!;
    private bool _hudVisible;

    private string? VolumeAppId => _media.SourceAppId;

    /// <summary>Hotkeys / mouse wheel: relative change, with a HUD when collapsed.</summary>
    public async Task ChangeVolumeAsync(float delta)
    {
        var state = await _volume.StepAsync(VolumeAppId, delta);
        Remember(state);
        ShowVolume(state, hud: true);
    }

    public async Task ToggleMuteAsync()
    {
        var state = await _volume.ToggleMuteAsync(VolumeAppId);
        ShowVolume(state, hud: true);
    }

    /// <summary>
    /// Reads the player's level and re-applies the remembered multiplier if the player recreated its audio
    /// session (e.g. after switching output device), since Windows does not persist per-session channel gains.
    /// </summary>
    private async Task RefreshVolumeAsync()
    {
        var state = await _volume.GetAsync(VolumeAppId);
        float wanted = (float)Math.Clamp(_settings.Current.PlayerVolume, 0, 1);
        if (state is { Muted: false } s && Math.Abs(s.Level - wanted) > 0.01f)
            state = await _volume.SetAsync(VolumeAppId, wanted);
        ShowVolume(state, hud: false);
    }

    private void Remember(VolumeService.VolumeState? state)
    {
        if (state is { Muted: false } s && Math.Abs(s.Level - _settings.Current.PlayerVolume) > 0.001)
            _settings.Update(x => x.PlayerVolume = Math.Round(s.Level, 3));
    }

    /// <summary>Dragging fires many values; apply them one at a time and always end on the latest.</summary>
    private async void SetVolumeCoalesced(float level)
    {
        _pendingVolume = level;
        if (_volumeBusy)
            return;
        _volumeBusy = true;
        VolumeService.VolumeState? state = null;
        while (_pendingVolume is { } next)
        {
            _pendingVolume = null;
            state = await _volume.SetAsync(VolumeAppId, next);
        }
        _volumeBusy = false;
        Remember(state);
        if (!VolumeBar.IsDragging)
            ShowVolume(state, hud: false);
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => _ = ToggleMuteAsync();

    private void ShowVolume(VolumeService.VolumeState? state, bool hud)
    {
        if (_allowClose)
            return;
        // No audio session (player closed / not started yet): show it as unavailable and change nothing.
        bool available = state is not null;
        VolumeBar.IsHitTestVisible = available;
        VolumeBar.Opacity = available ? 1 : 0.35;
        if (state is not { } s)
        {
            VolumeText.Text = "—";
            ToolTipService.SetToolTip(VolumeBar, "未检测到播放器音频（开始播放后可调节）");
            return;
        }
        float shown = s.Muted ? 0 : s.Level;
        string glyph = char.ConvertFromUtf32(s.Muted || s.Level <= 0.001f ? 0xE74F : s.Level < 0.34f ? 0xE993 : s.Level < 0.67f ? 0xE994 : 0xE995);
        string percent = s.Muted ? "静音" : $"{Math.Round(s.Level * 100)}";

        if (!VolumeBar.IsDragging)
            VolumeBar.SetValue(shown);
        VolumeGlyph.Glyph = HudGlyph.Glyph = glyph;
        VolumeText.Text = HudText.Text = percent;
        ToolTipService.SetToolTip(VolumeBar, "播放器音量乘数（最终音量 = 播放器内部音量 × 此值），不影响系统音量");

        var fill = Anim.Visual(HudFill);
        fill.CenterPoint = Vector3.Zero;
        var a = _compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, shown, Anim.EaseOut(_compositor));
        a.Duration = TimeSpan.FromMilliseconds(180);
        fill.StartAnimation("Scale.X", a);

        if (hud && _state == IslandState.Collapsed && _isShown)
            ShowVolumeHud();
    }

    private void ShowVolumeHud()
    {
        // The HUD spans the current pill (minus the cover, unless lyric-only).
        double chrome = _settings.Current.CollapsedStyle == CollapsedStyle.LyricOnly ? 36 : 6 + 26 + 10 + 12;
        VolumeHud.Width = Math.Max(90, _collapsedW - chrome);
        if (!_hudVisible)
        {
            _hudVisible = true;
            Anim.Fade(TickerHost, 0, 120);
            Anim.Fade(MiniVisualizer, 0, 120);
            Anim.Fade(MiniControls, 0, 120);
            Anim.Fade(VolumeHud, 1, 200, delayMs: 60);
        }
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void HideVolumeHud()
    {
        if (!_hudVisible)
            return;
        _hudVisible = false;
        Anim.Fade(VolumeHud, 0, 160);
        Anim.Fade(TickerHost, 1, 240, delayMs: 100);
        Anim.Fade(MiniVisualizer, 1, 240, delayMs: 100);
        Anim.Fade(MiniControls, 1, 240, delayMs: 100);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        e.Handled = true;
        _ = ChangeVolumeAsync((float)(delta / 120.0 * _settings.Current.VolumeStep));
    }

    // ---------------------------------------------------------------- lyrics

    private TrackInfo? _calibrationTrack;
    private double _calibrationDuration;
    private string _calibrationKey = "";
    private LyricCalibration CurrentCalibration
    {
        get
        {
            var track = _media.Track;
            if (track is null) return LyricCalibration.Default;
            var duration = _media.Duration > TimeSpan.Zero ? _media.Duration : track.Duration;
            var seconds = Math.Round(duration.TotalSeconds);
            if (!ReferenceEquals(track, _calibrationTrack) || seconds != _calibrationDuration)
            {
                _calibrationTrack = track;
                _calibrationDuration = seconds;
                _calibrationKey = LyricCalibration.Key(track with { Duration = duration });
            }
            return _settings.Current.SongLyricCalibrations.GetValueOrDefault(_calibrationKey) ?? LyricCalibration.Default;
        }
    }
    private LyricCalibration _lastCalibration = LyricCalibration.Default;
    private int _lastGlobalOffset;

    private void SeekLyricLine(LyricLine line) => _ = _media.SeekAsync(
        CurrentCalibration.ToMedia(line.Time, _settings.Current.LyricOffsetMs) + TimeSpan.FromMilliseconds(30));

    private void OnLyricsChanged(TrackInfo? track)
    {
        if (_allowClose) return;
        // Local imports complete on a worker thread; all XAML work stays on the window's dispatcher.
        _dispatcher.TryEnqueue(() =>
        {
            if (!_allowClose && (track is null || _media.Track?.Key == track.Key)) LoadLyrics(_media.Track);
        });
    }

    private async void LoadLyrics(TrackInfo? track)
    {
        if (_allowClose)
            return;
        _lyricsCts?.Cancel();
        _lyricLookupTrackKey = null;
        _lyrics = Lyrics.Empty;
        SourceText.Text = SourceLabel();
        ToolTipService.SetToolTip(SourceText, null);
        _lyricIndex = -2;
        _lyricTimer.Stop();
        _morphTimer.Stop();

        if (track is null || !_settings.Current.LyricsEnabled)
        {
            ShowLyricsMessage(track is null ? "" : "歌词已关闭");
            return;
        }

        var cts = new CancellationTokenSource();
        _lyricsCts = cts;
        ShowLyricsMessage("正在获取歌词…");

        try
        {
            // Debounce rapid skipping, and let the new track's timeline (duration) arrive first.
            await Task.Delay(450, cts.Token);
            var lookup = _media.Duration > TimeSpan.Zero ? track with { Duration = _media.Duration } : track;
            _lyricLookupTrackKey = track.Key;
            _lyricLookupDuration = lookup.Duration;
            Log.Info($"Lyrics lookup: {lookup.Title} · {lookup.Artist} · {lookup.Album} ({lookup.Duration.TotalSeconds:F1}s)");
            var lyrics = await _lyricsService.GetAsync(lookup, cts.Token);
#if DEBUG
            // Test hook: render the lyrics as if they had no timestamps.
            if (Environment.GetEnvironmentVariable("LALALAND_TEST_PLAIN") == "1" && lyrics.IsSynced)
                lyrics = new Lyrics(lyrics.Lines.Select(l => l with { Time = TimeSpan.Zero, Words = null }).ToList(), false, lyrics.Source);
#endif
            if (cts.IsCancellationRequested)
                return;

            _lyrics = lyrics;
            if (lyrics.IsEmpty) ShowLyricsMessage(_lyricsService.LastLookupMessage);
            else ShowLyricsContent();
            PrepareLyricFits();
            SourceText.Text = SourceLabel();
            ToolTipService.SetToolTip(SourceText, lyrics.Match is { } match
                ? $"匹配：{match.Title} · {match.Artist}\n版本时长：{match.Duration:m\\:ss}；播放器：{lookup.Duration:m\\:ss}\n来源：{lyrics.Source}"
                : null);
            SyncLyrics();
            UpdateTicker(animate: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Loading lyrics failed", ex);
            if (!cts.IsCancellationRequested)
                ShowLyricsMessage("歌词获取失败");
        }
        finally
        {
            if (ReferenceEquals(_lyricsCts, cts))
                _lyricsCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Applies the line for the current position and arms a one-shot timer for the next line.
    /// No polling: the UI thread wakes up exactly once per lyric line.
    /// </summary>
    private void SyncLyrics()
    {
        _lyricTimer.Stop();
        _morphTimer.Stop();
        if (!_lyrics.IsSynced)
            return;

        var position = LyricClock;
        int index = _lyrics.IndexAt(position);

        // Hysteresis: a sub-second backward clock correction must not bounce back to the previous line
        // (real seeks move further than this and still go back).
        if (_lyricIndex > 0 && index == _lyricIndex - 1 && _lyricIndex < _lyrics.Lines.Count &&
            position > _lyrics.Lines[_lyricIndex].Time - TimeSpan.FromMilliseconds(800))
            index = _lyricIndex;

        if (index != _lyricIndex)
        {
            _lyricIndex = index;
            if (_lyrics.IsSynced)
                LyricFlow.SetActive(index, LyricClock, _media.IsPlaying);
            EnsureFrames();
            UpdateTicker(animate: true);
        }
        else
        {
            // Same line, but the clock may have jumped (seek, correction): re-sync the word sweeps.
            ApplyTickerSweep();
            LyricFlow.SyncSweep(LyricClock, _media.IsPlaying);
        EnsureFrames();
        }

        if (_media.IsPlaying && index + 1 < _lyrics.Lines.Count)
        {
            var wait = CurrentCalibration.RealDelay(_lyrics.Lines[index + 1].Time - position, _media.PlaybackRate);
            _lyricTimer.Interval = wait < TimeSpan.FromMilliseconds(15) ? TimeSpan.FromMilliseconds(15) : wait;
            _lyricTimer.Start();
            ScheduleMorphFor(index + 1, wait);
        }
    }

    private string TickerTextFor()
    {
        var track = _media.Track;
        if (track is null)
            return "未在播放";
        if (_settings.Current.CollapsedStyle == CollapsedStyle.CoverAndControls)
            return track.Title;
        if (_lyrics.IsSynced && _lyricIndex >= 0)
        {
            return _lyrics.Lines[_lyricIndex].DisplayText;
        }
        return string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Title} · {track.Artist}";
    }

    /// <summary>All lyrics go to the flowing view (synced or not); the classic view only shows status messages.</summary>
    private void ShowLyricsContent()
    {
        bool flow = !_lyrics.IsEmpty;
        LyricFlow.Visibility = flow ? Visibility.Visible : Visibility.Collapsed;
        LyricsView.Visibility = flow ? Visibility.Collapsed : Visibility.Visible;
        if (flow)
        {
            LyricFlow.SetLyrics(_lyrics, _settings.Current.ShowTranslation);
            LyricFlow.SetActive(_lyricIndex, LyricClock, _media.IsPlaying);
            if (!_lyrics.IsSynced && _media.Duration > TimeSpan.Zero)
                LyricFlow.SetPlainProgress(_media.Position / _media.Duration);
        }
        else
        {
            LyricFlow.Clear();
            LyricsView.SetLyrics(_lyrics, _settings.Current.ShowTranslation);
        }
    }

    private void ShowLyricsMessage(string message)
    {
        LyricFlow.Clear();
        LyricFlow.Visibility = Visibility.Collapsed;
        LyricsView.Visibility = Visibility.Visible;
        LyricsView.ShowMessage(message);
    }

    private Brush _tickerBrush = new SolidColorBrush(Microsoft.UI.Colors.White);

    private void ApplyLyricForeground()
    {
        var s = _settings.Current;
        string key = $"{s.LyricColorMode}|{s.LyricFontColor}|{s.LyricGradientStrength}|{s.LyricOpacity}|{_media.Artwork?.Hash}";
        if (key == _appliedLyricColor) return;
        _appliedLyricColor = key;
        _tickerBrush = LyricColors.Create(s.LyricColorMode, s.LyricFontColor, _media.Artwork, s.LyricGradientStrength, s.LyricOpacity);
        LyricsView.SetForeground(_tickerBrush);
        LyricFlow.SetForeground(_tickerBrush);
        TickerA.SetForeground(_tickerBrush);
        TickerB.SetForeground(_tickerBrush);
    }
    private SweepText? _currentTicker;
    private int _tickerLyricIndex = -1;

    /// <summary>End of a line's sweep window: the next line's start (or a few seconds for the last line).</summary>
    private TimeSpan LineEnd(int index) =>
        index + 1 < _lyrics.Lines.Count ? _lyrics.Lines[index + 1].Time : _lyrics.Lines[index].Time + TimeSpan.FromSeconds(5);

    private TimeSpan LyricClock => CurrentCalibration.Apply(_media.Position, _settings.Current.LyricOffsetMs);

    /// <summary>(Re)starts the word sweep on the visible ticker — on line change, play/pause and seeks.</summary>
    private void ApplyTickerSweep()
    {
        if (_currentTicker is null)
            return;
        int index = _tickerLyricIndex;
        if (!_settings.Current.WordSweep || index < 0 || index >= _lyrics.Lines.Count || _lyrics.Lines[index].IsInterlude)
        {
            _currentTicker.SetLitAll(true);
            return;
        }
        var line = _lyrics.Lines[index];
        _currentTicker.StartSweep(line.CharTimes(LineEnd(index)), LyricClock, _media.IsPlaying);
        EnsureFrames();
    }

    private void UpdateTicker(bool animate, bool force = false)
    {
        var text = TickerTextFor();
        if (text == _tickerText && !force)
            return;
        _tickerText = text;

        var incoming = _tickerFlip ? TickerA : TickerB;
        var outgoing = _tickerFlip ? TickerB : TickerA;
        _tickerFlip = !_tickerFlip;

        // Shrink-wrap: the pill takes the size of the new line (a cache lookup — lines were prepared on load).
        var (fit, tokens) = FitWithTokens(text);
        ShapeTicker(incoming, text, fit, tokens);
        _tickerLyricIndex = _lyrics.IsSynced && _lyricIndex >= 0 && Style != CollapsedStyle.CoverAndControls &&
                            text == LineDisplayText(_lyricIndex) ? _lyricIndex : -1;
        _currentTicker = incoming;
        ApplyTickerSweep();
        _morphTimer.Stop();
        _pendingMorph = null;
        ApplyCollapsedFit(fit, animate: animate && _state == IslandState.Collapsed && _isShown);

        if (animate && _state == IslandState.Collapsed && _isShown)
        {
            Anim.Translate(incoming, Vector3.Zero, 420, from: new Vector3(0, 12, 0));
            Anim.Fade(incoming, 1, 320, delayMs: 60);
            Anim.Translate(outgoing, new Vector3(0, -12, 0), 380);
            Anim.Fade(outgoing, 0, 200);
        }
        else
        {
            Anim.SetTranslation(incoming, Vector3.Zero);
            Anim.SetOpacity(incoming, 1);
            Anim.SetOpacity(outgoing, 0);
        }
    }

    private string SourceLabel()
    {
        var app = _media.SourceAppId ?? "";
        string player =
            app.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) ? "Apple Music" :
            app.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ? "Spotify" :
            app.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ? "网易云音乐" :
            app.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) ? "QQ音乐" :
            app.Contains("chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome" :
            app.Contains("msedge", StringComparison.OrdinalIgnoreCase) ? "Edge" :
            app.Length > 0 ? Path.GetFileNameWithoutExtension(app.Split('!')[0].Split('_')[0]) : "";
        if (_lyrics.IsEmpty || string.IsNullOrEmpty(_lyrics.Source))
            return player;
        string timing = _lyrics.HasWordTiming ? "逐字同步" : _lyrics.IsSynced ? "逐行同步" : "无时间轴";
        return $"{player} · {_lyrics.Source}（{timing}）";
    }

    // ---------------------------------------------------------------- favourite

    private async Task RefreshFavoriteAsync(TrackInfo track)
    {
        await Task.Delay(800); // let Apple Music update its own UI for the new track first
        if (_allowClose || _media.Track?.Key != track.Key)
            return;
        var state = await _appleMusic.GetFavoriteStateAsync();
        if (_allowClose || _media.Track?.Key != track.Key || state is null)
            return;
        _isFavorite = state;
        UpdateFavoriteGlyph(pop: false);
    }

    public async Task ToggleFavoriteAsync()
    {
        if (_media.Track?.IsAppleMusic != true)
            return;

        var result = await _appleMusic.ToggleFavoriteAsync();
        if (_allowClose)
            return;
        switch (result)
        {
            case AppleMusicAutomation.ToggleResult.Failed:
                Log.Info("Favourite button not found in Apple Music");
                return;
            case AppleMusicAutomation.ToggleResult.NowFavorite:
                _isFavorite = true;
                break;
            case AppleMusicAutomation.ToggleResult.NowNotFavorite:
                _isFavorite = false;
                break;
            default:
                _isFavorite = !(_isFavorite ?? false);
                break;
        }
        UpdateFavoriteGlyph(pop: true);
        if (_settings.Current.PeekOnTrackChange)
            Peek();
    }

    private void UpdateFavoriteGlyph(bool pop)
    {
        bool fav = _isFavorite == true;
        FavoriteGlyph.Glyph = fav ? HeartFilled : HeartOutline;
        FavoriteGlyph.Foreground = new SolidColorBrush(fav ? FavoriteColor : Microsoft.UI.Colors.White);
        if (pop)
        {
            var v = Anim.Visual(FavoriteGlyph);
            v.CenterPoint = new Vector3((float)FavoriteGlyph.ActualWidth / 2, (float)FavoriteGlyph.ActualHeight / 2, 0);
            var a = _compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(0.35f, new Vector3(1.35f, 1.35f, 1), Anim.EaseOut(_compositor));
            a.InsertKeyFrame(1f, Vector3.One, _expandEasing);
            a.Duration = TimeSpan.FromMilliseconds(480);
            v.StartAnimation("Scale", a);
        }
    }

    // ---------------------------------------------------------------- UI handlers

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = _media.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = _media.NextAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = _media.PreviousAsync();

    private void OnFavoriteClick(object sender, RoutedEventArgs e) => _ = ToggleFavoriteAsync();

    private void OnLyricsClick(object sender, RoutedEventArgs e) => ToggleFullFromCard();

    private void OnOpenPlayerClick(object sender, RoutedEventArgs e)
    {
        var app = _media.SourceAppId;
        if (string.IsNullOrEmpty(app))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening player failed", ex);
        }
    }

    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && e.GetCurrentPoint(NowPlayingCard).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
            return;
        if (!_settings.Current.ClickToShowLyrics || IsInsideButton(e.OriginalSource as DependencyObject))
            return;
        ToggleFullFromCard();
    }

    private bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null && !ReferenceEquals(element, NowPlayingCard))
        {
            if (element is ButtonBase)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
