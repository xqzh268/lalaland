using System.Diagnostics;
namespace LaLaLand.Services;
public sealed partial class MediaService
{
    private readonly bool _useWorker = !Program.IsMediaWorker;
    private MediaWorkerClient? _worker;
    private bool _workerHasSession;
    private string? _workerSource;
    private long _workerStarted;
    private int _workerRestarts;
    internal int? WorkerProcessId => _worker?.ProcessId;

    private async Task StartWorkerAsync()
    {
        int version = _monitoringVersion;
        _workerStarted = Stopwatch.GetTimestamp();
        var client = new MediaWorkerClient(
            state => _dispatcher.TryEnqueue(() => { if (_monitoring && version == _monitoringVersion) ApplyWorkerState(state); }),
            () => _dispatcher.TryEnqueue(() => { if (_monitoring && version == _monitoringVersion) RestartWorker(); }),
            () => _settings.Current.Source);
        _worker = client;
        await client.StartAsync(monitor: true);
    }
    private void ApplyWorkerState(MediaWorkerState state)
    {
        bool playbackChanged = IsPlaying != state.IsPlaying || Math.Abs(_playbackRate - state.Rate) > 0.001;
        bool sessionChanged = _workerHasSession != state.HasSession || _workerSource != state.SourceAppId;
        _workerHasSession = state.HasSession;
        _workerSource = state.SourceAppId;
        IsPlaying = state.IsPlaying;
        _playbackRate = state.Rate > 0 ? state.Rate : 1;
        Duration = TimeSpan.FromTicks(state.DurationTicks);
        _timelinePosition = TimeSpan.FromTicks(state.PositionTicks);
        // QueryPerformanceCounter is shared across processes: include pipe/dispatcher delay in interpolation.
        _timelineStamp = state.PositionStamp;
        SetTrack(state.Track);
        if (state.ArtworkChanged) SetArtwork(state.Artwork);
        if (sessionChanged) SessionChanged?.Invoke();
        if (playbackChanged) PlaybackStateChanged?.Invoke();
        TimelineChanged?.Invoke();
    }
    private async void RestartWorker()
    {
        _worker?.Dispose();
        _worker = null;
        _workerHasSession = false;
        if (Stopwatch.GetElapsedTime(_workerStarted) > TimeSpan.FromSeconds(30)) _workerRestarts = 0;
        if (++_workerRestarts > 3)
        {
            _monitoring = false;
            Log.Error("Media worker repeatedly failed; monitoring stopped");
            return;
        }
        Log.Info("Media worker exited; UI remains active, reconnecting");
        int version = ++_monitoringVersion;
        await Task.Delay(500 * _workerRestarts);
        if (_disposed || !_monitoring || version != _monitoringVersion) return;
        try { await StartWorkerAsync(); }
        catch (Exception ex)
        {
            Log.Error("Restarting media worker failed", ex);
            if (_monitoring && version == _monitoringVersion) RestartWorker();
        }
    }
    private async Task<string?> WorkerCommandAsync(string command, long positionTicks = 0)
    {
        try
        {
            if (_worker is { } client) return await client.SendAsync(command, positionTicks);
            using var temporary = new MediaWorkerClient(_ => { }, () => { }, () => _settings.Current.Source);
            await temporary.StartAsync(monitor: false);
            return await temporary.SendAsync(command, positionTicks);
        }
        catch (Exception ex) { Log.Error("Media worker command failed", ex); return null; }
    }
#if DEBUG
    internal async Task InjectNativeFailureForTestAsync()
    {
        if (_worker is not null)
        {
            try { await _worker.SendAsync("test-native-crash"); }
            catch (IOException) { }
        }
    }
#endif
}
