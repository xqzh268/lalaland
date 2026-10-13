using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using LaLaLand.Models;
using Microsoft.UI.Dispatching;

namespace LaLaLand.Services;

internal sealed record MediaWorkerRequest(int Id, string Command, PlayerSource Source, long PositionTicks = 0);
internal sealed record MediaWorkerState(TrackInfo? Track, bool HasSession, bool IsPlaying, double Rate,
    long PositionTicks, long PositionStamp, long DurationTicks, string? SourceAppId, bool ArtworkChanged, ArtworkData? Artwork);
internal sealed record MediaWorkerMessage(string Kind, int Id = 0, MediaWorkerState? State = null, string? Result = null, string? Error = null);

// Native SMTC failures stay in this disposable process. The UI receives only managed snapshots.
internal static class MediaWorker
{
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public static async Task<int> RunAsync()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var queue = controller.DispatcherQueue;
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        MediaService? service = null;
        SettingsService? settings = null;
        string? lastArtwork = null;
        bool artworkInitialized = false;
        void Write(MediaWorkerMessage message) => output.WriteLine(JsonSerializer.Serialize(message, Json));
        void Snapshot()
        {
            if (service is null) return;
            bool changed = !artworkInitialized || lastArtwork != service.Artwork?.Hash;
            artworkInitialized = true;
            lastArtwork = service.Artwork?.Hash;
            Write(new("snapshot", State: new(service.Track, service.HasSession, service.IsPlaying, service.PlaybackRate,
                service.Position.Ticks, Stopwatch.GetTimestamp(), service.Duration.Ticks, service.SourceAppId, changed, changed ? service.Artwork : null)));
        }
        Task Dispatch(Func<Task> action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                _ = ExecuteAsync();
                async Task ExecuteAsync()
                {
                    try { await action(); done.SetResult(); }
                    catch (Exception ex) { done.SetException(ex); }
                }
            })) done.SetException(new InvalidOperationException("Media dispatcher stopped."));
            return done.Task;
        }
        try
        {
            await Dispatch(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                settings = new SettingsService(queue, persist: false);
                settings.Current.IsIslandVisible = false;
                service = new MediaService(queue, settings, new AppleMusicAutomation());
                service.TrackChanged += Snapshot;
                service.ArtworkChanged += Snapshot;
                service.PlaybackStateChanged += Snapshot;
                service.TimelineChanged += Snapshot;
                service.SessionChanged += Snapshot;
                return Task.CompletedTask;
            });
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var request = JsonSerializer.Deserialize<MediaWorkerRequest>(line, Json);
                if (request is null || request.Command == "stop") break;
                await Dispatch(async () =>
                {
                    try
                    {
                        if (settings!.Current.Source != request.Source)
                            settings.Update(s => s.Source = request.Source);
                        string? result = null;
                        switch (request.Command)
                        {
                            case "monitor": settings.Current.IsIslandVisible = true; await service!.InitializeAsync(); Snapshot(); break;
                            case "ready": break;
                            case "source": Snapshot(); break;
                            case "control-source": result = await service!.GetControlSourceAppIdAsync(); break;
                            case "play-pause": await service!.TogglePlayPauseAsync(); break;
                            case "next": await service!.NextAsync(); break;
                            case "previous": await service!.PreviousAsync(); break;
                            case "seek": await service!.SeekAsync(TimeSpan.FromTicks(request.PositionTicks)); break;
#if DEBUG
                            case "test-native-crash": ExitProcess(0xC0000374); break;
#endif
                            default: throw new InvalidOperationException("Unknown media command.");
                        }
                        Write(new("reply", request.Id, Result: result));
                    }
                    catch (Exception ex) { Write(new("reply", request.Id, Error: ex.Message)); }
                });
            }
            await Dispatch(() =>
            {
                if (service is not null)
                {
                    service.TrackChanged -= Snapshot;
                    service.ArtworkChanged -= Snapshot;
                    service.PlaybackStateChanged -= Snapshot;
                    service.TimelineChanged -= Snapshot;
                    service.SessionChanged -= Snapshot;
                    service.Dispose();
                }
                return Task.CompletedTask;
            });
            return 0;
        }
        catch (Exception ex) { Log.Error("Media worker failed", ex); return 1; }
        finally { await controller.ShutdownQueueAsync(); }
    }
#if DEBUG
    [DllImport("kernel32.dll")]
    private static extern void ExitProcess(uint code);
#endif
}
