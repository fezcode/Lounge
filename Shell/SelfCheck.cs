using System.Text.Json;

namespace Lounge.Shell;

/// <summary>Development self-checks that need a real window (the OpenGL video path cannot
/// render headlessly). Enabled with LOUNGE_SELFCHECK=&lt;folder&gt;: Lounge writes what it saw
/// there and exits. Never active in normal use.</summary>
public static class SelfCheck
{
    public static string? Folder => Environment.GetEnvironmentVariable("LOUNGE_SELFCHECK");
    public static bool Active => !string.IsNullOrEmpty(Folder);

    public static void Report(string name, object data)
    {
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string PathFor(string file)
    {
        Directory.CreateDirectory(Folder ?? ".");
        return System.IO.Path.Combine(Folder ?? ".", file);
    }
}
