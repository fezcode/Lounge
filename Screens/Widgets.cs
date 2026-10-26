using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lounge.Controls;
using Lounge.Core.Settings;
using Lounge.Core.Weather;
using Lounge.Shell;

namespace Lounge.Screens;

/// <summary>The weather and calendar widgets every home layout can show. They are drawn
/// from the theme (its surface, corners, type and colours), so the same widget is frosted
/// glass in Aurora, a printed card in Screening Room and pixels in Arcade. Each comes in
/// two sizes: compact for a corner, and a panel with the hours or the month.</summary>
public static class Widgets
{
    public static bool WeatherOn => L.Settings.Bool(K.ShowWeather);
    public static bool CalendarOn => L.Settings.Bool(K.ShowCalendar);
    public static bool Any => WeatherOn || CalendarOn;

    public const double CompactW = 270, CompactH = 150;

    /// <summary>The compact widgets that are on, side by side (or stacked); null when both are off.</summary>
    public static Control? Strip(Orientation orientation = Orientation.Horizontal, double spacing = 20, double w = CompactW, double h = CompactH)
    {
        if (!Any) return null;
        var panel = new StackPanel { Orientation = orientation, Spacing = spacing };
        if (WeatherOn) panel.Children.Add(Weather(w, h));
        if (CalendarOn) panel.Children.Add(Calendar(w, h));
        return panel;
    }

    /// <summary>How wide <see cref="Strip"/> lies, so a layout can keep its text clear of it.</summary>
    public static double StripWidth(double spacing = 20, double w = CompactW)
    {
        var n = (WeatherOn ? 1 : 0) + (CalendarOn ? 1 : 0);
        return n == 0 ? 0 : n * w + (n - 1) * spacing;
    }

    /// <summary>The weather: compact when small, with the coming hours when there's room.</summary>
    public static Control Weather(double w, double h, IBrush? background = null) =>
        new Live(w, h, background, subscribe: true, () => h >= 240 ? WeatherPanel(w, h) : WeatherCompact(w, h));

    /// <summary>Today: compact when small, with the month when there's room.</summary>
    public static Control Calendar(double w, double h, IBrush? background = null) =>
        new Live(w, h, background, subscribe: false, () => h >= 240 ? CalendarPanel(w, h) : CalendarCompact());

    // ------------------------------------------------------------------ weather

    public static string Glyph(Sky sky, bool day) => sky switch
    {
        Sky.Clear => day ? "sun" : "moon",
        Sky.PartlyCloudy => day ? "cloud-sun" : "cloud",
        Sky.Fog => "fog",
        Sky.Drizzle => "drizzle",
        Sky.Rain => "rain",
        Sky.Snow => "snow",
        Sky.Storm => "storm",
        _ => "cloud",
    };

    private static string Deg(double t) => Math.Round(t).ToString("0", CultureInfo.InvariantCulture) + "°";

    private static string Hour(DateTime t) => t.ToString(L.Settings.Bool(K.Clock24h) ? "HH" : "h tt", CultureInfo.CurrentCulture);

    /// <summary>No forecast yet: what's happening instead, under the cloud.</summary>
    private static Control Waiting(bool roomy)
    {
        var why = L.Weather.Problem ?? "Getting the weather...";
        var text = Ui.Text(why, T.Caption, T.TextMuted, FontWeight.Medium, TextWrapping.Wrap);
        var hint = Ui.Text("Settings > Appearance > Weather city", T.Caption, T.TextFaint, wrap: TextWrapping.Wrap);
        var v = Ui.V(8, Ui.H(14, Icons.Make("cloud", 40, T.TextMuted), Ui.Text("Weather", T.Body, T.Text, FontWeight.SemiBold).Align(HorizontalAlignment.Left, VerticalAlignment.Center)), text);
        if (roomy && L.Weather.Problem is not null) v.Children.Add(hint);
        v.VerticalAlignment = VerticalAlignment.Center;
        return v;
    }

    private static Control WeatherCompact(double w, double h)
    {
        if (L.Weather.Current is not { } r) return Waiting(roomy: false);
        var temp = Ui.Display(Ui.Text(Deg(r.Temp), 52, T.Text, T.TitleWeight));
        var top = Ui.H(14, Icons.Make(Glyph(r.Sky, r.Day), 46, T.Text, 1.6).Align(HorizontalAlignment.Left, VerticalAlignment.Center), temp);
        var place = Ui.Text($"{r.Place} · {r.Condition}", T.Caption, T.TextMuted, FontWeight.SemiBold);
        place.MaxWidth = w - 40;
        place.HorizontalAlignment = HorizontalAlignment.Left;
        place.TextTrimming = TextTrimming.CharacterEllipsis;
        var range = Ui.Text($"H {Deg(r.High)}  L {Deg(r.Low)}", T.Caption, T.TextFaint);
        var v = Ui.V(2, top, place, range);
        v.VerticalAlignment = VerticalAlignment.Center;
        return v;
    }

    private static Control WeatherPanel(double w, double h)
    {
        if (L.Weather.Current is not { } r) return Waiting(roomy: true);
        var head = Ui.Text(r.Place, T.Caption, T.TextMuted, FontWeight.SemiBold);
        head.MaxWidth = w * 0.5;
        head.HorizontalAlignment = HorizontalAlignment.Left;
        head.TextTrimming = TextTrimming.CharacterEllipsis;
        var wide = w >= 440;
        var temp = Ui.Display(Ui.Text(Deg(r.Temp), wide ? Math.Min(96, h * 0.3) : Math.Min(80, h * 0.2), T.Text, T.TitleWeight));
        var range = $"H {Deg(r.High)}  L {Deg(r.Low)}";
        Control upper;
        if (wide)
        {
            var sky = Ui.V(4,
                Icons.Make(Glyph(r.Sky, r.Day), 54, T.Text, 1.6).Align(HorizontalAlignment.Right, VerticalAlignment.Top),
                Ui.Text(r.Condition, T.Caption, T.Text, FontWeight.SemiBold).Align(HorizontalAlignment.Right, VerticalAlignment.Top),
                Ui.Text(range, T.Caption, T.TextMuted).Align(HorizontalAlignment.Right, VerticalAlignment.Top));
            var g = Ui.Grid("*,Auto", "Auto");
            g.Children.Add(Ui.V(4, head, temp).Col(0));
            g.Children.Add(sky.Col(1));
            upper = g;
        }
        else
        {
            // Narrow: the temperature beside its icon, the words under them.
            head.MaxWidth = w - 56;
            upper = Ui.V(4, head,
                Ui.H(16, temp, Icons.Make(Glyph(r.Sky, r.Day), 50, T.Text, 1.6).Align(HorizontalAlignment.Left, VerticalAlignment.Center)),
                Ui.Text(r.Condition, T.Caption, T.Text, FontWeight.SemiBold),
                Ui.Text(range, T.Caption, T.TextMuted));
        }

        // The coming hours, as many as fit: "Now", then on the hour.
        var columns = (int)Math.Clamp(Math.Floor((w - 56) / 88), 3, 6);
        var hours = new Grid();
        var slots = new List<(string Label, double Temp, int Code, bool Day)> { ("Now", r.Temp, r.Code, r.Day) };
        slots.AddRange(r.Hours.Take(columns - 1).Select(x => (Hour(x.Time), x.Temp, x.Code, x.Day)));
        for (int i = 0; i < slots.Count; i++)
        {
            hours.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var (label, t, code, day) = slots[i];
            var cell = Ui.V(6,
                Ui.Text(label, T.Caption, T.TextMuted, FontWeight.Medium).Align(HorizontalAlignment.Center, VerticalAlignment.Top),
                Icons.Make(Glyph(WeatherCodes.Sky(code), day), 32, T.Text, 1.6).Align(HorizontalAlignment.Center, VerticalAlignment.Top),
                Ui.Text(Deg(t), T.Caption, T.Text, FontWeight.SemiBold).Align(HorizontalAlignment.Center, VerticalAlignment.Top));
            hours.Children.Add(cell.Col(i));
        }
        var layout = Ui.Grid("*", "Auto,*,Auto");
        layout.Children.Add(upper.Row(0));
        layout.Children.Add(hours.Row(2));
        return layout;
    }

    // ------------------------------------------------------------------ calendar

    private static Control CalendarCompact()
    {
        var now = DateTime.Now;
        var day = Ui.Display(Ui.Text(now.Day.ToString(CultureInfo.CurrentCulture), 72, T.Text, T.TitleWeight));
        day.VerticalAlignment = VerticalAlignment.Center;
        var words = Ui.V(2,
            Ui.Text(T.Header(now.ToString("dddd", CultureInfo.CurrentCulture)), T.Caption, T.Danger, FontWeight.Bold),
            Ui.Text(now.ToString("MMMM", CultureInfo.CurrentCulture), T.Body, T.Text, FontWeight.SemiBold),
            Ui.Text(now.Year.ToString(CultureInfo.CurrentCulture), T.Caption, T.TextMuted));
        words.VerticalAlignment = VerticalAlignment.Center;
        return Ui.H(18, day, words);
    }

    private static Control CalendarPanel(double w, double h)
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        var head = Ui.V(0,
            Ui.Text(T.Header(now.ToString("dddd", culture)), T.Caption, T.Danger, FontWeight.Bold),
            Ui.Text(now.ToString("d MMMM", culture), T.Body, T.Text, FontWeight.SemiBold));

        // The month: weekday initials, then the weeks, today ringed in the accent.
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        var start = new DateTime(now.Year, now.Month, 1);
        var lead = ((int)start.DayOfWeek - (int)first + 7) % 7;
        var days = DateTime.DaysInMonth(now.Year, now.Month);
        var weeks = (lead + days + 6) / 7;
        var cell = Math.Floor(Math.Min((w - 56) / 7, (h - 110) / (weeks + 1)));
        var font = Math.Max(T.Caption, Math.Min(T.Body, cell * 0.5));
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(cell, GridUnitType.Pixel));
        for (int r = 0; r <= weeks; r++) grid.RowDefinitions.Add(new RowDefinition(cell, GridUnitType.Pixel));
        for (int c = 0; c < 7; c++)
        {
            var name = culture.DateTimeFormat.GetShortestDayName((DayOfWeek)(((int)first + c) % 7));
            grid.Children.Add(Ui.Text(name, font * 0.85, T.TextFaint, FontWeight.SemiBold).Align(HorizontalAlignment.Center, VerticalAlignment.Center).Col(c).Row(0));
        }
        for (int d = 1; d <= days; d++)
        {
            var slot = lead + d - 1;
            var today = d == now.Day;
            var number = Ui.Text(d.ToString(culture), font, today ? T.OnAccent : T.Text, today ? FontWeight.Bold : FontWeight.Medium);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.VerticalAlignment = VerticalAlignment.Center;
            var mark = new Border
            {
                Width = cell - 4, Height = cell - 4,
                CornerRadius = new CornerRadius(T.Radius <= 4 ? T.Radius : (cell - 4) / 2),
                Background = today ? T.Accent : Brushes.Transparent,
                Child = number,
            };
            grid.Children.Add(mark.Col(slot % 7).Row(1 + slot / 7));
        }
        var layout = Ui.Grid("*", "Auto,*");
        layout.Children.Add(head.Row(0));
        layout.Children.Add(grid.Row(1));
        return layout;
    }

    // ------------------------------------------------------------------ the card

    /// <summary>A widget's card: the theme's glass, redrawn when the forecast changes and
    /// once a minute (the hour moves on, midnight turns the page) while it is on screen.</summary>
    private sealed class Live : Border
    {
        private readonly Func<Control> _render;
        private readonly bool _subscribe;
        private DispatcherTimer? _timer;

        public Live(double w, double h, IBrush? background, bool subscribe, Func<Control> render)
        {
            _render = render;
            _subscribe = subscribe;
            Width = w;
            Height = h;
            CornerRadius = new CornerRadius(T.RadiusLarge);
            Background = background ?? T.Surface;
            BorderBrush = Ui.Rim();
            BorderThickness = new Thickness(T.Def.LiquidGlass ? 1.5 : 1);
            Padding = new Thickness(h >= 240 ? 28 : 22, h >= 240 ? 24 : 16);
            ClipToBounds = true;
            IsHitTestVisible = false;
            Child = render();
        }

        private void Redraw() => Dispatcher.UIThread.Post(() => Child = _render());

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (_subscribe) L.Weather.Updated += Redraw;
            _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Child = _render());
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_subscribe) L.Weather.Updated -= Redraw;
            _timer?.Stop();
            _timer = null;
        }
    }
}
