using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Lounge.Media;

/// <summary>Loads FFmpeg 8 (the DLL set tivi bundles) from the ffmpeg folder beside the
/// executable and offers a few helpers. Every other media class calls <see cref="Ensure"/>.</summary>
public static unsafe class Ff
{
    private static readonly object Gate = new();
    private static bool _tried;

    public static bool Available { get; private set; }
    public static string? Error { get; private set; }
    public static string Folder { get; private set; } = "";

    /// <summary>Where to look, in order: LOUNGE_FFMPEG, ./ffmpeg beside the app, the app folder.</summary>
    public static IEnumerable<string> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("LOUNGE_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return AppContext.BaseDirectory;
    }

    public static bool Ensure()
    {
        lock (Gate)
        {
            if (_tried) return Available;
            _tried = true;
            var dir = Candidates().FirstOrDefault(d => File.Exists(Path.Combine(d, "avcodec-62.dll")));
            if (dir is null)
            {
                Error = "FFmpeg was not found beside Lounge (ffmpeg\\avcodec-62.dll).";
                return false;
            }
            try
            {
                Folder = dir;
                // The FFmpeg DLLs depend on each other and on ~100 mingw libraries in the
                // same folder; LoadLibrary only finds those through the DLL search path.
                SetDllDirectory(dir);
                ffmpeg.RootPath = dir;
                DynamicallyLoadedBindings.Initialize();
                var v = ffmpeg.avcodec_version();
                Available = v >> 16 == 62;
                if (!Available) Error = $"Unexpected FFmpeg version {v >> 16} (need avcodec 62).";
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            }
            catch (Exception e)
            {
                Error = "FFmpeg could not be loaded: " + e.Message;
                Available = false;
            }
            return Available;
        }
    }

    public static string Describe(int error)
    {
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(error, buf, 256);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"error {error}";
    }

    public static void Check(int rc, string what)
    {
        if (rc < 0) throw new MediaException($"{what}: {Describe(rc)}");
    }

    public static double ToSeconds(long ts, AVRational tb) =>
        ts == ffmpeg.AV_NOPTS_VALUE ? double.NaN : ts * ffmpeg.av_q2d(tb);

    public static long FromSeconds(double seconds, AVRational tb) =>
        (long)Math.Round(seconds / ffmpeg.av_q2d(tb));

    public static string? Tag(AVDictionary* dict, string key)
    {
        if (dict == null) return null;
        // Flags 0: whole key, case-insensitive.
        var e = ffmpeg.av_dict_get(dict, key, null, 0);
        if (e == null) return null;
        var v = Marshal.PtrToStringUTF8((IntPtr)e->value);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
}

public sealed class MediaException(string message) : Exception(message);
