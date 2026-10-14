using System.Globalization;
using System.Text;

namespace Lounge.Core.Settings;

/// <summary>tivi's config.ini dialect: <c>key=value</c> lines, '#' comments, no sections.
/// Unknown keys survive a round trip so a newer file is never truncated.</summary>
public sealed class IniFile
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string Header { get; set; } = "# Lounge player settings (tivi config.ini format)";

    public static IniFile Parse(string text)
    {
        var ini = new IniFile();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#' || t[0] == ';') continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var key = t[..eq].Trim();
            var value = t[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            ini.Set(key, value);
        }
        return ini;
    }

    public static IniFile Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new IniFile();

    public IEnumerable<string> Keys => _order;

    public bool Contains(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value)
    {
        if (!_values.ContainsKey(key)) _order.Add(key);
        _values[key] = value;
    }

    public bool GetBool(string key, bool fallback)
    {
        var v = Get(key);
        if (v is null) return fallback;
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i != 0;
        if (bool.TryParse(v, out var b)) return b;
        return fallback;
    }

    public double GetDouble(string key, double fallback)
    {
        var v = Get(key);
        return v is not null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d : fallback;
    }

    public int GetInt(string key, int fallback)
    {
        var v = Get(key);
        return v is not null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
    }

    /// <summary>tivi writes colours as bare six-digit hex (<c>sub_color=ffffff</c>).</summary>
    public uint GetHex(string key, uint fallback)
    {
        var v = Get(key);
        return v is not null && uint.TryParse(v.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)
            ? u & 0xFFFFFF : fallback;
    }

    public void SetBool(string key, bool v) => Set(key, v ? "1" : "0");
    public void SetDouble(string key, double v, string format = "0.000") => Set(key, v.ToString(format, CultureInfo.InvariantCulture));
    public void SetInt(string key, int v) => Set(key, v.ToString(CultureInfo.InvariantCulture));
    public void SetHex(string key, uint v) => Set(key, (v & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture));

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var k in _order) sb.Append(k).Append('=').Append(_values[k]).Append('\n');
        return sb.ToString();
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, Serialize());
}
