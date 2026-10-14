using Lounge.Core;

namespace Lounge.Shell;

/// <summary>A small rolling log in %APPDATA%\fezcode\Lounge\logs, one file per day,
/// kept for seven days.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool _pruned;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = AppPaths.Logs;
                if (!_pruned)
                {
                    _pruned = true;
                    foreach (var old in Directory.EnumerateFiles(dir, "lounge-*.log"))
                        if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7)) File.Delete(old);
                }
                File.AppendAllText(Path.Combine(dir, $"lounge-{DateTime.Now:yyyyMMdd}.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Error(string where, Exception e) => Write($"ERROR {where}: {e}");
}
