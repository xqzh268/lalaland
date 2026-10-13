using System.Diagnostics;

namespace LaLaLand.Services;

/// <summary>Minimal file logger (%AppData%\LaLaLand\log.txt, truncated at 512 KB).</summary>
public static class Log
{
    private static readonly object s_lock = new();
    private static readonly string s_path = Path.Combine(SettingsService.DataDirectory, "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(SettingsService.DataDirectory);
                var info = new FileInfo(s_path);
                if (info.Exists && info.Length > 512 * 1024)
                    info.Delete();
                File.AppendAllText(s_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the app down
        }
    }
}
