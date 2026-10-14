using System.Text;

namespace Lounge.Core;

/// <summary>Atomic text writes: a crash mid-write leaves the old file, never half a
/// new one. Corrupt files are set aside rather than deleted.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Renames a file that failed to parse to <c>name.corrupt-yyyyMMddHHmmss</c>
    /// and returns the new path, or null when it could not be moved.</summary>
    public static string? SetAside(string path)
    {
        try
        {
            var dest = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, dest, overwrite: true);
            return dest;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
