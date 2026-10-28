using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace LaLaLand.Services;

/// <summary>
/// SMTC has no "like" command. Apple Music keeps its dedicated automation; the other desktop players (Spotify, NetEase
/// Cloud Music, QQ Music, Kugou) are driven through the like/heart button of their own window with UI Automation.
/// <para>
/// None of these players document their UI, so the lookup is deliberately forgiving: it scores every small control in
/// the now-playing bar by name, help text, id and class, retries while a Chromium/CEF window is still building its
/// accessibility tree, briefly reveals a window that was hidden to the tray, and as a last resort clicks the control
/// itself. When nothing matches, the visible candidates are written to the log so the profile can be tuned.
/// </para>
/// </summary>
public sealed class FavoriteService(AppleMusicAutomation appleMusic)
{
    private sealed record Profile(string[] Processes, string[] Keywords, string[] Favorited);

    private static readonly string[] Excluded = ["playlist", "歌单", "download", "下载", "radio", "电台", "comment", "评论", "share", "分享"];
    private static readonly string[] Hearts = ["♥", "♡", "❤", "🤍", "💗", "heart", "红心", "爱心"];

    private static readonly string[] CommonKeywords = ["喜欢", "喜爱", "收藏", "like", "love", "favorite", "favourite", "save to", "liked songs", "your library", "保存到"];
    private static readonly string[] CommonFavorited = ["取消", "已喜欢", "已收藏", "已保存", "移除", "remove", "unlike", "unfavorite", "unfavourite", "unsave", "saved to"];

    private static readonly Dictionary<string, Profile> Profiles = new()
    {
        ["spotify"] = new(["Spotify"], CommonKeywords, CommonFavorited),
        ["netease"] = new(["cloudmusic"], CommonKeywords, CommonFavorited),
        ["qqmusic"] = new(["QQMusic"], CommonKeywords, CommonFavorited),
        ["kugou"] = new(["KuGou", "KuGouMusic", "KGMusic"], CommonKeywords, CommonFavorited),
    };

    private const int UIA_Invoke = 10000, UIA_Toggle = 10015, UIA_Legacy = 10018;
    private const int P_Rect = 30001, P_Type = 30003, P_Name = 30005, P_Enabled = 30010, P_Id = 30011, P_Class = 30012, P_Help = 30013, P_Pid = 30002;
    private static readonly int[] ControlTypes = [50000, 50002, 50005, 50006, 50025, 50026, 50011, 50007, 50031];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IUIAutomation? _uia;
    private IUIAutomation Uia => _uia ??= new CUIAutomation8();

    private static string? Key(string? appId) =>
        string.IsNullOrEmpty(appId) ? null :
        Has(appId, "applemusic") ? "apple" :
        Has(appId, "spotify") ? "spotify" :
        Has(appId, "cloudmusic") || Has(appId, "netease") ? "netease" :
        Has(appId, "qqmusic") ? "qqmusic" :
        Has(appId, "kugou") || Has(appId, "kgmusic") ? "kugou" : null;

    private static bool Has(string value, string token) => value.Contains(token, StringComparison.OrdinalIgnoreCase);

    public static bool Supports(string? appId) => Key(appId) is not null;

    public async Task<bool?> GetStateAsync(string? appId)
    {
        var key = Key(appId);
        if (key == "apple") return await appleMusic.GetFavoriteStateAsync();
        if (key is null) return null;
        var profile = Profiles[key];
        bool? state = null;
        await RunAsync(() =>
        {
            // A read must never pop windows up: only look at what is already reachable.
            using var found = Find(profile, reveal: false, attempts: 2);
            if (found is not null) state = ReadState(found.Element, profile);
        });
        return state;
    }

    public async Task<AppleMusicAutomation.ToggleResult> ToggleAsync(string? appId)
    {
        var key = Key(appId);
        if (key == "apple") return await appleMusic.ToggleFavoriteAsync();
        if (key is null) return AppleMusicAutomation.ToggleResult.Failed;
        var profile = Profiles[key];
        var result = AppleMusicAutomation.ToggleResult.Failed;
        await RunAsync(() =>
        {
            using var found = Find(profile, reveal: true, attempts: 4);
            if (found is null)
            {
                LogCandidates(profile);
                return;
            }
            var button = found.Element;
            bool? before = ReadState(button, profile);
            var beforeName = SafeName(button);
            if (!Activate(button, found)) return;
            result = AppleMusicAutomation.ToggleResult.Toggled;
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(100);
                var after = ReadState(button, profile);
                if (after is not null && after != before)
                {
                    result = after == true ? AppleMusicAutomation.ToggleResult.NowFavorite : AppleMusicAutomation.ToggleResult.NowNotFavorite;
                    break;
                }
                if (SafeName(button) != beforeName && after is not null)
                    break;
            }
            // Re-read through a fresh lookup: web UIs often replace the button element after a click.
            if (result == AppleMusicAutomation.ToggleResult.Toggled)
            {
                using var again = Find(profile, reveal: false, attempts: 1);
                if (again is not null && ReadState(again.Element, profile) is { } now && before is not null && now != before)
                    result = now ? AppleMusicAutomation.ToggleResult.NowFavorite : AppleMusicAutomation.ToggleResult.NowNotFavorite;
            }
        });
        return result;
    }

    private async Task RunAsync(Action work)
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex) { Log.Error("Player favourite automation failed", ex); }
            });
        }
        finally { _gate.Release(); }
    }

    private static string SafeName(IUIAutomationElement element)
    {
        try { return element.CurrentName ?? ""; } catch { return ""; }
    }

    /// <summary>Toggle state when the control exposes one, otherwise the wording of its name.</summary>
    private static bool? ReadState(IUIAutomationElement element, Profile profile)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_Toggle) is IUIAutomationTogglePattern toggle)
                return toggle.CurrentToggleState == ToggleState.ToggleState_On;
        }
        catch { /* stale element or unsupported pattern: fall back to the name */ }
        try
        {
            var text = (element.CurrentName ?? "") + " " + (element.CurrentHelpText ?? "");
            if (profile.Favorited.Any(h => Has(text, h))) return true;
            if (profile.Keywords.Any(k => Has(text, k)) || Hearts.Any(h => Has(text, h))) return false;
        }
        catch { }
        return null;
    }

    private sealed class Found(IUIAutomationElement element, IReadOnlyList<IntPtr> hidden, tagRECT rect, int processId) : IDisposable
    {
        public IUIAutomationElement Element { get; } = element;
        public tagRECT Rect { get; } = rect;
        public int ProcessId { get; } = processId;
        /// <summary>A window this lookup showed on purpose; hidden again when the lookup is finished.</summary>
        public IReadOnlyList<IntPtr> Revealed { get; } = hidden;
        public void Dispose()
        {
            foreach (var window in Revealed) Win.ShowWindow(window, Win.SW_HIDE);
        }
    }

    private Found? Find(Profile profile, bool reveal, int attempts)
    {
        var revealed = new List<IntPtr>();
        try
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) Thread.Sleep(350); // Chromium builds its accessibility tree after the first UIA request
                // Second pass: wake windows that are hidden to the tray (they have no accessibility tree while hidden).
                if (attempt == 1 && reveal)
                    foreach (var window in HiddenWindows(profile)) { Win.ShowWindow(window, Win.SW_SHOWMINNOACTIVE); revealed.Add(window); Thread.Sleep(250); }
                if (Search(profile, attempt >= 2 ? 0.45 : 0.18) is { } hit)
                {
                    // Keep the revealed window alive until the click and read-back are done.
                    return new Found(hit.Element, revealed, hit.Rect, hit.ProcessId);
                }
            }
        }
        catch (Exception ex) { Log.Error("Favourite lookup failed", ex); }
        foreach (var window in revealed) Win.ShowWindow(window, Win.SW_HIDE);
        return null;
    }

    private IEnumerable<IntPtr> HiddenWindows(Profile profile)
    {
        var ids = ProcessIds(profile).ToHashSet();
        var result = new List<IntPtr>();
        Win.EnumWindows((h, _) =>
        {
            Win.GetWindowThreadProcessId(h, out var pid);
            if (ids.Contains((int)pid) && !Win.IsWindowVisible(h) && Win.GetWindow(h, Win.GW_OWNER) == IntPtr.Zero &&
                Win.GetWindowRect(h, out var r) && r.right - r.left > 300 && r.bottom - r.top > 200)
                result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IEnumerable<int> ProcessIds(Profile profile)
    {
        foreach (var name in profile.Processes)
            foreach (var process in Process.GetProcessesByName(name))
                using (process) yield return process.Id;
    }

    private (IUIAutomationElement Element, tagRECT Rect, int ProcessId)? Search(Profile profile, double bottomFraction)
    {
        var request = Uia.CreateCacheRequest();
        foreach (var id in new[] { P_Rect, P_Type, P_Name, P_Enabled, P_Id, P_Class, P_Help }) request.AddProperty(id);
        IUIAutomationCondition type = Uia.CreatePropertyCondition(P_Type, ControlTypes[0]);
        foreach (var t in ControlTypes.Skip(1)) type = Uia.CreateOrCondition(type, Uia.CreatePropertyCondition(P_Type, t));

        (IUIAutomationElement Element, tagRECT Rect, int ProcessId, double Score)? best = null;
        foreach (var pid in ProcessIds(profile))
        {
            var windows = Uia.GetRootElement().FindAll(TreeScope.TreeScope_Children, Uia.CreatePropertyCondition(P_Pid, pid));
            for (int w = 0; w < windows.Length; w++)
            {
                var window = windows.GetElement(w);
                var area = window.CurrentBoundingRectangle;
                int height = area.bottom - area.top;
                if (height < 200) continue;
                var elements = window.FindAllBuildCache(TreeScope.TreeScope_Descendants, type, request);
                for (int i = 0; i < elements.Length; i++)
                {
                    var element = elements.GetElement(i);
                    var rect = element.CachedBoundingRectangle;
                    int width = rect.right - rect.left, h = rect.bottom - rect.top;
                    if (width <= 0 || h <= 0 || width > 140 || h > 140 || element.CachedIsEnabled == 0) continue;
                    if (rect.top < area.bottom - height * bottomFraction) continue; // the now-playing bar is at the bottom
                    var label = element.CachedName ?? "";
                    var text = $"{label} {element.CachedHelpText} {element.CachedAutomationId} {element.CachedClassName}";
                    if (Excluded.Any(x => Has(text, x))) continue;
                    double score = 0;
                    if (profile.Keywords.Any(k => Has(text, k))) score += 4;
                    if (Hearts.Any(x => Has(text, x))) score += 3;
                    if (score == 0) continue;
                    if (profile.Keywords.Any(k => Has(label, k))) score += 1;
                    int controlType = element.CachedControlType;
                    if (controlType is 50000 or 50002) score += 1;
                    if (label.Length is > 0 and <= 40) score += 0.5;
                    if (rect.top > area.bottom - height * 0.18) score += 1;
                    if (best is null || score > best.Value.Score || score == best.Value.Score && rect.top > best.Value.Rect.top)
                        best = (element, rect, pid, score);
                }
            }
        }
        return best is { } b ? (b.Element, b.Rect, b.ProcessId) : null;
    }

    private static bool Activate(IUIAutomationElement element, Found found)
    {
        try
        {
            if (element.GetCurrentPattern(UIA_Toggle) is IUIAutomationTogglePattern toggle) { toggle.Toggle(); return true; }
        }
        catch { }
        try
        {
            if (element.GetCurrentPattern(UIA_Invoke) is IUIAutomationInvokePattern invoke) { invoke.Invoke(); return true; }
        }
        catch { }
        try
        {
            if (element.GetCurrentPattern(UIA_Legacy) is IUIAutomationLegacyIAccessiblePattern legacy) { legacy.DoDefaultAction(); return true; }
        }
        catch { }
        return ClickAt(found);
    }

    /// <summary>
    /// Last resort for controls without any action pattern: a real click on the control's centre. Only when that point
    /// really belongs to the player (nothing covers it), and the cursor is put back afterwards.
    /// </summary>
    private static bool ClickAt(Found found)
    {
        int x = (found.Rect.left + found.Rect.right) / 2, y = (found.Rect.top + found.Rect.bottom) / 2;
        var target = Win.WindowFromPoint(new Win.POINT { x = x, y = y });
        if (target == IntPtr.Zero) return false;
        Win.GetWindowThreadProcessId(target, out var pid);
        if ((int)pid != found.ProcessId) return false;
        Win.GetCursorPos(out var origin);
        Win.SetCursorPos(x, y);
        Win.mouse_event(Win.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Win.mouse_event(Win.MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        Win.SetCursorPos(origin.x, origin.y);
        return true;
    }

    /// <summary>Writes what the now-playing bar exposes, so a profile miss can be diagnosed from the log alone.</summary>
    private void LogCandidates(Profile profile)
    {
        try
        {
            var names = new List<string>();
            foreach (var pid in ProcessIds(profile))
            {
                var windows = Uia.GetRootElement().FindAll(TreeScope.TreeScope_Children, Uia.CreatePropertyCondition(P_Pid, pid));
                for (int w = 0; w < windows.Length && names.Count < 40; w++)
                {
                    var window = windows.GetElement(w);
                    var area = window.CurrentBoundingRectangle;
                    var all = window.FindAll(TreeScope.TreeScope_Descendants, Uia.CreatePropertyCondition(P_Type, 50000));
                    for (int i = 0; i < all.Length && names.Count < 40; i++)
                    {
                        var e = all.GetElement(i);
                        var r = e.CurrentBoundingRectangle;
                        if (r.top >= area.bottom - (area.bottom - area.top) * 0.3)
                            names.Add($"\"{e.CurrentName}\"#{e.CurrentAutomationId}");
                    }
                }
            }
            Log.Info("Favourite button not found; bottom-bar buttons: " + (names.Count == 0 ? "(none exposed)" : string.Join(", ", names)));
        }
        catch (Exception ex) { Log.Error("Listing favourite candidates failed", ex); }
    }

    private static class Win
    {
        public const int SW_HIDE = 0, SW_SHOWMINNOACTIVE = 7, GW_OWNER = 4;
        public const uint MOUSEEVENTF_LEFTDOWN = 2, MOUSEEVENTF_LEFTUP = 4;
        public struct POINT { public int x, y; }
        public struct RECT { public int left, top, right, bottom; }
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    }
}
