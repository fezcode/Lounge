using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Library;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Media;
using Lounge.Platform;
using Lounge.Screens;
using Lounge.Shell;

namespace Lounge.Preview;

/// <summary>Renders every screen in every theme to PNG with the production controls,
/// headlessly: nothing appears on the desktop and nothing makes a sound. Sample posters,
/// albums and history are generated so the screens look lived in.</summary>
internal static class Program
{
    private static string _out = "dist/preview";
    private static MainWindow _window = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        _out = Path.GetFullPath(args.FirstOrDefault() ?? "dist/preview");
        Directory.CreateDirectory(_out);
        var data = Path.Combine(Path.GetTempPath(), "lounge-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("LOUNGE_DATA", data);

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        Motion.Enabled = false;
        L.Preview = true;
        try
        {
            Services(data);
            var sample = Seed(data);
            _window = new MainWindow { Width = 1920, Height = 1080 };
            L.Window = _window;
            _window.ApplyTheme(rebuild: false);
            _window.Push(new HomeScreen());
            _window.Show();

            if (args.Contains("--debug-card"))
            {
                var poster = L.Library.VideoCollections()[0].Art!;
                var e = new HomeEntry("Debug", "sub", poster, "video", "#0A84FF", () => { });
                var inside = new PosterCard(400, 225, "Inside", "caption", (w, h) => HomeModel.Art(e, w, h), captionInside: true);
                var below = new PosterCard(400, 225, "Below", "caption", (w, h) => HomeModel.Art(e, w, h));
                var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 40, Margin = new Thickness(100), Children = { inside, below } };
                _window.Content = host;
                Pump(60);
                Shot("debug-card");
                return 0;
            }
            if (args.Contains("--debug-home"))
            {
                L.Settings.Set(K.Theme, "livingroom");
                _window.ApplyTheme(rebuild: true);
                Pump(40);
                Shot("debug-home-short");
                Pump(150);
                Shot("debug-home-long");
                return 0;
            }

            foreach (var theme in Core.Themes.Themes.All)
            {
                L.Settings.Set(K.Theme, theme.Id);
                _window.ApplyTheme(rebuild: true);
                var t = theme.Id;
                Shot($"{t}-home");
                _window.OpenSettings("Appearance");
                Shot($"{t}-settings");
                _window.CloseOverlays();
                Pump(10);

                Show(new VideosScreen(), $"{t}-videos");
                Show(new CollectionScreen(L.Library.VideoCollections()[0]), $"{t}-collection");
                Show(new MusicScreen(), $"{t}-music");
                Show(new AlbumScreen(L.Library.Albums()[0]), $"{t}-album");
                Show(new AppsScreen(), $"{t}-apps");
                Show(new RemoteScreen(), $"{t}-remote");

                var files = new FilesScreen(sample);
                _window.Push(files);
                Pump(10);
                files.Handle(new ActionEvent(Act.Right, ActionPhase.Pressed));
                files.Handle(new ActionEvent(Act.Down, ActionPhase.Pressed));
                Shot($"{t}-files");
                _window.Keyboard(new KeyboardRequest("Rename file", "The Expanse S01E02", ".mkv", Suggestions: ["The Expanse S01E02", "The Expanse"]), _ => { });
                Shot($"{t}-keyboard");
                _window.CloseOverlays();
                Pump(10);
                _window.Menu("Options",
                [
                    new MenuEntry("Open", "play", null), new MenuEntry("Rename", "pencil", null), new MenuEntry("Copy", "copy", null),
                    new MenuEntry("Add to Videos", "library-add", null, "Shows"), new MenuEntry("Delete", "trash", null, Danger: true),
                ]);
                Shot($"{t}-menu");
                _window.CloseOverlays();
                Pump(10);
                _window.Ask("Move to the Recycle Bin?", "You can restore it from the Recycle Bin in Windows.", ["Recycle", "Cancel"], 1, _ => { }, "trash");
                Shot($"{t}-dialog");
                _window.CloseOverlays();
                _window.Toast("Renamed to The Expanse S01E02.mkv", "pencil");
                Shot($"{t}-toast");
                _window.GoHome();
                Pump(10);
            }

            RenderIcons();
            Console.WriteLine($"Previews written to {_out}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Preview FAILED: {e}");
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Services(string data)
    {
        L.Settings = SettingsStore.OpenDefault();
        L.Settings.Set(K.PlayerVolume, 0.0);
        L.Settings.Set(K.Ambient, false);
        L.Settings.Set(K.Sounds, false);
        L.Keymap = Keymap.Defaults();
        L.Pad = new XInputPad();
        L.Router = new ActionRouter(L.Keymap);
        L.Tiles = new TileStore(AppPaths.Tiles);
        L.History = new WatchHistory(AppPaths.History);
        L.Library = new LibraryDb(AppPaths.LibraryDb);
        L.Player = new MediaPlayer();
        L.Volume = new SystemVolume();
    }

    private static void Show(Lounge.Shell.Screen s, string name)
    {
        _window.Push(s);
        Pump(12);
        Shot(name);
        _window.Pop();
        Pump(6);
    }

    private static void Pump(int rounds = 45)
    {
        for (int i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
    }

    private static void Shot(string name)
    {
        Pump();
        var frame = _window.CaptureRenderedFrame();
        if (frame is null) throw new InvalidOperationException("Nothing rendered for " + name);
        var file = Path.Combine(_out, name + ".png");
        frame.Save(file);
        Console.WriteLine("  wrote " + name + ".png");
    }

    // ------------------------------------------------------------------ sample data

    private static readonly string[] Shows = ["The Long Coast", "Night Signal", "Orbit Kitchen", "Paper Lanterns", "Northbound", "Glass Harbour", "Quiet Engine", "Red Meridian"];
    private static readonly string[] Palettes = ["#1E3A8A,#0EA5E9", "#7C2D12,#F59E0B", "#14532D,#22C55E", "#581C87,#EC4899", "#0F172A,#64748B", "#7F1D1D,#F97316", "#134E4A,#2DD4BF", "#312E81,#A78BFA"];

    private static string Seed(string data)
    {
        var art = Path.Combine(data, "art");
        var lib = Path.Combine(data, "Library", "Shows");
        Directory.CreateDirectory(art);
        var db = L.Library;
        long added = 1_700_000_000;
        for (int i = 0; i < Shows.Length; i++)
        {
            var folder = Path.Combine(lib, Shows[i]);
            Directory.CreateDirectory(folder);
            var poster = Path.Combine(art, $"poster{i}.png");
            Poster(poster, Shows[i], Palettes[i], 500, 750);
            for (int e = 1; e <= 6; e++)
            {
                var file = Path.Combine(folder, $"{Shows[i]} S01E0{e}.mkv");
                File.WriteAllText(file, "");
                db.Upsert(new LibraryItem(file, LibraryKind.Video, lib, folder, $"{Shows[i]} S01E0{e}", "", "", "", 0, 0, 2580 + e * 30, 1920, 1080, poster, added + i), 1, 1);
                if (e == 2 && i < 5) L.History.Put(file, new WatchEntry(600 + i * 200, 2640, 0, 0));
            }
        }
        string[] albums = ["Low Tide", "Satellite Hearts", "Field Notes", "Amber Hours", "Neon Weather", "Slow Arcade"];
        string[] artists = ["Mira Vale", "The Cartographers", "Juno Park", "Ash & Ivory", "Night Signal", "Pixel Choir"];
        for (int a = 0; a < albums.Length; a++)
        {
            var cover = Path.Combine(art, $"album{a}.png");
            Poster(cover, albums[a], Palettes[(a + 3) % Palettes.Length], 600, 600);
            for (int t = 1; t <= 9; t++)
            {
                var song = Path.Combine(data, "Library", "Music", albums[a], $"{t:00}.flac");
                Directory.CreateDirectory(Path.GetDirectoryName(song)!);
                db.Upsert(new LibraryItem(song, LibraryKind.Music, "", Path.GetDirectoryName(song)!, $"Track {t} of {albums[a]}", artists[a], albums[a], artists[a], t, 2020 + a, 180 + t * 9, 0, 0, cover, added), 1, 1);
            }
        }
        L.Settings.AddFolder(K.VideoRoots, lib);
        // Wallpaper-free, so each theme shows its own backdrop.
        var sample = Path.Combine(data, "Library", "Shows", Shows[0]);
        File.WriteAllText(Path.Combine(sample, "notes.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(sample, "Extras"));
        Poster(Path.Combine(sample, "poster.png"), Shows[0], Palettes[0], 300, 450);
        return sample;
    }

    private static void Poster(string file, string title, string palette, int w, int h)
    {
        var colors = palette.Split(',').Select(Color.Parse).ToArray();
        var grid = new Grid
        {
            Width = w, Height = h,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(colors[0], 0), new GradientStop(colors[1], 1) },
            },
        };
        grid.Children.Add(new Border
        {
            Width = w * 0.7, Height = w * 0.7, CornerRadius = new CornerRadius(w),
            Background = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromArgb(120, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) } },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, w * 0.1, -w * 0.15, 0),
        });
        grid.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = w / 9.0, FontWeight = FontWeight.Black, Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(w * 0.08), VerticalAlignment = VerticalAlignment.Bottom,
            LetterSpacing = 2, FontFamily = T.Font,
        });
        Render(grid, file, w, h);
    }

    private static void Render(Control c, string file, int w, int h)
    {
        c.Measure(new Size(w, h));
        c.Arrange(new Rect(0, 0, w, h));
        using var rtb = new RenderTargetBitmap(new PixelSize(w, h));
        rtb.Render(c);
        rtb.Save(file);
    }

    /// <summary>The app icon: a soft gradient tile with a lounge-chair glyph, at the
    /// sizes Windows asks for.</summary>
    private static void RenderIcons()
    {
        foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
        {
            var r = size * 0.22;
            var tile = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(r),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#0A84FF"), 0), new GradientStop(Color.Parse("#6D28D9"), 0.6), new GradientStop(Color.Parse("#DB2777"), 1) },
                },
                Child = new Viewbox
                {
                    Width = size * 0.62, Height = size * 0.62,
                    Child = new Canvas
                    {
                        Width = 24, Height = 24,
                        Children =
                        {
                            new Avalonia.Controls.Shapes.Path
                            {
                                // A sofa: back, seat and arms, the "lounge".
                                Data = Geometry.Parse("M5 11V8a3 3 0 0 1 3-3h8a3 3 0 0 1 3 3v3 M3 12a2 2 0 0 1 4 0v2h10v-2a2 2 0 0 1 4 0v5H3z M5 17v2 M19 17v2"),
                                Stroke = Brushes.White, StrokeThickness = size < 32 ? 2.6 : 1.9,
                                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                            },
                        },
                    },
                },
            };
            Render(tile, Path.Combine(_out, $"icon-{size}.png"), size, size);
        }
        Console.WriteLine("  wrote icon-*.png");
    }
}
