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
    private string? _billboardDestination;

    /// <summary>Heatwave: a crime-caper poster. The destination's illustrated scene fills the
    /// screen; the destinations stack down the left in tall condensed capitals, the chosen
    /// one lit in hot pink behind a slanted bar; its collection runs along the bottom.</summary>
    private (Control, FocusNode) Billboard()
    {
        var grid = Ui.Grid("620,*", "*,Auto,76");
        var menu = HomeModel.Menu();
        var sections = HomeModel.Sections().ToDictionary(s => s.Id);

        // The wordmark: condensed, tilted a touch, like a title card.
        var mark = Ui.Text(Lounge.Core.Product.Name.ToUpperInvariant(), 64, T.Accent, FontWeight.SemiBold);
        mark.FontFamily = T.DisplayFont;
        mark.LetterSpacing = 10;
        mark.RenderTransform = new SkewTransform(-8, 0);
        grid.Children.Add(mark.Align(HorizontalAlignment.Left, VerticalAlignment.Top).Margin(96, 60, 0, 0).Col(0).Row(0));
        if (Widgets.Strip(spacing: 20, w: 300, h: 170) is { } widgets)
            grid.Children.Add(widgets.Align(HorizontalAlignment.Right, VerticalAlignment.Top).Margin(0, 112, 96, 0).Col(1).Row(0));

        var menuFocus = new FocusList(horizontal: false);
        var list = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(80, 148, 0, 16) };
        var shelfFocus = new FocusList(horizontal: false, rememberIndex: false);
        // The menu is on the left and its collection below: Right (or Down past the end)
        // reaches the collection, Left or Up comes back.
        var root = new FocusMenuShelf(menuFocus, shelfFocus);
        var shelf = new ContentControl();
        var views = new List<(TextBlock Label, Border Bar, HomeEntry Entry)>();
        var shown = "";

        void Select(HomeEntry destination)
        {
            if (shown == destination.Title) return;
            shown = _billboardDestination = destination.Title;
            foreach (var (label, bar, entry) in views)
            {
                var active = entry.Title == destination.Title;
                label.Foreground = active ? T.OnAccent : T.TextMuted;
                bar.Opacity = active ? 1 : 0;
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
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };

            void AddShelf(HomeSection section)
            {
                var (w, h) = section.Shape switch
                {
                    EntryShape.Poster => (150.0, 225.0),
                    EntryShape.Wide => (300.0, 169.0),
                    _ => (200.0, 200.0),
                };
                var (view, focus) = Row(section, w, h, headerSize: 28, spacing: 24);
                stack.Children.Add(view);
                shelfFocus.Add(focus);
            }

            var id = destination.Title switch
            {
                "Videos" => "videos", "Music" => "music", "Apps & Games" => "apps", "Web" => "web", _ => null,
            };
            if (destination.Title == "Videos" && sections.GetValueOrDefault("jump") is { } jump)
                AddShelf(jump);
            else if (id is not null && sections.GetValueOrDefault(id) is { } section)
                AddShelf(section);

            if (!shelfFocus.HasItems)
            {
                // An empty or hidden collection still gets a way in.
                var title = Ui.Text(destination.Title.ToUpperInvariant(), 44, T.Text, FontWeight.SemiBold);
                title.FontFamily = T.DisplayFont;
                var description = Ui.Muted(destination.Subtitle ?? "Open your collection", 24);
                var open = Ui.H(12, Icons.Make(destination.Icon, 28, T.Accent),
                    Ui.Text("Open " + destination.Title, 24, T.Accent, FontWeight.SemiBold));
                var card = new Card(Ui.V(12, title, description, open).Margin(28), -1, destination)
                {
                    Width = 560, HorizontalAlignment = HorizontalAlignment.Left,
                };
                card.Body.BorderBrush = T.Line;
                card.Body.BorderThickness = new Thickness(1);
                card.Item.Activate = destination.Open;
                card.Item.Secondary = destination.Options;
                _entries[card.Item] = destination;
                stack.Children.Add(card.Margin(96, 12, 96, 24));
                shelfFocus.Add(card.Item);
            }

            shelf.Content = new ScrollViewer
            {
                Content = stack,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                MaxHeight = 360,
            };
            if (!_building) RefreshFocus();
        }

        foreach (var destination in menu)
        {
            var label = Ui.Text(destination.Title.ToUpperInvariant(), 48, T.TextMuted, FontWeight.SemiBold);
            label.FontFamily = T.DisplayFont;
            label.LetterSpacing = 2;
            // The slanted bar behind the chosen destination, like a sticker on the poster.
            var bar = new Border
            {
                Background = T.Accent, Opacity = 0, CornerRadius = new CornerRadius(2),
                RenderTransform = new SkewTransform(-12, 0), Margin = new Thickness(-14, 6, -22, 4),
            };
            var face = new Grid();
            face.Children.Add(bar);
            face.Children.Add(label.Margin(8, 0, 16, 0));
            var card = new Card(face, 0, destination) { FocusScale = 1.03, ShowGlow = false, HorizontalAlignment = HorizontalAlignment.Left };
            card.Body.Background = Brushes.Transparent;
            card.Body.BorderThickness = new Thickness(0);   // the slanted bar marks it, not an outline
            card.Item.Activate = destination.Open;
            card.Focused += focused => { if (focused) Select(destination); };
            _entries[card.Item] = destination;
            views.Add((label, bar, destination));
            menuFocus.Add(card.Item);
            list.Children.Add(card);
        }
        var selected = Math.Max(0, menu.ToList().FindIndex(d => d.Title == _billboardDestination));
        menuFocus.Index = selected;
        Select(menu[selected]);
        grid.Children.Add(list.Col(0).Row(0));
        grid.Children.Add(Span(shelf).Row(1));
        grid.Children.Add(Span(HintBar().Margin(96, 14, 96, 24)).Row(2));
        return (grid, root);

        static Control Span(Control c) { Grid.SetColumnSpan(c, 2); return c; }
    }
}
