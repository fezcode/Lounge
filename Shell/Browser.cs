using Lounge.Core.Launch;
using Lounge.Core.Web;
using Lounge.Screens;

namespace Lounge.Shell;

/// <summary>Lounge's browser, for a quick look at anything: an address or a search typed
/// with the controller keyboard opens in the embedded web view. Private windows keep no
/// history, cookies or sign-ins once closed.</summary>
public static class Browser
{
    /// <summary>The browser tile: choose a normal or a private window.</summary>
    public static void Choose() => L.Window.Menu("Browser",
    [
        new MenuEntry("Browse", "web", () => Start(inPrivate: false), "Signed in like your sites"),
        new MenuEntry("Private browsing", "eye-off", () => Start(inPrivate: true), "Nothing is kept after you leave"),
    ]);

    /// <summary>Asks for an address or search, then opens it.</summary>
    public static void Start(bool inPrivate) => Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        L.Window.Keyboard(new KeyboardRequest(inPrivate ? "Address or search (private)" : "Address or search"), text =>
        {
            if (text is not null) Open(WebText.AddressOrSearch(text), inPrivate);
        }), TimeSpan.FromMilliseconds(250));

    public static void Open(string url, bool inPrivate) =>
        L.Window.Push(new WebScreen(new Tile
        {
            Id = "browser", Kind = TileKind.Web, Name = inPrivate ? "Private browsing" : "Browser",
            Target = url, Color = "#2F6FEB", AllowMouse = true,
        }, browser: true, inPrivate));
}
