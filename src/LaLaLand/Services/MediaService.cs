using System.Diagnostics;
using LaLaLand.Models;
using Microsoft.UI.Dispatching;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LaLaLand.Services;

/// <summary>
/// Tracks the relevant media session through Windows' Global System Media Transport Controls (SMTC).
/// Apple Music, Spotify, NetEase Cloud Music, QQ Music and browsers all publish to SMTC.
/// All public events are raised on the UI thread.
/// </summary>
public sealed partial class MediaService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsService _settings;
    private readonly AppleMusicAutomation _appleMusic;
    private readonly DispatcherQueueTimer _metadataDebounce;
    private readonly DispatcherQueueTimer _artworkRecheck;
    private int _artworkRecheckCount;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _metadataVersion;
    private int _monitoringVersion;
    private bool _monitoring;
    private bool _disposed;
    private CancellationTokenSource? _metadataCts;

    private TimeSpan _timelinePosition;
    private long _timelineStamp;
    private double _playbackRate = 1.0;
    private TimeSpan _lastReported = TimeSpan.MinValue;

    public TrackInfo? Track { get; private set; }
    public ArtworkData? Artwork { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool HasSession => _useWorker ? _workerHasSession : _session != null;
    public TimeSpan Duration { get; private set; }
    public double PlaybackRate => _playbackRate;
    public string? SourceAppId => _useWorker ? _workerSource : _session?.SourceAppUserModelId;

    /// <summary>Current playback position, interpolated between SMTC timeline updates.</summary>
    public TimeSpan Position
    {
        get
        {
            var position = _timelinePosition;
            if (IsPlaying)
                position += Stopwatch.GetElapsedTime(_timelineStamp) * _playbackRate;
            if (Duration > TimeSpan.Zero && position > Duration)
                position = Duration;
            return position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }
    }

    public event Action? SessionChanged;
    public event Action? TrackChanged;
    public event Action? ArtworkChanged;
    public event Action? PlaybackStateChanged;
    public event Action? TimelineChanged;

    public MediaService(DispatcherQueue dispatcher, SettingsService settings, AppleMusicAutomation appleMusic)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _appleMusic = appleMusic;
        _selectedFor = settings.Current.Source;
        _settings.Changed += OnSettingsChanged;
        _metadataDebounce = dispatcher.CreateTimer();
        _metadataDebounce.Interval = TimeSpan.FromMilliseconds(120);
        _metadataDebounce.IsRepeating = false;
        _metadataDebounce.Tick += async (_, _) => await RefreshMetadataAsync();

        _artworkRecheck = dispatcher.CreateTimer();
        _artworkRecheck.IsRepeating = false;
        _artworkRecheck.Tick += async (_, _) =>
        {
            if (!_monitoring)
                return;
            // 1.2 s and 3 s after a track change
            if (++_artworkRecheckCount < 2)
            {
                _artworkRecheck.Interval = TimeSpan.FromMilliseconds(1800);
                _artworkRecheck.Start();
            }
            await RefreshMetadataAsync();
        };
    }

    public async Task InitializeAsync()
    {
        if (_disposed || !_settings.Current.IsIslandVisible || _monitoring)
            return;
        _monitoring = true;
        int version = ++_monitoringVersion;
        try
        {
            if (_useWorker)
            {
                await StartWorkerAsync();
                return;
            }
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!_monitoring || version != _monitoringVersion)
                return;
            _manager = manager;
            _manager.SessionsChanged += OnSessionsChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            SelectSession();
        }
        catch (Exception ex)
        {
            if (version == _monitoringVersion)
            {
                if (_useWorker) RestartWorker();
                else _monitoring = false;
            }
            Log.Error("Media session manager unavailable", ex);
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() => SelectSession(force: true));

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() => SelectSession());

    private void SuspendMonitoring()
    {
        _monitoring = false;
        ++_monitoringVersion;
        ++_metadataVersion;
        _metadataCts?.Cancel();
        _metadataDebounce.Stop();
        _artworkRecheck.Stop();
        _worker?.Dispose();
        _worker = null;
        _workerHasSession = false;
        _workerSource = null;
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }
        Detach();
        IsPlaying = false;
        Duration = TimeSpan.Zero;
        _timelinePosition = TimeSpan.Zero;
        _lastReported = TimeSpan.MinValue;
        SetTrack(null);
        SetArtwork(null);
    }

    // ---------------------------------------------------------------- session selection

    private PlayerSource _selectedFor;

    /// <summary>Only the player-source setting affects session selection; ignore every other change.</summary>
    private void OnSettingsChanged()
    {
        if (!_settings.Current.IsIslandVisible)
        {
            if (_monitoring)
                SuspendMonitoring();
            return;
        }
        if (!_monitoring)
        {
            _selectedFor = _settings.Current.Source;
            _ = InitializeAsync();
            return;
        }
        if (_settings.Current.Source == _selectedFor)
            return;
        _selectedFor = _settings.Current.Source;
        if (_useWorker) _ = WorkerCommandAsync("source");
        else SelectSession();
    }

    /// <param name="force">Re-attach even if the same app is picked (its session object may have been recreated).</param>
    private void SelectSession(bool force = false)
    {
        if (!_monitoring || _manager is null)
            return;

        GlobalSystemMediaTransportControlsSession? next;
        try
        {
            next = PickSession(_manager);
        }
        catch (Exception ex)
        {
            Log.Error("Enumerating media sessions failed", ex);
            return;
        }

        if (!force && next is not null && _session is not null && next.SourceAppUserModelId == _session.SourceAppUserModelId)
            return;
        if (next is null && _session is null)
            return;

        Detach();
        _session = next;
        Log.Info($"Media session -> {_session?.SourceAppUserModelId ?? "(none)"}");

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        SessionChanged?.Invoke();
        UpdatePlaybackInfo();
        UpdateTimeline();
        _ = RefreshMetadataAsync();
    }

    private GlobalSystemMediaTransportControlsSession? PickSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();
        var apple = sessions.FirstOrDefault(s => IsPlatform(s, PlayerSource.AppleMusicOnly));
        var current = manager.GetCurrentSession() ?? sessions.FirstOrDefault();

        return _settings.Current.Source switch
        {
            PlayerSource.AppleMusicOnly => apple,
            PlayerSource.SpotifyOnly => PickPlatformSession(sessions, PlayerSource.SpotifyOnly),
            PlayerSource.NeteaseOnly => PickPlatformSession(sessions, PlayerSource.NeteaseOnly),
            PlayerSource.QqMusicOnly => PickPlatformSession(sessions, PlayerSource.QqMusicOnly),
            PlayerSource.KugouOnly => PickPlatformSession(sessions, PlayerSource.KugouOnly),
            PlayerSource.SystemCurrent => current,
            _ when apple is not null && IsSessionPlaying(apple) => apple,
            _ when current is not null && IsSessionPlaying(current) => current,
            _ => apple ?? current,
        };
    }

    private static GlobalSystemMediaTransportControlsSession? PickPlatformSession(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions, PlayerSource source) =>
        sessions.FirstOrDefault(s => IsPlatform(s, source) && IsSessionPlaying(s)) ??
        sessions.FirstOrDefault(s => IsPlatform(s, source));

    /// <summary>
    /// SMTC exposes package AUMIDs for Store apps and process names for desktop apps.
    /// Keep matching on stable vendor tokens so desktop and Store editions share one setting.
    /// </summary>
    private static bool IsPlatform(GlobalSystemMediaTransportControlsSession session, PlayerSource source)
    {
        var id = session.SourceAppUserModelId;
        return source switch
        {
            PlayerSource.AppleMusicOnly => ContainsAny(id, "applemusic", "apple.music"),
            PlayerSource.SpotifyOnly => ContainsAny(id, "spotify"),
            PlayerSource.NeteaseOnly => ContainsAny(id, "netease", "cloudmusic", "163music", "music.163"),
            PlayerSource.QqMusicOnly => ContainsAny(id, "qqmusic", "qq.music", "tencentmusic", "tencent.qqmusic"),
            PlayerSource.KugouOnly => ContainsAny(id, "kugou", "酷狗"),
            _ => false,
        };
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession session) =>
        session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private void Detach()
    {
        if (_session is null)
            return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    // ---------------------------------------------------------------- SMTC callbacks (thread pool)

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (!_monitoring)
                return;
            // SMTC fires several times per track change (text first, artwork later); coalesce them.
            _metadataDebounce.Stop();
            _metadataDebounce.Start();
        });

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (!_monitoring)
                return;
            // Only re-pick on a real play/pause transition: re-enumerating every session on each of the many
            // playback events raced with other apps tearing down their sessions (native crashes in MediaControl.dll).
            if (UpdatePlaybackInfo() && _settings.Current.Source == PlayerSource.PreferAppleMusic)
                SelectSession();
        });

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _dispatcher.TryEnqueue(UpdateTimeline);

    // ---------------------------------------------------------------- state updates (UI thread)

    /// <returns>True if the play state or rate changed.</returns>
    private bool UpdatePlaybackInfo()
    {
        if (_useWorker) return false;
        if (!_monitoring)
            return false;
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info;
        try
        {
            info = _session?.GetPlaybackInfo();
        }
        catch (Exception ex)
        {
            Log.Error("Reading playback info failed (session gone?)", ex);
            return false;
        }
        var playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var rate = info?.PlaybackRate ?? 1.0;

        if (playing != IsPlaying || Math.Abs(rate - _playbackRate) > 0.001)
        {
            // Re-base the interpolation clock so the position does not jump when toggling.
            _timelinePosition = Position;
            _timelineStamp = Stopwatch.GetTimestamp();
            IsPlaying = playing;
            _playbackRate = rate > 0 ? rate : 1.0;
            PlaybackStateChanged?.Invoke();
            TimelineChanged?.Invoke();
            return true;
        }
        return false;
    }

    private void UpdateTimeline()
    {
        if (_useWorker) return;
        if (!_monitoring)
            return;
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline;
        try
        {
            timeline = _session?.GetTimelineProperties();
        }
        catch (Exception ex)
        {
            Log.Error("Reading timeline failed (session gone?)", ex);
            return;
        }
        if (timeline is null)
        {
            _timelinePosition = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
        }
        else
        {
            var position = timeline.Position;
            // The reported position is a snapshot taken at LastUpdatedTime; age it if that looks sane.
            var age = DateTimeOffset.Now - timeline.LastUpdatedTime;
            if (IsPlaying && age > TimeSpan.Zero && age < TimeSpan.FromSeconds(10))
                position += age * _playbackRate;

            var reported = position - timeline.StartTime;
            var duration = timeline.EndTime - timeline.StartTime;
            // Compare the raw value: the aged one differs by a few ms on every report.
            bool stepped = timeline.Position != _lastReported;
            _lastReported = timeline.Position;

            // Players may report coarse positions — Apple Music sends whole seconds a few times per second. Taking
            // each report literally drags the clock back and forth (lyrics flip between lines, the progress bar
            // jitters). Instead the report bounds the true position: it lies in [reported, reported + 1 s), and
            // right after the reported value changes it has only just crossed that second. Keep our smooth clock
            // while it is inside the bounds; nudge it only when it falls outside. Big gaps are real seeks.
            var predicted = Position;
            if (IsPlaying && duration == Duration && (reported - predicted).Duration() < TimeSpan.FromSeconds(2))
            {
                var upper = reported + TimeSpan.FromMilliseconds(stepped ? 350 : 1050);
                var corrected = predicted < reported ? reported : predicted > upper ? upper : predicted;
                if (corrected == predicted)
                    return; // consistent with our clock: nothing to do, no event

                _timelinePosition = corrected;
            }
            else
            {
                _timelinePosition = reported;
            }
            Duration = duration;
        }

        _timelineStamp = Stopwatch.GetTimestamp();
        TimelineChanged?.Invoke();
    }

    private async Task RefreshMetadataAsync()
    {
        if (_useWorker) return;
        if (!_monitoring)
            return;
        var session = _session;
        int version = ++_metadataVersion;
        _metadataCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _metadataCts = cts;
        try
        {
            if (session is null)
            {
                SetTrack(null);
                SetArtwork(null);
                return;
            }

            GlobalSystemMediaTransportControlsSessionMediaProperties? props;
            try
            {
                props = await session.TryGetMediaPropertiesAsync().AsTask(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Reading media properties failed", ex);
                return;
            }

            if (version != _metadataVersion || props is null)
                return;

            var title = props.Title?.Trim() ?? "";
            var artist = props.Artist?.Trim() ?? "";
            var album = props.AlbumTitle?.Trim() ?? "";

            // Apple Music for Windows publishes "Artist — Album" in the artist field and leaves the album empty.
            if (string.IsNullOrEmpty(album) && session.SourceAppUserModelId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
            {
                int split = artist.IndexOf(" — ", StringComparison.Ordinal);
                if (split > 0)
                {
                    album = artist[(split + 3)..].Trim();
                    artist = artist[..split].Trim();
                }
            }

            if (string.IsNullOrEmpty(title))
            {
                SetTrack(null);
                SetArtwork(null);
                return;
            }

            var track = new TrackInfo(title, artist, album, Duration, session.SourceAppUserModelId);
            bool changed = Track?.Key != track.Key;
            SetTrack(track);

            if (changed)
            {
                // Players often publish the new title first and the new artwork later — sometimes without another
                // event. Re-check a couple of times; unchanged artwork is skipped cheaply by its content hash.
                _artworkRecheckCount = 0;
                _artworkRecheck.Interval = TimeSpan.FromMilliseconds(1200);
                _artworkRecheck.Start();
            }

            // Always look at the thumbnail: comparing its content (not the track) catches late artwork updates.
            var encoded = await ArtworkProcessor.TryReadAsync(props.Thumbnail, cts.Token);
            if (version != _metadataVersion)
                return;
            if (encoded is null)
            {
                if (changed)
                    SetArtwork(null);
                return;
            }

            var hash = ArtworkProcessor.HashOf(encoded);
            if (hash == Artwork?.Hash)
                return;
            var artwork = await ArtworkProcessor.TryProcessAsync(encoded, hash, cts.Token);
            if (version != _metadataVersion || artwork is null)
                return;
            SetArtwork(artwork);
        }
        finally
        {
            if (ReferenceEquals(_metadataCts, cts))
                _metadataCts = null;
        }
    }

    private void SetTrack(TrackInfo? track)
    {
        if (Track?.Key == track?.Key)
        {
            Track = track;
            return;
        }
        Track = track;
        TrackChanged?.Invoke();
    }

    private void SetArtwork(ArtworkData? artwork)
    {
        if (ReferenceEquals(Artwork, artwork))
            return;
        Artwork = artwork;
        ArtworkChanged?.Invoke();
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Hidden mode owns no live session or subscriptions: resolve a player only for this command.</summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> GetCommandSessionAsync()
    {
        if (_disposed)
            return null;
        if (_monitoring && _session is not null)
            return _session;
        try
        {
            var manager = _manager ?? await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _disposed ? null : PickSession(manager);
        }
        catch (Exception ex)
        {
            Log.Error("Finding the player for a hotkey failed", ex);
            return null;
        }
    }

    public async Task<string?> GetControlSourceAppIdAsync() => _useWorker
        ? await WorkerCommandAsync("control-source") : (await GetCommandSessionAsync())?.SourceAppUserModelId;

    public async Task TogglePlayPauseAsync()
    {
        if (_useWorker) { await WorkerCommandAsync("play-pause"); return; }
        var session = await GetCommandSessionAsync();
        if (session is null)
            return;

        // Optimistic UI update; the real state arrives through PlaybackInfoChanged.
        if (_monitoring && ReferenceEquals(session, _session))
        {
            _timelinePosition = Position;
            _timelineStamp = Stopwatch.GetTimestamp();
            IsPlaying = !IsPlaying;
            PlaybackStateChanged?.Invoke();
            TimelineChanged?.Invoke();
        }

        await Try(() => session.TryTogglePlayPauseAsync().AsTask());
    }

    public async Task NextAsync()
    {
        if (_useWorker) { await WorkerCommandAsync("next"); return; }
        var session = await GetCommandSessionAsync();
        await Try(() => session?.TrySkipNextAsync().AsTask());
    }

    public async Task PreviousAsync()
    {
        if (_useWorker) { await WorkerCommandAsync("previous"); return; }
        var session = await GetCommandSessionAsync();
        await Try(() => session?.TrySkipPreviousAsync().AsTask());
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_useWorker)
        {
            position = position < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && position > Duration ? Duration : position;
            _timelinePosition = position;
            _timelineStamp = Stopwatch.GetTimestamp();
            TimelineChanged?.Invoke();
            await WorkerCommandAsync("seek", position.Ticks);
            return;
        }
        var session = _session;
        if (session is null)
            return;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration)
            position = Duration;

        // Optimistic: move the UI right away; the next timeline update confirms or corrects it.
        _timelinePosition = position;
        _timelineStamp = Stopwatch.GetTimestamp();
        TimelineChanged?.Invoke();

        bool done = false;
        try
        {
            if (session.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled == true)
                done = await session.TryChangePlaybackPositionAsync(position.Ticks);

            // Apple Music refuses SMTC seeking; drive its own scrubber instead.
            if (!done && session.SourceAppUserModelId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
                done = await _appleMusic.SeekAsync(position);
        }
        catch (Exception ex)
        {
            Log.Error("Seeking failed", ex);
        }

        if (!done)
        {
            Log.Info($"Seeking is not supported by {session.SourceAppUserModelId}");
            UpdateTimeline(); // snap back to the real position
        }
    }

    private static async Task Try(Func<Task<bool>?> action)
    {
        try
        {
            if (action() is { } task)
                await task;
        }
        catch (Exception ex)
        {
            Log.Error("Media command failed", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        SuspendMonitoring();
    }
}
