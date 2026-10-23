using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Lemonize.Core;

/// <summary>
/// Lightweight diagnostic log (%LOCALAPPDATA%\Lemonize\diag.log). Writes happen on a background
/// thread so callers on time-critical threads (the mouse hook) never block on disk I/O.
/// </summary>
public static class Diag
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static readonly string FilePath = Path.Combine(AppPaths.LocalData, "diag.log");

    static Diag()
    {
        new Thread(Writer) { IsBackground = true, Name = "Lemonize diag log" }.Start();
    }

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,2}] {message}";
        Queue.TryAdd(line);
    }

    /// <summary>Blocks briefly until queued lines are on disk (used right before exiting).</summary>
    public static void Flush()
    {
        var deadline = Stopwatch.StartNew();
        while (Queue.Count > 0 && deadline.ElapsedMilliseconds < 500) Thread.Sleep(10);
    }

    private static void Writer()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Diagnostics must never disturb the app.
            }
        }
    }
}
