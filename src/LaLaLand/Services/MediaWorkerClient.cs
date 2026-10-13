using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LaLaLand.Models;

namespace LaLaLand.Services;

internal sealed class MediaWorkerClient(Action<MediaWorkerState> snapshot, Action lost, Func<PlayerSource> source) : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> _pending = new();
    private readonly SemaphoreSlim _write = new(1);
    private readonly CancellationTokenSource _stop = new();
    private Process? _process;
    private int _id;
    public int? ProcessId => _process is { HasExited: false } p ? p.Id : null;

    public async Task StartAsync(bool monitor)
    {
        _process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--media-worker")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        }) ?? throw new InvalidOperationException("无法启动媒体监听进程");
        _ = ReadAsync(_process);
        await SendAsync(monitor ? "monitor" : "ready");
    }
    public async Task<string?> SendAsync(string command, long positionTicks = 0)
    {
        int id = Interlocked.Increment(ref _id);
        var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                if (_process is not { HasExited: false } process) throw new IOException("媒体监听已断开");
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new MediaWorkerRequest(id, command, source(), positionTicks), MediaWorker.Json)).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            finally { _write.Release(); }
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                var message = JsonSerializer.Deserialize<MediaWorkerMessage>(line, MediaWorker.Json);
                if (message is { Kind: "snapshot", State: not null }) snapshot(message.State);
                else if (message is { Kind: "reply" } && _pending.TryGetValue(message.Id, out var reply))
                {
                    if (message.Error is null) reply.TrySetResult(message.Result);
                    else reply.TrySetException(new InvalidOperationException(message.Error));
                }
            }
        }
        catch (Exception ex) { if (!_stop.IsCancellationRequested) Log.Error("Media worker pipe interrupted", ex); }
        finally
        {
            foreach (var reply in _pending.Values) reply.TrySetException(new IOException("媒体监听进程已退出"));
            if (!_stop.IsCancellationRequested) lost();
        }
    }
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try
        {
            if (_process is { HasExited: false } process)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(300)) process.Kill();
            }
        }
        catch (Exception ex) { Log.Error("Stopping media worker failed", ex); }
        _process?.Dispose();
        _process = null;
    }
}
