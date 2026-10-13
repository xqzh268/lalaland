using System.Text;
using LaLaLand.Interop;
using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI.Dispatching;

namespace LaLaLand.Views;

/// <summary>
/// Game mode: while a borderless / windowed full-screen app (a game, a full-screen video) covers the island's
/// display, the island removes everything that costs the game frames:
/// <list type="bullet">
/// <item>live background blur (DWM would re-blur the game behind the pill every frame),</item>
/// <item>looping animations (they force a recomposition every refresh),</item>
/// <item>periodic z-order nudges.</item>
/// </list>
/// Any visible overlay still makes DWM compose the game instead of letting it flip independently; the
/// <see cref="GamePresentation.PeekOnly"/> policy avoids that by staying hidden except for brief reveals.
/// </summary>
public sealed partial class IslandWindow
{
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private bool _gameOnOurDisplay;
    private bool _gamePeekActive;
    private DispatcherQueueTimer _gamePeekTimer = null!;
    private long _lastAssertTimestamp;

    private GamePresentation GamePolicy => _settings.Current.GamePresentation;

    /// <summary>True while a full-screen game is on our display and the island should be as cheap as possible.</summary>
    private bool LightweightMode => _gameOnOurDisplay && GamePolicy != GamePresentation.Full;

    /// <summary>True while the game policy wants the island out of the way.</summary>
    private bool HiddenByGame => _gameOnOurDisplay &&
        (GamePolicy == GamePresentation.Hidden || (GamePolicy == GamePresentation.PeekOnly && !_gamePeekActive));

    private void InitGame()
    {
        _gamePeekTimer = CreateTimer(() =>
        {
            _gamePeekActive = false;
            UpdateVisibility();
        });
        _gamePeekTimer.Interval = TimeSpan.FromSeconds(4.5);
    }

    private void UpdateGameState()
    {
        bool game = !_exclusiveOnOurDisplay && IsFullScreenWindowOnOurDisplay();
        if (game == _gameOnOurDisplay)
            return;

        _gameOnOurDisplay = game;
        if (!game)
        {
            _gamePeekActive = false;
            _gamePeekTimer.Stop();
        }
        Log.Info(game ? "Full-screen app on the island's display: game mode on" : "Game mode off");
        ApplyMaterial();
        ApplyCollapsedStyle(); // also hides the equaliser bars in game mode and refits the pill
        UpdateVisibility();
    }

    /// <summary>Foreground window (not ours, not the desktop/taskbar) covering the whole monitor we are on.</summary>
    private bool IsFullScreenWindowOnOurDisplay()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        Native.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == Environment.ProcessId)
            return _gameOnOurDisplay; // our settings window etc. do not change the verdict

        var className = new StringBuilder(64);
        Native.GetClassName(foreground, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString()))
            return false;

        var monitor = Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor != Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST))
            return false;

        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(foreground, out var rect))
            return false;
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    /// <summary>
    /// In <see cref="GamePresentation.PeekOnly"/> mode, briefly shows the island (track change, hotkeys);
    /// otherwise does nothing.
    /// </summary>
    public void RevealInGame(bool expand = false)
    {
        if (!_gameOnOurDisplay || GamePolicy != GamePresentation.PeekOnly)
            return;
        _gamePeekActive = true;
        UpdateVisibility();
        _gamePeekTimer.Stop();
        _gamePeekTimer.Start();
        if (expand)
        {
            GoTo(IslandState.Expanded);
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(PeekDurationMs);
            _collapseTimer.Start();
        }
    }

    /// <summary>Periodic re-assert is skipped in game mode unless a while has passed (z-order changes cost DWM work).</summary>
    private bool PeriodicAssertDue()
    {
        if (!LightweightMode)
            return true;
        return System.Diagnostics.Stopwatch.GetElapsedTime(_lastAssertTimestamp) > TimeSpan.FromSeconds(10);
    }
}
