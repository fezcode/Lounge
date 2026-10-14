namespace Lounge.Core;

/// <summary>Where Lounge keeps its data: %APPDATA%\fezcode\Lounge, like every Fezcode
/// desktop app. LOUNGE_DATA overrides it so tests and previews never touch the real
/// profile. Lounge never reads or writes tivi's folder.</summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("LOUNGE_DATA");
            var root = !string.IsNullOrWhiteSpace(over)
                ? over
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fezcode", "Lounge");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Keymap => Path.Combine(Root, "keymap.json");
    public static string Tiles => Path.Combine(Root, "tiles.json");
    public static string PlayerConfig => Path.Combine(Root, "config.ini");
    public static string History => Path.Combine(Root, "history.ini");
    public static string LibraryDb => Path.Combine(Root, "library.db");
    public static string Thumbs => Dir("thumbs");
    public static string WebView => Dir("webview");
    public static string Logs => Dir("logs");

    private static string Dir(string name)
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }
}
