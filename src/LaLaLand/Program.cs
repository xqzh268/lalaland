using System.Diagnostics;
using LaLaLand.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LaLaLand;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so the watchdog mode can run without starting WinUI.
/// </summary>
public static class Program
{
    public const string WatchdogArgument = "--watchdog";
    internal static bool IsMediaWorker { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // Installer/updater hooks (--veloapp-*) run and exit here; a normal start continues.
        Velopack.VelopackApp.Build().Run();
        if (args.Length == 2 && args[0] == WatchdogArgument && int.TryParse(args[1], out int pid))
            return Watchdog.Run(pid);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length == 1 && args[0] == "--media-worker")
        {
            IsMediaWorker = true;
            return MediaWorker.RunAsync().GetAwaiter().GetResult();
        }
        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}

/// <summary>
/// Tiny supervisor process: waits for the main instance and relaunches it after a crash. Native faults inside
/// system components (e.g. Windows.Media.MediaControl.dll) cannot be caught in-process, and Windows Error
/// Reporting restarts are not reliable on every machine. Normal exits and user kills are respected.
/// </summary>
internal static class Watchdog
{
    private const int MinUptimeSeconds = 30;
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);
    private static readonly string HistoryPath = Path.Combine(SettingsService.DataDirectory, "restarts.txt");

    public static int Run(int pid)
    {
        try
        {
            return Supervise(pid);
        }
        catch (Exception ex)
        {
            Log.Error("Watchdog failed", ex);
            return 0;
        }
    }

    private static int Supervise(int pid)
    {
        // Open our own handle up-front and hold it: Process.ExitCode on a process we did not start re-opens the
        // process after it exited, which can fail once the system has reclaimed it.
        IntPtr handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return 0;

        DateTime started;
        try
        {
            using var process = Process.GetProcessById(pid);
            started = process.StartTime;
        }
        catch
        {
            started = DateTime.Now;
        }

        int code;
        try
        {
            WaitForSingleObject(handle, INFINITE);
            if (!GetExitCodeProcess(handle, out uint exitCode))
                return 0;
            code = unchecked((int)exitCode);
        }
        finally
        {
            CloseHandle(handle);
        }

        if (!IsCrash(code))
        {
            Log.Info($"Main process {pid} exited normally (code 0x{code:X8}); watchdog done");
            return 0;
        }

        var uptime = DateTime.Now - started;
        Log.Info($"Main process {pid} crashed (exit code 0x{code:X8}) after {uptime.TotalMinutes:F1} min");
        if (uptime.TotalSeconds < MinUptimeSeconds || !RecordRestart())
        {
            Log.Info("Not restarting: crashing too often");
            return 0;
        }

        Thread.Sleep(2000);
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        Log.Info("Restarted after crash");
        return 0;
    }

    private const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, INFINITE = 0xFFFFFFFF;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>NTSTATUS failures (access violation, stack overflow, …) and .NET fatal errors; not normal exits or kills.</summary>
    private static bool IsCrash(int code)
    {
        uint c = unchecked((uint)code);
        // 0xFFFFFFFF (TerminateProcess(-1), e.g. Stop-Process) and 0xC000013A (Ctrl+C / logoff) are deliberate exits.
        bool ntError = c >= 0xC0000000 && c < 0xD0000000 && c != 0xC000013A;
        return ntError || c == 0x80131623 /* COR_E_FAILFAST */ || c == 0xE0434352 /* CLR exception */ || c == 0xE0434F4D;
    }

    /// <summary>Keeps a short restart history; refuses once the window already holds <see cref="MaxRestarts"/> restarts.</summary>
    private static bool RecordRestart()
    {
        try
        {
            var now = DateTime.UtcNow;
            var recent = File.Exists(HistoryPath)
                ? File.ReadAllLines(HistoryPath)
                    .Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue)
                    .Where(t => now - t < RestartWindow)
                    .ToList()
                : [];
            if (recent.Count >= MaxRestarts)
                return false;
            recent.Add(now);
            Directory.CreateDirectory(SettingsService.DataDirectory);
            File.WriteAllLines(HistoryPath, recent.Select(t => t.ToString("O")));
            return true;
        }
        catch
        {
            return true;
        }
    }
}
