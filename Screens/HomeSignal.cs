using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Lounge.Controls;
using Lounge.Core.Focus;
using Lounge.Shell;

namespace Lounge.Screens;

public sealed partial class HomeScreen
{
    private string? _signalDestination;

    /// <summary>Fezcodex's Signal poster becomes a living-room launcher: the artwork has
    /// the upper half to itself, with destinations and the real collection below it.</summary>
    private (Control, FocusNode) Signal()
    {
        var grid = Ui.Grid("*", "352,104,*,76");
        var root = new FocusList(horizontal: false);
        var menu = HomeModel.Menu();
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        var masthead = Ui.Text("Lounge", 166, T.Text, FontWeight.SemiBold);
        masthead.FontFamily = T.DisplayFont;
        masthead.LetterSpacing = -8;
        var branding = Ui.V(0, masthead);
        branding.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(branding.Margin(96, 64, 0, 0).Row(0));
        if (Widgets.Strip(spacing: 24, w: 310, h: 180) is { } widgets)
            grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top)
                .Margin(0, 112, 96, 0).Row(0));

        var tabsFocus = root.Add(new FocusList(horizontal: true));
        var tabs = new Grid { Margin = new Thickness(80, 8, 80, 12) };
        var shelfFocus = root.Add(new FocusList(horizontal: false, rememberIndex: false));
        var shelf = new ContentControl();
        var tabViews = new List<(Card Card, TextBlock Label, Border Marker, HomeEntry Entry)>();
        var shown = "";

        void Select(HomeEntry destination)
        {
            if (shown == destination.Title) return;
            shown = _signalDestination = destination.Title;
            foreach (var tab in tabViews)
            {
                var active = tab.Entry.Title == destination.Title;
                tab.Label.Foreground = active ? T.Accent : T.TextMuted;
                tab.Marker.Opacity = active ? 1 : 0;
                tab.Card.Body.Background = active ? T.Surface : T.WithAlpha(T.Bg0Color, 0.72);
            }
            L.Window.Backdrop.SetSignalScene(destination.Title switch
            {
                "Videos" => "signal-bloom",
                "Music" or "Now Playing" => "night-frequency",
                "Apps & Games" => "chromatic-drift",
                _ => "magenta-horizon",
            });

            foreach (var item in shelfFocus.Items.ToList()) _entries.Remove(item);
            shelfFocus.Clear();
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 20) };

            void AddShelf(HomeSection section)
            {
                var (w, h) = section.Shape switch
                {
                    EntryShape.Poster => (166.0, 249.0),
                    EntryShape.Wide => (320.0, 180.0),
                    _ => (226.0, 226.0),
                };
                var (view, focus) = Row(section, w, h, headerSize: 30, spacing: 28);
                stack.Children.Add(view);
                shelfFocus.Add(focus);
            }

            var id = destination.Title switch
            {
                "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null,
            };
            if (destination.Title == "Videos" && sections.GetValueOrDefault("jump") is { } jump)
                AddShelf(jump);
            if (id is not null && sections.GetValueOrDefault(id) is { } section)
                AddShelf(section);

            if (!shelfFocus.HasItems)
            {
                // A direct way into the destination also serves empty/hidden collections.
                var title = Ui.Text(destination.Title, 38, T.Text, FontWeight.SemiBold);
                title.FontFamily = T.DisplayFont;
                var description = Ui.Muted(destination.Subtitle ?? "Open your collection", 26);
                var open = Ui.H(12, Icons.Make(destination.Icon, 30, T.Accent),
                    Ui.Text("Open " + destination.Title, 26, T.Accent, FontWeight.SemiBold));
                var card = new Card(Ui.V(16, title, description, open).Margin(32), 4, destination)
                {
                    Width = 600, HorizontalAlignment = HorizontalAlignment.Left,
                };
                card.Body.BorderBrush = T.Line;
                card.Body.BorderThickness = new Thickness(1);
                card.Item.Activate = destination.Open;
                card.Item.Secondary = destination.Options;
                _entries[card.Item] = destination;
                stack.Children.Add(card.Margin(96, 24, 96, 32));
                shelfFocus.Add(card.Item);
            }

            shelf.Content = new ScrollViewer
            {
                Content = stack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            };
            if (!_building) RefreshFocus();
        }

        for (var i = 0; i < menu.Count; i++)
        {
            var destination = menu[i];
            tabs.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var label = Ui.Text(destination.Title, 26, T.TextMuted, FontWeight.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var marker = new Border { Height = 3, Background = T.Accent, Opacity = 0 };
            var face = Ui.Grid("*", "*,3");
            face.Children.Add(label.Align(HorizontalAlignment.Center, VerticalAlignment.Center).Margin(12, 0).Row(0));
            face.Children.Add(marker.Row(1));
            var card = new Card(face, 4, destination) { FocusScale = 1, ShowGlow = false };
            card.Body.Background = Brushes.Transparent;
            card.Item.Activate = destination.Open;
            card.Focused += focused => { if (focused) Select(destination); };
            _entries[card.Item] = destination;
            tabViews.Add((card, label, marker, destination));
            tabsFocus.Add(card.Item);
            tabs.Children.Add(card.Margin(8, 4).Col(i));
        }
        var selected = Math.Max(0, menu.ToList().FindIndex(d => d.Title == _signalDestination));
        tabsFocus.Index = selected;
        Select(menu[selected]);
        grid.Children.Add(tabs.Row(1));
        grid.Children.Add(shelf.Row(2));
        grid.Children.Add(HintBar().Margin(96, 14, 96, 24).Row(3));
        return (grid, root);
    }
}
