using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace LaLaLand.Services;

/// <summary>
/// Hands memory that is no longer touched back to the OS once the app is idle (after start-up and after the island
/// has been collapsed for a while). Pages are faulted back in transparently if they are needed again.
/// </summary>
public sealed class MemoryTrimmer
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    private readonly DispatcherQueueTimer _timer;

    public MemoryTrimmer(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Trim();
    }

    /// <summary>Schedules a trim after <paramref name="delay"/>; re-scheduling postpones it.</summary>
    public void Schedule(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    public void Cancel() => _timer.Stop();

    private static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
    }
}
