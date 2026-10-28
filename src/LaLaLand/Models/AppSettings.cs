namespace LaLaLand.Models;

public enum IslandPosition
{
    TopCenter,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum CollapsedStyle
{
    /// <summary>Cover thumbnail + single-line lyric.</summary>
    CoverAndLyric,
    /// <summary>Single-line lyric only.</summary>
    LyricOnly,
    /// <summary>Cover thumbnail + previous / play-pause / next.</summary>
    CoverAndControls,
}

public enum IslandMaterial
{
    /// <summary>System-blurred desktop + dark tint + fine noise (Windows Acrylic recipe).</summary>
    Acrylic,
    /// <summary>System-blurred desktop + milky white frost + visible grain.</summary>
    Frosted,
    /// <summary>Barely tinted blur with specular rim and lens-like highlights (inspired by Apple's Liquid Glass).</summary>
    LiquidGlass,
    /// <summary>Opaque solid colour, like the hardware Dynamic Island.</summary>
    Solid,
    /// <summary>Blurred, colour-rich backdrop derived from the album artwork.</summary>
    AlbumGlow,
}

public enum PlayerSource
{
    /// <summary>Prefer Apple Music, fall back to whatever Windows reports as the current media session.</summary>
    PreferAppleMusic,
    /// <summary>Follow the system's current media session.</summary>
    SystemCurrent,
    /// <summary>Only ever show Apple Music.</summary>
    AppleMusicOnly,
    /// <summary>Only ever show a Spotify media session.</summary>
    SpotifyOnly,
    /// <summary>Only ever show a NetEase Cloud Music media session.</summary>
    NeteaseOnly,
    /// <summary>Only ever show a QQ Music media session.</summary>
    QqMusicOnly,
    /// <summary>Only ever show a Kugou Music media session.</summary>
    KugouOnly,
}

public enum GamePresentation
{
    /// <summary>Visible, but with every continuous cost removed (no live blur, no looping animation).</summary>
    Optimized,
    /// <summary>Hidden; appears for a few seconds on track changes and hotkeys (game keeps independent flip).</summary>
    PeekOnly,
    /// <summary>Hidden while a full-screen game runs on the island's display.</summary>
    Hidden,
    /// <summary>No changes (live blur and animations stay on).</summary>
    Full,
}

public enum HotkeyAction
{
    PlayPause,
    Next,
    Previous,
    ToggleFavorite,
    ToggleLyrics,
    ToggleIsland,
    VolumeUp,
    VolumeDown,
    ToggleMute,
}

public enum LyricColorMode { Album, Custom }

public sealed record HotkeyBinding(uint Modifiers, uint Key)
{
    public override string ToString() => HotkeyFormatter.Format(this);
}

public sealed class AppSettings
{
    // Placement
    public IslandPosition Position { get; set; } = IslandPosition.TopCenter;
    /// <summary>Index into the display list; -1 means the primary display.</summary>
    public int DisplayIndex { get; set; } = -1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; } = 10;

    // Appearance
    public CollapsedStyle CollapsedStyle { get; set; } = CollapsedStyle.CoverAndLyric;
    public IslandMaterial Material { get; set; } = IslandMaterial.Acrylic;
    /// <summary>Colour used by the solid material (#AARRGGBB).</summary>
    public string SolidColor { get; set; } = "#FF000000";
    /// <summary>Corner radius of the expanded island in DIPs; the collapsed pill uses min(radius, height / 2).</summary>
    public double CornerRadius { get; set; } = 30;
    /// <summary>How much translucent materials are dimmed for legibility (0 – 0.85).</summary>
    public double BackdropDim { get; set; } = 0.35;
    /// <summary>Show the equaliser bars (driven by the real audio) in the collapsed island.</summary>
    public bool ShowVisualizer { get; set; } = true;
    /// <summary>Island edges quiver like a water droplet on a drumhead, kicked by the beat.</summary>
    public bool DropletEffect { get; set; } = true;
    /// <summary>Full lyrics view: lyrics flow around the cover, which pulses with the beat.</summary>
    public bool LyricFlowAroundCover { get; set; } = true;
    /// <summary>Word-by-word highlight sweep on the current line.</summary>
    public bool WordSweep { get; set; } = true;
    /// <summary>User zoom factor applied on top of DPI scaling (0.8 – 1.5).</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>Collapsed width; with <see cref="FitCollapsedToLyrics"/> it is the maximum width.</summary>
    public double CollapsedWidth { get; set; } = 360;
    /// <summary>Collapsed island shrink-wraps each lyric line (long lines wrap into a two-line pill).</summary>
    public bool FitCollapsedToLyrics { get; set; } = true;
    /// <summary>Let over-long lines wrap into a two-line pill; otherwise they stay on one line with an ellipsis.</summary>
    public bool CollapsedTwoLineLyrics { get; set; } = true;
    /// <summary>Width of the expanded card and lyrics in DIPs (340 – 600).</summary>
    public double ExpandedWidth { get; set; } = 400;
    /// <summary>Legacy font setting, retained to migrate older settings files.</summary>
    public string LyricFontFamily { get; set; } = "";
    /// <summary>Chinese, Japanese and Korean lyric font; null inherits the legacy font, empty = system default.</summary>
    public string? LyricChineseFontFamily { get; set; }
    /// <summary>Latin and other non-CJK lyric font; null inherits the legacy font, empty = system default.</summary>
    public string? LyricEnglishFontFamily { get; set; }
    /// <summary>Lyric foreground in #RRGGBB format; shared opacity is configured separately.</summary>
    public string LyricFontColor { get; set; } = "#FFFFFF";
    public LyricColorMode LyricColorMode { get; set; } = LyricColorMode.Album;
    /// <summary>0 = one averaged cover color, 1 = the full cover gradient.</summary>
    public double LyricGradientStrength { get; set; } = 1;
    /// <summary>Shared foreground opacity, multiplied by karaoke and line emphasis.</summary>
    public double LyricOpacity { get; set; } = 1;

    // Behaviour
    public int HoverExpandDelayMs { get; set; } = 120;
    public int CollapseDelayMs { get; set; } = 450;
    public bool PeekOnTrackChange { get; set; }
    public bool HideWhenIdle { get; set; } = true;
    public bool ClickToShowLyrics { get; set; } = true;
    public bool TopmostGuard { get; set; } = true;
    /// <summary>Hide (and stop touching the z-order) while an exclusive full-screen app runs on the same display.</summary>
    public bool YieldToExclusiveFullscreen { get; set; } = true;
    /// <summary>How the island behaves over a borderless / windowed full-screen game on its display.</summary>
    public GamePresentation GamePresentation { get; set; } = GamePresentation.Optimized;
    /// <summary>False destroys the panel and suspends monitoring; media hotkeys query the player on demand.</summary>
    public bool IsIslandVisible { get; set; } = true;

    /// <summary>Check GitHub Releases for a newer version in the background (installed copies only).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    // Source
    public PlayerSource Source { get; set; } = PlayerSource.PreferAppleMusic;

    // Volume
    /// <summary>Player volume multiplier (0 – 1) applied to the player's audio sessions; never the system volume.</summary>
    public double PlayerVolume { get; set; } = 1.0;
    /// <summary>Step for hotkeys / one mouse-wheel notch (0.01 – 0.2).</summary>
    public double VolumeStep { get; set; } = 0.05;

    // Lyrics
    public bool LyricsEnabled { get; set; } = true;
    public bool ShowTranslation { get; set; } = true;
    public int LyricOffsetMs { get; set; }
    public Dictionary<string, LyricCalibration> SongLyricCalibrations { get; set; } = new();

    public Dictionary<HotkeyAction, HotkeyBinding?> Hotkeys { get; set; } = DefaultHotkeys();

    public static Dictionary<HotkeyAction, HotkeyBinding?> DefaultHotkeys()
    {
        const uint ctrlAlt = Interop.Native.MOD_CONTROL | Interop.Native.MOD_ALT;
        return new()
        {
            [HotkeyAction.PlayPause] = new(ctrlAlt, 0x20),      // Space
            [HotkeyAction.Next] = new(ctrlAlt, 0x27),           // Right
            [HotkeyAction.Previous] = new(ctrlAlt, 0x25),       // Left
            [HotkeyAction.ToggleFavorite] = new(ctrlAlt, 0x46), // F
            [HotkeyAction.ToggleLyrics] = new(ctrlAlt, 0x4C),   // L
            [HotkeyAction.ToggleIsland] = new(ctrlAlt, 0x48),   // H
            [HotkeyAction.VolumeUp] = new(ctrlAlt, 0x26),       // Up
            [HotkeyAction.VolumeDown] = new(ctrlAlt, 0x28),     // Down
            [HotkeyAction.ToggleMute] = new(ctrlAlt, 0x4D),     // M
        };
    }
}
