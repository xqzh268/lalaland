using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI.Xaml;

namespace LaLaLand.Views;

/// <summary>
/// Real-time audio reactivity: equaliser bars, the "droplet on a drumhead" edge motion and the beat that
/// drives the lyric-flow cover. Capture runs only while something visible actually uses it.
/// </summary>
public sealed partial class IslandWindow
{
    private readonly AudioAnalyzer _analyzer = new();
    private AudioFrame _latestFrame;
    private int _framePending;
    private bool _audioLive;          // frames are arriving
    private bool _audioStarting;
    private string? _audioAppId;

    private bool VisualizerWantsAudio => MiniVisualizer.Visibility == Visibility.Visible && _state == IslandState.Collapsed;
    private bool DropletWantsAudio => _settings.Current.DropletEffect;

    private void InitAudio()
    {
        _analyzer.Frame += frame =>
        {
            // Capture thread: just keep the newest frame. It is applied by the shared frame clock, so bars, ripples and
            // sweeps all change in the same compositor frame instead of each triggering their own redraws.
            _latestFrame = frame;
            Interlocked.Exchange(ref _framePending, 1);
        };
        _analyzer.Beat += strength => _dispatcher.TryEnqueue(() => OnBeat(strength));
    }

    /// <summary>Starts or stops capture according to what is visible and enabled. Cheap to call often.</summary>
    private async void UpdateAudioActivity()
    {
        if (_allowClose)
            return;
        bool want = _media.IsPlaying && _isShown && !LightweightMode &&
                    (VisualizerWantsAudio || DropletWantsAudio);
        if (!want)
        {
            if (_analyzer.IsRunning)
            {
                _analyzer.Stop();
                ResetAudioValues();
            }
            return;
        }

        var app = _media.SourceAppId;
        if ((_analyzer.IsRunning && app == _audioAppId) || _audioStarting)
            return;

        // Capture the processes that own the player's audio (Apple Music plays through AMPLibraryAgent.exe).
        _audioStarting = true;
        uint[] pids;
        try
        {
            pids = await _volume.FindPlayerProcessIdsAsync(app);
        }
        finally
        {
            _audioStarting = false;
        }
        if (_allowClose || !_media.IsPlaying || !_isShown || LightweightMode)
            return;
        _audioAppId = app;
        _analyzer.Start(pids.Length > 0 ? pids[0] : 0);
        EnsureFrames();
    }

    private void ResetAudioValues()
    {
        foreach (var name in new[] { "V0", "V1", "V2", "V3" })
            _shape.InsertScalar(name, 0f);
        StopRipples();
        if (_audioLive)
        {
            _audioLive = false;
            UpdateVisualizer();
        }
    }

    /// <summary>Called by the frame clock when a new analysis frame is waiting.</summary>
    private void ApplyAudioFrame()
    {
        if (Interlocked.Exchange(ref _framePending, 0) == 0 || !_analyzer.IsRunning)
            return;
        var f = _latestFrame;
        if (!_audioLive)
        {
            _audioLive = true;
            UpdateVisualizer(); // switch the bars from the fallback loop to the real spectrum
        }

        _shape.InsertScalar("V0", f.Band0);
        _shape.InsertScalar("V1", f.Band1);
        _shape.InsertScalar("V2", f.Band2);
        _shape.InsertScalar("V3", f.Band3);

    }

    /// <summary>A beat: a drop lands on the island'"'"'s rim (see IslandWindow.Ripple) and the flowing cover pulses.</summary>
    private void OnBeat(float strength)
    {
        if (!_analyzer.IsRunning)
            return;
        AddRipple(strength);
    }
}
