using System.Diagnostics;
using LaLaLand.Controls;
using Microsoft.UI.Dispatching;

namespace LaLaLand.Views;

/// <summary>
/// One shared clock (20 Hz collapsed, 30 Hz expanded) for everything that changes continuously while music plays:
/// equaliser bars, rim ripples, word sweeps, the spinning
/// record and the progress bar — all updated in the same tick, so they cost one redraw rather than several.
/// <para>
/// WinUI's compositor runs in-process and redraws the whole window at the display refresh rate for as long as any
/// key-frame animation is running. Driving these values from a low-rate clock instead means the window is only
/// redrawn when something actually changed — at most 30 times a second — and not at all once the clock stops.
/// </para>
/// </summary>
public sealed partial class IslandWindow
{
    private const double FrameIntervalMs = 33;
    private const double SpinDegreesPerSecond = 40;

    private DispatcherQueueTimer _frameTimer = null!;
    private long _lastFrameTimestamp;
    private double _spinAngle;

    private void InitFrames()
    {
        _frameTimer = _dispatcher.CreateTimer();
        _frameTimer.Interval = TimeSpan.FromMilliseconds(FrameIntervalMs);
        _frameTimer.IsRepeating = true;
        _frameTimer.Tick += (_, _) => FrameTick();
    }

    /// <summary>Starts the clock if anything needs it; it stops itself when idle.</summary>
    private void EnsureFrames()
    {
        if (_frameTimer is null || _frameTimer.IsRunning || !_media.IsPlaying || !_isShown)
            return;
        // The tiny collapsed pill looks smooth at 20 fps; the full view (spinning record) gets 30.
        _frameTimer.Interval = TimeSpan.FromMilliseconds(_state == IslandState.Collapsed ? 50 : FrameIntervalMs);
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        _frameTimer.Start();
    }

    private void FrameTick()
    {
        double dt = Stopwatch.GetElapsedTime(_lastFrameTimestamp).TotalSeconds;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        if (!_media.IsPlaying || !_isShown)
        {
            _frameTimer.Stop();
            return;
        }

        var now = LyricClock;
        bool busy = false;

        // Live audio: newest analysis frame → equaliser bars (one update per clock tick).
        if (_analyzer.IsRunning)
        {
            ApplyAudioFrame();
            busy = true;
        }

        // Droplet ripples on the rim
        if (_rippleRendering)
        {
            RippleFrame();
            busy |= _rippleRendering;
        }

        // Word sweeps (collapsed ticker, full-view current line)
        if (_currentTicker is { IsSweeping: true } ticker && _state == IslandState.Collapsed)
            busy |= ticker.Tick(now);
        if (_state == IslandState.Full)
            busy |= LyricFlow.TickSweep(now);

        // Spinning record
        if (_vinylOn)
        {
            _spinAngle = (_spinAngle + SpinDegreesPerSecond * Math.Min(dt, 0.1)) % 360;
            Anim.Visual(_discSpinner).RotationAngleInDegrees = (float)_spinAngle;
            busy = true;
        }

        // Progress bar while the card is visible
        if (_state != IslandState.Collapsed && !ProgressBar.IsDragging)
        {
            var duration = _media.Duration;
            if (duration > TimeSpan.Zero)
                ProgressBar.SetValue(Math.Clamp(_media.Position / duration, 0, 1));
            busy = true;
        }

        if (!busy)
            _frameTimer.Stop();
    }
}
