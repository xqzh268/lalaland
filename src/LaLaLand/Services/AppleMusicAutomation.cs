using System.Diagnostics;
using Interop.UIAutomationClient;

namespace LaLaLand.Services;

/// <summary>
/// SMTC has no "favourite" command, so we drive Apple Music's own Favourite button through UI Automation.
/// The button lives in the transport bar's "LCD" panel (AutomationId = LCD) and is named "喜爱" / "Favourite".
/// Everything runs on a background MTA thread so the UI thread never blocks on cross-process COM calls.
/// </summary>
public sealed class AppleMusicAutomation
{
    private const int UIA_ProcessIdPropertyId = 30002;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_AutomationIdPropertyId = 30011;
    private const int UIA_ButtonControlTypeId = 50000;
    private const int UIA_InvokePatternId = 10000;
    private const int UIA_RangeValuePatternId = 10003;

    private static readonly string[] FavoriteKeywords = ["喜爱", "喜愛", "收藏", "Favorite", "Favourite", "Love", "お気に入り", "즐겨찾"];
    private static readonly string[] FavoritedHints = ["取消", "已", "移除", "Unfavo", "Remove", "Undo", "Favorited", "Favourited", "Loved"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IUIAutomation? _uia;
    private IUIAutomationElement? _lcd;

    public static bool IsRunning => Process.GetProcessesByName("AppleMusic").Length > 0;

    /// <summary>Reads the current favourite state, or null when it cannot be determined.</summary>
    public Task<bool?> GetFavoriteStateAsync() => RunAsync(() =>
    {
        var button = FindFavoriteButton();
        return button is null ? null : IsFavoritedName(button.CurrentName);
    });

    /// <summary>
    /// Clicks the Favourite button. Returns <see cref="ToggleResult.Failed"/> if the button could not be found,
    /// otherwise the state read back afterwards (or <see cref="ToggleResult.Toggled"/> if it is not observable).
    /// </summary>
    public async Task<ToggleResult> ToggleFavoriteAsync()
    {
        ToggleResult result = ToggleResult.Failed;
        await RunAsync(() =>
        {
            var button = FindFavoriteButton();
            if (button?.GetCurrentPattern(UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
                return null;

            var before = button.CurrentName;
            invoke.Invoke();
            result = ToggleResult.Toggled;

            // Give Apple Music a moment to update the button's accessible name.
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(80);
                var after = button.CurrentName;
                if (after != before)
                {
                    result = IsFavoritedName(after) ? ToggleResult.NowFavorite : ToggleResult.NowNotFavorite;
                    break;
                }
            }
            return null;
        });
        return result;
    }

    public enum ToggleResult { Failed, Toggled, NowFavorite, NowNotFavorite }

    /// <summary>
    /// Seeks by writing Apple Music's own scrubber (AutomationId = LCDScrubber, value in seconds).
    /// Needed because Apple Music reports IsPlaybackPositionEnabled = false over SMTC.
    /// </summary>
    public async Task<bool> SeekAsync(TimeSpan position) => await RunAsync(() =>
    {
        var scrubber = FindLcd()?.FindFirst(TreeScope.TreeScope_Descendants,
            Uia.CreatePropertyCondition(UIA_AutomationIdPropertyId, "LCDScrubber"));
        if (scrubber?.GetCurrentPattern(UIA_RangeValuePatternId) is not IUIAutomationRangeValuePattern range || range.CurrentIsReadOnly != 0)
            return false;
        range.SetValue(Math.Clamp(position.TotalSeconds, range.CurrentMinimum, range.CurrentMaximum));
        return true;
    }) == true;

    private async Task<bool?> RunAsync(Func<bool?> work)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return work();
                }
                catch (Exception ex)
                {
                    // Cached elements go stale when Apple Music restarts or re-templates its UI.
                    _lcd = null;
                    Log.Error("Apple Music automation failed", ex);
                    return null;
                }
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    private IUIAutomationElement? FindFavoriteButton()
    {
        var lcd = FindLcd();
        if (lcd is null)
            return null;

        var buttons = lcd.FindAll(TreeScope.TreeScope_Descendants,
            Uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_ButtonControlTypeId));
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons.GetElement(i);
            var name = b.CurrentName ?? "";
            if (FavoriteKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return b;
        }
        return null;
    }

    private IUIAutomationElement? FindLcd()
    {
        if (_lcd is not null)
        {
            try
            {
                _ = _lcd.CurrentAutomationId; // throws if stale
                return _lcd;
            }
            catch
            {
                _lcd = null;
            }
        }

        foreach (var process in Process.GetProcessesByName("AppleMusic"))
        {
            using (process)
            {
                var window = process.MainWindowHandle != IntPtr.Zero
                    ? Uia.ElementFromHandle(process.MainWindowHandle)
                    : Uia.GetRootElement().FindFirst(TreeScope.TreeScope_Children,
                        Uia.CreatePropertyCondition(UIA_ProcessIdPropertyId, process.Id));
                _lcd = window?.FindFirst(TreeScope.TreeScope_Descendants,
                    Uia.CreatePropertyCondition(UIA_AutomationIdPropertyId, "LCD"));
                if (_lcd is not null)
                    return _lcd;
            }
        }
        return null;
    }

    private IUIAutomation Uia => _uia ??= new CUIAutomation8();

    private static bool IsFavoritedName(string? name) =>
        name is not null && FavoritedHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
}
