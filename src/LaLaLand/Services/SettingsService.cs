using System.Text.Json;
using System.Text.Json.Serialization;
using LaLaLand.Models;
using Microsoft.UI.Dispatching;

namespace LaLaLand.Services;

/// <summary>Loads / persists <see cref="AppSettings"/> as JSON under %AppData%\LaLaLand.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly DispatcherQueueTimer _saveTimer;

    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("LALALAND_PANEL_ACCEPTANCE_DIR") is { Length: > 0 } testDirectory)
            return Path.GetFullPath(testDirectory);
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaLaLand");
    }

    public AppSettings Current { get; }

    /// <summary>Raised on the UI thread after general settings change.</summary>
    public event Action? Changed;
    /// <summary>Foreground-only changes; listeners can update ink without rebuilding layout or playback.</summary>
    public event Action? LyricPaintChanged;
    private readonly bool _persist;

    public SettingsService(DispatcherQueue dispatcher, bool persist = true)
    {
        _persist = persist;
        _path = Path.Combine(DataDirectory, "settings.json");
        Current = Load(_path);

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    /// <summary>Applies a mutation, notifies listeners and schedules a debounced save.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Changed?.Invoke();
        ScheduleSave();
    }

    public void UpdateLyricPaint(LyricColorMode? mode = null, string? color = null,
        double? gradientStrength = null, double? opacity = null)
    {
        var s = Current;
        var nextMode = mode ?? s.LyricColorMode;
        string nextColor = color is null ? s.LyricFontColor : Controls.LyricColors.Hex(Controls.LyricColors.Parse(color));
        double nextStrength = gradientStrength.HasValue ? Controls.LyricColors.Unit(gradientStrength.Value) : s.LyricGradientStrength;
        double nextOpacity = opacity.HasValue ? Controls.LyricColors.Unit(opacity.Value) : s.LyricOpacity;
        if (nextMode == s.LyricColorMode && nextColor == s.LyricFontColor &&
            nextStrength == s.LyricGradientStrength && nextOpacity == s.LyricOpacity) return;
        s.LyricColorMode = nextMode;
        s.LyricFontColor = nextColor;
        s.LyricGradientStrength = nextStrength;
        s.LyricOpacity = nextOpacity;
        LyricPaintChanged?.Invoke();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (!_persist) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, s_json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), s_json);
                if (settings != null)
                {
                    settings.SongLyricCalibrations ??= new();
                    settings.LyricChineseFontFamily ??= settings.LyricFontFamily ?? "";
                    settings.LyricEnglishFontFamily ??= settings.LyricFontFamily ?? "";
                    settings.LyricFontColor = Controls.LyricColors.Hex(Controls.LyricColors.Parse(settings.LyricFontColor));
                    settings.LyricGradientStrength = Controls.LyricColors.Unit(settings.LyricGradientStrength);
                    settings.LyricOpacity = Controls.LyricColors.Unit(settings.LyricOpacity);
                    // Fill in actions added in newer versions
                    foreach (var (action, binding) in AppSettings.DefaultHotkeys())
                        settings.Hotkeys.TryAdd(action, binding);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Loading settings failed, using defaults", ex);
        }
        return new AppSettings();
    }
}
