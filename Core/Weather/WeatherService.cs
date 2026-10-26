using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lounge.Core.Weather;

/// <summary>What the sky is doing, from a WMO weather code: enough to pick an icon and a word.</summary>
public enum Sky { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Storm }

public sealed record WeatherHour(DateTime Time, double Temp, int Code, bool Day);

/// <summary>A forecast as the widgets show it: now, today's high and low, the next hours.</summary>
public sealed record WeatherReport(
    string Place,
    double Temp,
    int Code,
    bool Day,
    double High,
    double Low,
    IReadOnlyList<WeatherHour> Hours,
    bool Fahrenheit,
    DateTime Fetched)
{
    public Sky Sky => WeatherCodes.Sky(Code);
    public string Condition => WeatherCodes.Describe(Code);
}

public static class WeatherCodes
{
    public static Sky Sky(int code) => code switch
    {
        0 or 1 => Weather.Sky.Clear,
        2 => Weather.Sky.PartlyCloudy,
        3 => Weather.Sky.Cloudy,
        45 or 48 => Weather.Sky.Fog,
        >= 51 and <= 57 => Weather.Sky.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => Weather.Sky.Rain,
        (>= 71 and <= 77) or 85 or 86 => Weather.Sky.Snow,
        >= 95 => Weather.Sky.Storm,
        _ => Weather.Sky.Cloudy,
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        61 or 80 => "Light rain",
        63 or 81 => "Rain",
        65 or 82 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 or 85 => "Light snow",
        73 or 75 or 86 => "Snow",
        77 => "Snow grains",
        >= 95 => "Thunderstorms",
        _ => "Cloudy",
    };
}

/// <summary>The weather for the home widgets, from Open-Meteo (free, no key). The place is
/// the city named in Settings, or with none named, the city the internet connection is in.
/// The last forecast is kept on disk so the widgets have something to show at start-up
/// and offline; it refreshes every half hour.</summary>
public sealed class WeatherService
{
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);

    private readonly string? _cacheFile;
    private readonly HttpClient? _http;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private string _key = "";

    /// <summary>A null cache file keeps everything in memory; a null client never goes
    /// online (previews and checks).</summary>
    public WeatherService(string? cacheFile, HttpClient? http)
    {
        _cacheFile = cacheFile;
        _http = http;
        Load();
    }

    public WeatherReport? Current { get; private set; }

    /// <summary>Why there is no forecast, in words for the widget; null while there is one.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised on a worker thread whenever the forecast or the problem changes.</summary>
    public event Action? Updated;

    /// <summary>Sets the forecast directly (previews, checks).</summary>
    public void Show(WeatherReport? report, string? problem = null)
    {
        Current = report;
        Problem = problem;
        Updated?.Invoke();
    }

    /// <summary>Fetches a new forecast when the one held is stale, or is for another place
    /// or unit. An empty place means "where the connection is".</summary>
    public async Task RefreshAsync(string place, bool fahrenheit, bool force = false)
    {
        if (_http is null) return;
        var key = $"{place.Trim().ToLowerInvariant()}|{(fahrenheit ? "f" : "c")}";
        if (!force && key == _key && Current is { } c && DateTime.Now - c.Fetched < Fresh) return;
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            var where = await Locate(place.Trim());
            if (where is null)
            {
                Problem = place.Trim().Length > 0 ? $"Couldn't find \"{place.Trim()}\"" : "Couldn't find where you are";
                if (key != _key) Current = null;
                Updated?.Invoke();
                return;
            }
            var url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={where.Value.Lat:0.####}&longitude={where.Value.Lon:0.####}") +
                "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day" +
                "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=2" +
                (fahrenheit ? "&temperature_unit=fahrenheit" : "");
            var json = await _http.GetStringAsync(url);
            Current = Parse(json, where.Value.Name, fahrenheit, DateTime.Now);
            Problem = null;
            _key = key;
            Save(key);
            Updated?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            // Offline or a bad answer: keep showing the last forecast, if it is for this place.
            if (key != _key) Current = null;
            Problem = Current is null ? "Weather isn't available right now" : null;
            Updated?.Invoke();
        }
        finally { _busy.Release(); }
    }

    private async Task<(double Lat, double Lon, string Name)?> Locate(string place)
    {
        if (place.Length == 0)
        {
            // No city named: the one the connection is in (BigDataCloud's free client lookup).
            var node = JsonNode.Parse(await _http!.GetStringAsync("https://api.bigdatacloud.net/data/reverse-geocode-client?localityLanguage=en"));
            if (node?["latitude"] is null || node["longitude"] is null) return null;
            var name = new[] { "city", "locality", "principalSubdivision", "countryName" }
                .Select(k => node[k]?.GetValue<string>()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "Here";
            return (node["latitude"]!.GetValue<double>(), node["longitude"]!.GetValue<double>(), name);
        }
        var found = JsonNode.Parse(await _http!.GetStringAsync(
            "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" + Uri.EscapeDataString(place)));
        if (found?["results"] is not JsonArray { Count: > 0 } results) return null;
        var r = results[0]!;
        return (r["latitude"]!.GetValue<double>(), r["longitude"]!.GetValue<double>(), r["name"]?.GetValue<string>() ?? place);
    }

    /// <summary>Reads Open-Meteo's forecast: the current reading, today's range, and the
    /// hours from now on.</summary>
    public static WeatherReport Parse(string json, string place, bool fahrenheit, DateTime fetched)
    {
        var root = JsonNode.Parse(json) ?? throw new JsonException("empty forecast");
        var current = root["current"]!;
        var nowText = current["time"]!.GetValue<string>();
        var now = DateTime.Parse(nowText, CultureInfo.InvariantCulture);
        var hourly = root["hourly"]!;
        var times = hourly["time"]!.AsArray();
        var temps = hourly["temperature_2m"]!.AsArray();
        var codes = hourly["weather_code"]!.AsArray();
        var days = hourly["is_day"]?.AsArray();
        var hours = new List<WeatherHour>();
        for (int i = 0; i < times.Count && hours.Count < 12; i++)
        {
            var t = DateTime.Parse(times[i]!.GetValue<string>(), CultureInfo.InvariantCulture);
            // The hours after the current one, on the hour.
            if (t <= now || temps[i] is null || codes[i] is null) continue;
            hours.Add(new WeatherHour(t, temps[i]!.GetValue<double>(), codes[i]!.GetValue<int>(), days?[i]?.GetValue<int>() != 0));
        }
        var daily = root["daily"]!;
        return new WeatherReport(
            place,
            current["temperature_2m"]!.GetValue<double>(),
            current["weather_code"]!.GetValue<int>(),
            current["is_day"]?.GetValue<int>() != 0,
            daily["temperature_2m_max"]![0]!.GetValue<double>(),
            daily["temperature_2m_min"]![0]!.GetValue<double>(),
            hours,
            fahrenheit,
            fetched);
    }

    /// <summary>Fahrenheit where the region measures in it (the US and a few others).</summary>
    public static bool FahrenheitFor(string units) => units switch
    {
        "f" => true,
        "c" => false,
        _ => !RegionInfo.CurrentRegion.IsMetric,
    };

    /// <summary>A believable forecast for previews and checks, which never go online.</summary>
    public static WeatherReport Sample(DateTime now)
    {
        var start = now.Date.AddHours(now.Hour + 1);
        int[] codes = [61, 61, 3, 2, 2, 1, 0, 0, 1, 2, 3, 3];
        double[] temps = [70, 71, 74, 72, 77, 76, 74, 73, 71, 70, 69, 68];
        var hours = Enumerable.Range(0, 12).Select(i => new WeatherHour(start.AddHours(i), temps[i], codes[i], start.AddHours(i).Hour is >= 7 and < 19)).ToList();
        return new WeatherReport("Miami", 70, 61, true, 80, 69, hours, true, now);
    }

    private sealed record Saved(string Key, WeatherReport Report);

    private void Load()
    {
        if (_cacheFile is null || !File.Exists(_cacheFile)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_cacheFile));
            if (saved?.Report is null) return;
            _key = saved.Key;
            Current = saved.Report;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    private void Save(string key)
    {
        if (_cacheFile is null || Current is null) return;
        try { AtomicFile.WriteAllText(_cacheFile, JsonSerializer.Serialize(new Saved(key, Current))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
