using Lounge.Core.Settings;
using Lounge.Core.Weather;
using Xunit;

namespace Lounge.Tests;

public class WeatherTests
{
    // An Open-Meteo answer cut down to what Lounge asks for.
    private const string Forecast = """
        {"current":{"time":"2026-10-25T16:30","temperature_2m":79.1,"weather_code":2,"is_day":1},
         "hourly":{"time":["2026-10-25T15:00","2026-10-25T16:00","2026-10-25T17:00","2026-10-25T18:00","2026-10-25T19:00"],
                   "temperature_2m":[80.2,79.5,78.0,76.4,74.9],"weather_code":[1,2,61,63,3],"is_day":[1,1,1,1,0]},
         "daily":{"temperature_2m_max":[81.3,82.0],"temperature_2m_min":[69.2,70.1]}}
        """;

    [Fact]
    public void A_forecast_reads_now_today_and_the_hours_ahead()
    {
        var r = WeatherService.Parse(Forecast, "Miami", fahrenheit: true, new DateTime(2026, 10, 25, 16, 31, 0));
        Assert.Equal("Miami", r.Place);
        Assert.Equal(79.1, r.Temp);
        Assert.Equal(Sky.PartlyCloudy, r.Sky);
        Assert.Equal("Partly cloudy", r.Condition);
        Assert.Equal(81.3, r.High);
        Assert.Equal(69.2, r.Low);
        // Only the hours after the current reading.
        Assert.Equal([17, 18, 19], r.Hours.Select(h => h.Time.Hour));
        Assert.Equal(Sky.Rain, WeatherCodes.Sky(r.Hours[0].Code));
        Assert.False(r.Hours[2].Day);
    }

    [Fact]
    public void Weather_codes_map_to_every_sky()
    {
        Assert.Equal(Sky.Clear, WeatherCodes.Sky(0));
        Assert.Equal(Sky.Cloudy, WeatherCodes.Sky(3));
        Assert.Equal(Sky.Fog, WeatherCodes.Sky(45));
        Assert.Equal(Sky.Drizzle, WeatherCodes.Sky(53));
        Assert.Equal(Sky.Rain, WeatherCodes.Sky(81));
        Assert.Equal(Sky.Snow, WeatherCodes.Sky(73));
        Assert.Equal(Sky.Storm, WeatherCodes.Sky(95));
        Assert.True(WeatherService.FahrenheitFor("f"));
        Assert.False(WeatherService.FahrenheitFor("c"));
    }

    [Fact]
    public void Offline_service_shows_what_it_is_given_and_never_fetches()
    {
        var w = new WeatherService(null, null);
        var raised = 0;
        w.Updated += () => raised++;
        Assert.Null(w.Current);
        w.RefreshAsync("Miami", true).Wait();
        Assert.Null(w.Current);
        w.Show(WeatherService.Sample(DateTime.Now));
        Assert.Equal("Miami", w.Current?.Place);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_weather_city_is_typed_text_trimmed_with_automatic_when_empty()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(dir.File("settings.json"), dir.File("config.ini"));
        var d = SettingsSchema.Get(K.WeatherPlace);
        Assert.Equal(SettingKind.Text, d.Kind);
        Assert.Equal("Automatic", d.ShowValue(s.Get(K.WeatherPlace)));
        Assert.True(s.Set(K.WeatherPlace, "  Ankara "));
        Assert.Equal("Ankara", s.String(K.WeatherPlace));
        Assert.Equal("Ankara", new SettingsStore(dir.File("settings.json"), dir.File("config.ini")).String(K.WeatherPlace));
        Assert.True(s.Bool(K.ShowWeather) && s.Bool(K.ShowCalendar));
    }
}
