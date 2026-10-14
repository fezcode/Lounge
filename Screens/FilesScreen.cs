using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Audio;
using Lounge.Core.Files;
using Lounge.Core.Focus;
using Lounge.Core.Input;
using Lounge.Core.Launch;
using Lounge.Core.Media;
using Lounge.Core.Settings;
using Lounge.Shell;

namespace Lounge.Screens;

public enum FilePickKind { Folder, Image, Program, Subtitle }

/// <summary>When Files is opened to choose something for a setting.</summary>
public sealed record FilePick(FilePickKind Kind, string Title, Action<string> Done);

/// <summary>The controller file explorer. Browse drives and folders, play anything
/// playable, rename with the controller keyboard, copy, move, delete to the Recycle Bin,
/// make folders, and add library folders, all without a keyboard or mouse.</summary>
public sealed class FilesScreen : Screen
{
    private static List<string> _clipboard = new();
    private static bool _clipboardCut;

    private readonly FilePick? _pick;
    private readonly string? _pickFor;
    private string? _folder;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FocusItem, FileEntry> _entries = new();
    private readonly Dictionary<FocusItem, Place> _places = new();
    private readonly Dictionary<string, Border> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _cameFrom = new();
    private readonly TextBlock _path = Ui.Text("", T.Heading, T.Text, T.TitleWeight);
    private readonly TextBlock _status = Ui.Text("", T.Caption, T.TextMuted);
    private readonly StackPanel _placesPanel = new() { Spacing = 4 };
    private readonly ContentControl _listHost = new();
    private readonly FocusList _root = new(horizontal: true);
    private readonly FocusList _placesFocus = new(horizontal: false, rememberIndex: true);
    private FocusNode _listFocus = new FocusList(horizontal: false);
    private FileSystemWatcher? _watcher;
    private IDisposable? _reload;
    private bool _busy;
    private string? _focusName;

    public override string Context => "Files";
    public override string Title => "Files";
    public override bool WantsAmbient => true;

    public FilesScreen(string? start = null, FilePick? pick = null, string? pickFor = null)
    {
        _pick = pick;
        _pickFor = pickFor;
        if (pickFor is not null)
            _pick = new FilePick(FilePickKind.Folder, pickFor == K.VideoRoots ? "Choose a folder for Videos" : "Choose a folder for Music", path =>
            {
                L.Settings.AddFolder(pickFor, path);
                L.Window.Toast($"Added {Path.GetFileName(path.TrimEnd('\\'))} to {(pickFor == K.VideoRoots ? "Videos" : "Music")}. Scanning...", "library-add");
            });

        var grid = Ui.Grid("400,*", "Auto,*,Auto");
        var title = _pick is null ? "Files" : _pick.Title;
        grid.Children.Add(Ui.V(6, Ui.Header(title, T.Title), _status).Margin(96, 56, 0, 24).Col(0, 2).Row(0));
        grid.Children.Add(new ScrollViewer { Content = _placesPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }.Margin(72, 0, 0, 0).Col(0).Row(1));
        var right = Ui.Grid("*", "Auto,*");
        right.Children.Add(_path.Margin(0, 0, 96, 18).Row(0));
        right.Children.Add(_listHost.Row(1));
        grid.Children.Add(right.Margin(40, 0, 0, 0).Col(1).Row(1));
        grid.Children.Add(Hints.Bar("Files",
            (Act.Accept, _pick is null ? "Open" : "Open / Choose"), (Act.FileSelect, "Select"), (Act.FileRename, "Rename"),
            (Act.ContextMenu, "Options"), (Act.Back, "Up")).Margin(96, 18, 0, 40).Col(0, 2).Row(2));
        Children.Add(grid);

        _root.Add(_placesFocus);
        _root.Add(_listFocus);
        BuildPlaces();
        UseScope(_root);
        Navigate(start is not null && Directory.Exists(start) ? start : null);
    }

    private void BuildPlaces()
    {
        _placesPanel.Children.Clear();
        _placesFocus.Clear();
        _places.Clear();
        var places = new List<Place>();
        foreach (var root in L.Settings.List(K.VideoRoots).Concat(L.Settings.List(K.MusicRoots)).Where(Directory.Exists))
            places.Add(new Place(Path.GetFileName(root.TrimEnd('\\')) is { Length: > 0 } n ? n : root, root, "library-add"));
        places.AddRange(FileOps.Places());
        foreach (var p in places)
        {
            var label = Ui.Text(p.Name, T.Body, T.TextMuted, FontWeight.SemiBold);
            var card = new Card(Ui.H(18, Icons.Make(p.Icon, 30, T.TextMuted), label).Margin(22, 14), 14) { FocusScale = 1.03, ShowRing = false };
            card.Body.Background = Brushes.Transparent;
            card.Focused += f => { card.Body.Background = f ? T.WithAlpha(T.TextColor, 0.12) : Brushes.Transparent; label.Foreground = f ? T.Text : T.TextMuted; };
            var place = p;
            card.Item.Activate = () => Navigate(place.Path, intoList: true);
            _placesPanel.Children.Add(card);
            _placesFocus.Add(card.Item);
            _places[card.Item] = p;
        }
    }

    // ------------------------------------------------------------------ listing

    private void Navigate(string? folder, bool intoList = false, string? focusName = null)
    {
        _folder = folder;
        _focusName = focusName;
        _selected.Clear();
        Watch();
        Load();
        if (intoList && _listFocus.HasItems) FocusItem(_listFocus.Current!);
    }

    private void Load()
    {
        _entries.Clear();
        _checks.Clear();
        var gridView = L.Settings.String(K.FilesView) == "grid";
        IReadOnlyList<FileEntry> items = [];
        string? error = null;
        if (_folder is not null)
        {
            try { items = FileOps.List(_folder, L.Settings.Bool(K.ShowHidden)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = "Windows didn't allow opening this folder."; }
        }
        _path.Text = _folder ?? "Choose a place";
        UpdateStatus(items.Count);

        FocusNode focus;
        Control view;
        var actions = new List<(string Label, string Icon, Action Run)>();
        if (_pick?.Kind == FilePickKind.Folder && _folder is not null)
            actions.Add(("Choose this folder", "check", () => Pick(_folder)));
        if (_folder is not null && _clipboard.Count > 0)
            actions.Add(($"Paste {_clipboard.Count} item{(_clipboard.Count == 1 ? "" : "s")} here", "paste", Paste));

        if (_folder is null)
        {
            view = Ui.V(18, Icons.Make("folder", 120, T.TextFaint, 1.2), Ui.Muted("Pick a place on the left to start.", T.Body)).Margin(0, 140, 0, 0);
            focus = new FocusList(horizontal: false);
        }
        else if (gridView)
        {
            var columns = Math.Max(3, (int)((L.Window.DesignWidth - 600) / 250));
            var g = new FocusGrid(columns);
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 250, ItemHeight = 280 };
            foreach (var (label, icon, run) in actions) wrap.Children.Add(ActionTile(label, icon, run, g, grid: true));
            foreach (var e in items) wrap.Children.Add(GridTile(e, g));
            focus = g;
            view = new ScrollViewer { Content = wrap.Margin(0, 10, 60, 60), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        }
        else
        {
            var list = new FocusList(horizontal: false);
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 80, 80) };
            foreach (var (label, icon, run) in actions) panel.Children.Add(ActionTile(label, icon, run, list, grid: false));
            foreach (var e in items) panel.Children.Add(ListRow(e, list));
            if (error is not null || items.Count == 0)
                panel.Children.Add(Ui.Muted(error ?? "This folder is empty.", T.Body).Margin(24, 30, 0, 0));
            focus = list;
            view = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        }

        _root.Remove(_listFocus);
        _listFocus = _root.Add(focus);
        _listHost.Content = view;
        RefreshFocus();
        if (_focusName is not null && _entries.FirstOrDefault(kv => kv.Value.Name.Equals(_focusName, StringComparison.OrdinalIgnoreCase)).Key is { } again)
        {
            FocusItem(again);
            _focusName = null;
        }
    }

    private void UpdateStatus(int count)
    {
        var parts = new List<string>();
        if (_folder is not null) parts.Add($"{count} item{(count == 1 ? "" : "s")}");
        if (_selected.Count > 0) parts.Add($"{_selected.Count} selected");
        if (_clipboard.Count > 0) parts.Add($"{_clipboard.Count} to {(_clipboardCut ? "move" : "copy")}");
        _status.Text = string.Join("  Â·  ", parts);
    }

    private Control ActionTile(string label, string icon, Action run, FocusNode focus, bool grid)
    {
        var text = Ui.Text(label, T.Body, T.Accent, FontWeight.SemiBold);
        Control content = grid
            ? Ui.V(12, Icons.Make(icon, 64, T.Accent), text.Also(t => { t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; })).Align(HorizontalAlignment.Center, VerticalAlignment.Center)
            : Ui.H(20, Icons.Make(icon, 34, T.Accent), text).Margin(24, 16);
        var card = new Card(content, 14) { FocusScale = grid ? 1.06 : 1.01, ShowRing = grid };
        if (grid) { card.Width = 220; card.Height = 250; }
        card.Body.Background = T.WithAlpha(T.AccentColor, 0.12);
        card.Item.Activate = run;
        Add(focus, card.Item);
        return card;
    }

    private static void Add(FocusNode focus, FocusItem item)
    {
        if (focus is FocusGrid g) g.Add(item); else ((FocusList)focus).Add(item);
    }

    private static string IconFor(FileEntry e) => e.Kind switch
    {
        MediaKind.Folder => "folder", MediaKind.Video => "video", MediaKind.Audio => "music", MediaKind.Image => "image",
        MediaKind.Subtitle => "subtitles", MediaKind.Program => "apps", _ => "file",
    };

    private Border Check(FileEntry e)
    {
        var check = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderBrush = T.Accent, BorderThickness = new Thickness(2),
            Background = T.Accent, Opacity = _selected.Contains(e.Path) ? 1 : 0, Child = Icons.Make("check", 20, T.OnAccent, 2.4).Align(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        _checks[e.Path] = check;
        return check;
    }

    private Control ListRow(FileEntry e, FocusNode focus)
    {
        var name = Ui.Text(L.Settings.Bool(K.ShowExtensions) || e.IsDirectory ? e.Name : e.Stem, T.Body, e.Hidden ? T.TextMuted : T.Text, FontWeight.Medium);
        var meta = Ui.Text(e.IsDirectory ? "" : FileOps.FormatSize(e.Size), T.Caption, T.TextMuted);
        var date = Ui.Text(e.Modified.ToString("d MMM yyyy"), T.Caption, T.TextFaint);
        var row = Ui.Grid("Auto,Auto,*,160,200", "Auto");
        row.Children.Add(Check(e).Margin(0, 0, 16, 0).Col(0).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        row.Children.Add(Icons.Make(IconFor(e), 34, e.IsDirectory ? T.Accent : T.TextMuted).Margin(0, 0, 22, 0).Col(1));
        row.Children.Add(name.Col(2).Align(HorizontalAlignment.Left, VerticalAlignment.Center));
        row.Children.Add(meta.Col(3).Align(HorizontalAlignment.Right, VerticalAlignment.Center));
        row.Children.Add(date.Col(4).Align(HorizontalAlignment.Right, VerticalAlignment.Center));
        var card = new Card(row.Margin(20, 14), 12) { FocusScale = 1.01, ShowRing = false };
        card.Body.Background = Brushes.Transparent;
        card.Focused += f => card.Body.Background = f ? T.WithAlpha(T.TextColor, 0.12) : Brushes.Transparent;
        Wire(card, e);
        Add(focus, card.Item);
        return card;
    }

    private Control GridTile(FileEntry e, FocusNode focus)
    {
        Control art = e.Kind == MediaKind.Image
            ? ImageCache.Art(e.Path, 220, 160, Icons.Make("image", 60, T.TextMuted))
            : new Border { Width = 220, Height = 160, Child = Icons.Make(IconFor(e), 70, e.IsDirectory ? T.Accent : T.TextMuted, 1.4).Align(HorizontalAlignment.Center, VerticalAlignment.Center) };
        var name = Ui.Text(e.Name, T.Caption, T.Text, FontWeight.Medium, TextWrapping.Wrap);
        name.MaxHeight = 64;
        name.TextAlignment = TextAlignment.Center;
        var grid = new Grid();
        grid.Children.Add(Ui.V(10, art, name.Margin(10, 0)));
        grid.Children.Add(Check(e).Margin(10).Align(HorizontalAlignment.Left, VerticalAlignment.Top));
        var card = new Card(grid, 16) { Width = 220, Height = 250, FocusScale = 1.06 };
        Wire(card, e);
        Add(focus, card.Item);
        return card;
    }

    private void Wire(Card card, FileEntry e)
    {
        card.Item.Activate = () => Open(e);
        card.Item.Secondary = () => ToggleSelect(e);
        _entries[card.Item] = e;
    }

    // ------------------------------------------------------------------ actions

    private void Open(FileEntry e)
    {
        if (e.IsDirectory) { _cameFrom.Push(_folder ?? ""); Navigate(e.Path, intoList: true); return; }
        if (_pick is not null && Matches(_pick.Kind, e)) { Pick(e.Path); return; }
        switch (e.Kind)
        {
            case MediaKind.Video or MediaKind.Audio:
                var siblings = _entries.Values.Where(x => x.Kind == e.Kind).Select(x => x.Path).ToList();
                Playback.Open(e.Path, siblings, siblings.IndexOf(e.Path));
                break;
            case MediaKind.Image:
                var images = _entries.Values.Where(x => x.Kind == MediaKind.Image).Select(x => x.Path).ToList();
                L.Window.Push(new ImageViewerScreen(images, images.IndexOf(e.Path)));
                break;
            case MediaKind.Subtitle when L.Player.HasVideo:
                if (L.Player.LoadSubtitleFile(e.Path)) L.Window.Toast($"Subtitles: {e.Name}", "subtitles");
                break;
            case MediaKind.Program:
                L.Window.Ask($"Open {e.Stem}?", "It will start like any program. Hold View + Menu to come back to Lounge.", ["Open", "Add to Home", "Cancel"], 2, i =>
                {
                    if (i == 0) Launcher.Launch(new Tile { Kind = TileKind.App, Name = e.Stem, Target = e.Path });
                    if (i == 1) AddToHome(e);
                }, "apps");
                break;
            default:
                L.Window.Ask($"Open {e.Name} with Windows?", "Lounge can't show this kind of file itself.", ["Open", "Cancel"], 1, i =>
                {
                    if (i == 0) try { Process.Start(new ProcessStartInfo(e.Path) { UseShellExecute = true }); }
                        catch (Exception x) when (x is System.ComponentModel.Win32Exception) { L.Window.Toast("No app is set to open that file.", "warning"); }
                }, "file");
                break;
        }
    }

    private static bool Matches(FilePickKind kind, FileEntry e) => kind switch
    {
        FilePickKind.Image => e.Kind == MediaKind.Image,
        FilePickKind.Program => e.Kind == MediaKind.Program,
        FilePickKind.Subtitle => e.Kind == MediaKind.Subtitle,
        _ => false,
    };

    private void Pick(string path)
    {
        var pick = _pick!;
        L.Play(UiSound.Select);
        L.Window.Pop();
        pick.Done(path);
    }

    private void ToggleSelect(FileEntry e)
    {
        if (!_selected.Remove(e.Path)) _selected.Add(e.Path);
        if (_checks.TryGetValue(e.Path, out var c)) c.Opacity = _selected.Contains(e.Path) ? 1 : 0;
        UpdateStatus(_entries.Count);
    }

    private FileEntry? Current => Focused is { } f && _entries.TryGetValue(f, out var e) ? e : null;

    private IReadOnlyList<string> Targets() =>
        _selected.Count > 0 ? _selected.ToList() : Current is { } c ? [c.Path] : [];

    private async void Rename(FileEntry e)
    {
        var lockExt = L.Settings.Bool(K.LockExtension) && !e.IsDirectory && e.Extension.Length > 0;
        var initial = lockExt ? e.Stem : e.Name;
        var name = await L.Window.KeyboardAsync(new KeyboardRequest($"Rename {(e.IsDirectory ? "folder" : "file")}", initial,
            LockedSuffix: lockExt ? e.Extension : null,
            Suggestions: [e.Stem, Lounge.Core.Library.LibraryScanner.CleanTitle(e.Stem)],
            Validate: n => FileOps.ValidateName(n)));
        if (name is null || name == e.Name) return;
        var r = FileOps.Rename(e.Path, name);
        if (!r.Ok) { L.Window.Toast(r.Error!, "warning"); L.Play(UiSound.Error); return; }
        L.Window.Toast($"Renamed to {name}", "pencil");
        Navigate(_folder, focusName: name);
    }

    private async void NewFolder()
    {
        if (_folder is null) return;
        var name = await L.Window.KeyboardAsync(new KeyboardRequest("New folder", FileOps.UniqueName(_folder, "New folder"),
            Validate: n => FileOps.ValidateName(n)));
        if (name is null) return;
        var r = FileOps.NewFolder(_folder, name);
        if (!r.Ok) { L.Window.Toast(r.Error!, "warning"); return; }
        Navigate(_folder, focusName: name);
    }

    private void CopyOrCut(bool cut)
    {
        var t = Targets();
        if (t.Count == 0) return;
        _clipboard = t.ToList();
        _clipboardCut = cut;
        _selected.Clear();
        L.Window.Toast($"{(cut ? "Cut" : "Copied")} {t.Count} item{(t.Count == 1 ? "" : "s")}. Open a folder and choose Paste.", cut ? "cut" : "copy");
        Load();
    }

    private async void Paste()
    {
        if (_folder is null || _clipboard.Count == 0 || _busy) return;
        _busy = true;
        var dest = _folder;
        var progress = new Progress<FileProgress>(p =>
            _status.Text = p.BytesTotal > 0 ? $"{(_clipboardCut ? "Moving" : "Copying")} {p.Current} Â· {p.BytesDone * 100 / Math.Max(1, p.BytesTotal)}%" : _status.Text);
        var result = await FileOps.TransferAsync(_clipboard, dest, _clipboardCut, async path =>
        {
            var i = await L.Window.AskAsync($"{Path.GetFileName(path)} is already here", "What should Lounge do?",
                ["Replace", "Keep both", "Skip", "Stop"], 3, "copy");
            return i switch { 0 => ConflictChoice.Replace, 1 => ConflictChoice.KeepBoth, 2 => ConflictChoice.Skip, _ => ConflictChoice.Cancel };
        }, progress);
        _busy = false;
        if (result.Ok)
        {
            L.Window.Toast(_clipboardCut ? "Moved." : "Copied.", "check");
            if (_clipboardCut) _clipboard.Clear();
        }
        else L.Window.Toast(result.Error ?? "Something went wrong.", "warning");
        Load();
    }

    private void Delete()
    {
        var t = Targets();
        if (t.Count == 0) return;
        var permanent = L.Settings.Bool(K.PermanentDelete);
        void Go()
        {
            var r = FileOps.Delete(t, permanent);
            if (!r.Ok) { L.Window.Toast(r.Error!, "warning"); return; }
            L.Window.Toast(permanent ? "Deleted." : "Moved to the Recycle Bin.", "trash");
            Load();
        }
        if (!L.Settings.Bool(K.ConfirmDelete) && !permanent) { Go(); return; }
        var what = t.Count == 1 ? $"\"{Path.GetFileName(t[0].TrimEnd('\\'))}\"" : $"{t.Count} items";
        L.Window.Ask(permanent ? $"Delete {what} for good?" : $"Move {what} to the Recycle Bin?",
            permanent ? "This can't be undone." : "You can restore it from the Recycle Bin in Windows.",
            [permanent ? "Delete" : "Recycle", "Cancel"], 1, i => { if (i == 0) Go(); }, "trash");
    }

    private void AddLibrary(string key, string folder)
    {
        L.Settings.AddFolder(key, folder);
        L.Window.Toast($"Added {Path.GetFileName(folder.TrimEnd('\\'))} to {(key == K.VideoRoots ? "Videos" : "Music")}. Scanning...", "library-add");
        BuildPlaces();
        RefreshFocus();
    }

    private static void AddToHome(FileEntry e)
    {
        L.Tiles.Add(new Tile { Kind = TileKind.App, Name = e.Stem, Target = e.Path, Color = "#3A3A4A" });
        L.Window.Toast($"{e.Stem} is on Home now.", "plus");
    }

    private void ShowOptions()
    {
        var e = Current;
        var items = new List<MenuEntry>();
        if (e is not null)
        {
            items.Add(new MenuEntry("Open", "play", () => Open(e)));
            items.Add(new MenuEntry("Rename", "pencil", () => Rename(e)));
        }
        if (Targets().Count > 0)
        {
            items.Add(new MenuEntry(_selected.Count > 1 ? $"Copy {_selected.Count} items" : "Copy", "copy", () => CopyOrCut(false)));
            items.Add(new MenuEntry(_selected.Count > 1 ? $"Cut {_selected.Count} items" : "Cut", "cut", () => CopyOrCut(true)));
            items.Add(new MenuEntry(_selected.Count > 1 ? $"Delete {_selected.Count} items" : "Delete", "trash", Delete, Danger: true));
        }
        if (_folder is not null && _clipboard.Count > 0) items.Add(new MenuEntry($"Paste {_clipboard.Count} here", "paste", Paste));
        if (_folder is not null) items.Add(new MenuEntry("New folder", "new-folder", NewFolder));
        var libFolder = e is { IsDirectory: true } ? e.Path : _folder;
        if (libFolder is not null)
        {
            var name = Path.GetFileName(libFolder.TrimEnd('\\'));
            items.Add(new MenuEntry("Add to Videos", "library-add", () => AddLibrary(K.VideoRoots, libFolder), name));
            items.Add(new MenuEntry("Add to Music", "library-add", () => AddLibrary(K.MusicRoots, libFolder), name));
            items.Add(new MenuEntry("Use for ambient music", "music", () =>
            {
                L.Settings.Set(K.AmbientFolder, libFolder);
                L.Settings.Set(K.AmbientSource, "folder");
                L.Window.Toast($"Ambient music now plays from {name}.", "music");
            }, name));
        }
        if (e is { Kind: MediaKind.Image })
            items.Add(new MenuEntry("Use as wallpaper", "image", () => { L.Settings.Set(K.Wallpaper, e.Path); L.Window.Toast("Wallpaper set.", "image"); }));
        if (e is { Kind: MediaKind.Program }) items.Add(new MenuEntry("Add to Home", "plus", () => AddToHome(e)));
        if (e is not null) items.Add(new MenuEntry("Select", "check", () => ToggleSelect(e), Checked: _selected.Contains(e.Path)));
        if (_entries.Count > 0)
            items.Add(_selected.Count > 0
                ? new MenuEntry("Clear selection", "close", () => { _selected.Clear(); Load(); })
                : new MenuEntry("Select all", "check", () => { foreach (var x in _entries.Values) _selected.Add(x.Path); Load(); }));
        items.Add(new MenuEntry(L.Settings.String(K.FilesView) == "grid" ? "Show as list" : "Show as grid", L.Settings.String(K.FilesView) == "grid" ? "list" : "grid",
            () => { L.Settings.Set(K.FilesView, L.Settings.String(K.FilesView) == "grid" ? "list" : "grid"); Load(); }));
        items.Add(new MenuEntry(L.Settings.Bool(K.ShowHidden) ? "Hide hidden files" : "Show hidden files", "eye",
            () => { L.Settings.Set(K.ShowHidden, !L.Settings.Bool(K.ShowHidden)); Load(); }));
        if (e is not null) items.Add(new MenuEntry("Properties", "info", () => Properties(e)));
        L.Window.Menu(e?.Name ?? Path.GetFileName(_folder?.TrimEnd('\\') ?? "Files"), items);
    }

    private static void Properties(FileEntry e)
    {
        var size = e.IsDirectory ? "" : $"\nSize: {FileOps.FormatSize(e.Size)}";
        L.Window.Ask(e.Name, $"Location: {Path.GetDirectoryName(e.Path)}{size}\nModified: {e.Modified:f}", ["Close"], 0, _ => { }, "info");
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_folder is null) return;
        try
        {
            _watcher = new FileSystemWatcher(_folder) { EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _reload?.Dispose();
                _reload = DispatcherTimer.RunOnce(() =>
                {
                    _focusName = Current?.Name;
                    if (!_busy) Load();
                }, TimeSpan.FromMilliseconds(600));
            });
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Renamed += (s, a) => h(s, a);
        }
        catch (Exception x) when (x is ArgumentException or IOException) { }
    }

    public override void OnClosed() => _watcher?.Dispose();

    // ------------------------------------------------------------------ input

    public override bool Handle(ActionEvent e)
    {
        if (e.Phase == ActionPhase.Pressed)
        {
            switch (e.Action)
            {
                case Act.ContextMenu: ShowOptions(); return true;
                case Act.FileRename: if (Current is { } c) Rename(c); return true;
                case Act.FileSelect: if (Current is { } s) { ToggleSelect(s); L.Play(UiSound.Toggle); } return true;
            }
        }
        return base.Handle(e);
    }

    protected override bool OnBack()
    {
        L.Play(UiSound.Back);
        if (_selected.Count > 0) { _selected.Clear(); Load(); return true; }
        if (_folder is null) return L.Window.Pop();
        var parent = Directory.GetParent(_folder.TrimEnd('\\'))?.FullName;
        var leaving = Path.GetFileName(_folder.TrimEnd('\\'));
        if (parent is null || _places.Values.Any(p => string.Equals(p.Path.TrimEnd('\\'), _folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) && _cameFrom.Count == 0)
        {
            Navigate(null);
            FocusItem(_placesFocus.Current!);
            return true;
        }
        if (_cameFrom.Count > 0) _cameFrom.Pop();
        Navigate(parent, intoList: true, focusName: leaving);
        return true;
    }
}
