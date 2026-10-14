using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lounge.Core.Settings;

/// <summary>The one settings model every module reads. JSON settings live in
/// settings.json, player settings in config.ini (tivi's format). Values are validated
/// and clamped on the way in, every change is saved at once, and <see cref="Changed"/>
/// fires with the key so the UI can apply it live.</summary>
public sealed class SettingsStore
{
    private readonly string _jsonPath;
    private readonly string _iniPath;
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly JsonObject _unknown = new();
    private IniFile _ini = new();
    private readonly object _gate = new();

    /// <summary>Fires after a value changed and was saved. Argument: the key.</summary>
    public event Action<string>? Changed;

    /// <summary>A message for the user when the stored file could not be used
    /// (corrupt, or written by a newer Lounge). Null when all went well.</summary>
    public string? LoadNotice { get; private set; }

    /// <summary>When false, nothing is written (previews and tests that only read).</summary>
    public bool Persist { get; set; } = true;

    public SettingsStore(string jsonPath, string iniPath)
    {
        _jsonPath = jsonPath;
        _iniPath = iniPath;
        Load();
    }

    public static SettingsStore OpenDefault() => new(AppPaths.Settings, AppPaths.PlayerConfig);

    /// <summary>An in-memory store with defaults, for previews.</summary>
    public static SettingsStore InMemory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lounge-mem-" + Guid.NewGuid().ToString("N"));
        return new SettingsStore(Path.Combine(dir, "settings.json"), Path.Combine(dir, "config.ini")) { Persist = false };
    }

    private void Load()
    {
        foreach (var d in SettingsSchema.All)
            if (d.Kind != SettingKind.Action) _values[d.Key] = Clone(d.Default);

        if (File.Exists(_jsonPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(_jsonPath)) as JsonObject
                           ?? throw new JsonException("settings.json is not an object");
                var version = root["schemaVersion"]?.GetValue<int>() ?? 1;
                if (version > SettingsSchema.Version)
                {
                    var backup = $"{_jsonPath}.v{version}-backup";
                    File.Copy(_jsonPath, backup, overwrite: true);
                    LoadNotice = $"These settings came from a newer Lounge. A copy was kept as {Path.GetFileName(backup)}.";
                }
                if (root["values"] is JsonObject values)
                {
                    foreach (var (key, node) in values)
                    {
                        if (SettingsSchema.TryGet(key, out var d) && d.Store == SettingStore.Json)
                        {
                            if (TryRead(d, node, out var v)) _values[key] = v;
                        }
                        else if (node is not null)
                        {
                            _unknown[key] = node.DeepClone();
                        }
                    }
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                var moved = AtomicFile.SetAside(_jsonPath);
                LoadNotice = "Your settings file could not be read, so Lounge started with defaults." +
                             (moved is null ? "" : $" The old file was kept as {Path.GetFileName(moved)}.");
            }
        }

        _ini = IniFile.Load(_iniPath);
        foreach (var d in SettingsSchema.All.Where(d => d.Store == SettingStore.Ini))
        {
            if (!_ini.Contains(d.Key)) continue;
            _values[d.Key] = d.Kind switch
            {
                SettingKind.Toggle => _ini.GetBool(d.Key, (bool)d.Default!),
                SettingKind.Number => Clamp(d, _ini.GetDouble(d.Key, (double)d.Default!)),
                SettingKind.Integer => (int)Clamp(d, _ini.GetInt(d.Key, (int)d.Default!)),
                SettingKind.Choice when d.IniHex => _ini.GetHex(d.Key, uint.Parse((string)d.Default!, NumberStyles.HexNumber)).ToString("x6"),
                SettingKind.Choice => _ini.Get(d.Key) ?? (string)d.Default!,
                _ => _values[d.Key],
            };
        }
    }

    private static object? Clone(object? v) => v is string[] arr ? arr.ToArray() : v;

    private static double Clamp(SettingDescriptor d, double v) =>
        d.Max > d.Min ? Math.Clamp(v, d.Min, d.Max) : v;

    private static bool TryRead(SettingDescriptor d, JsonNode? node, out object? value)
    {
        value = null;
        if (node is null) return false;
        try
        {
            switch (d.Kind)
            {
                case SettingKind.Toggle: value = node.GetValue<bool>(); return true;
                case SettingKind.Integer: value = (int)Clamp(d, node.GetValue<double>()); return true;
                case SettingKind.Number: value = Clamp(d, node.GetValue<double>()); return true;
                case SettingKind.Choice:
                    var s = node.GetValue<string>();
                    if (d.Choices.Count > 0 && d.Choices.All(c => c.Value != s)) return false;
                    value = s; return true;
                case SettingKind.Path: value = node.GetValue<string>(); return true;
                case SettingKind.Folders:
                    if (node is not JsonArray a) return false;
                    value = a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>()
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return true;
                default: return false;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return false; }
    }

    // ---- typed reads

    public object? Get(string key) { lock (_gate) return _values.TryGetValue(key, out var v) ? Clone(v) : null; }
    public bool Bool(string key) => Get(key) is true;
    public int Int(string key) => Get(key) is int i ? i : Convert.ToInt32(Get(key) ?? 0, CultureInfo.InvariantCulture);
    public double Double(string key) => Convert.ToDouble(Get(key) ?? 0.0, CultureInfo.InvariantCulture);
    public string String(string key) => Get(key) as string ?? "";
    public IReadOnlyList<string> List(string key) => Get(key) as string[] ?? [];

    public bool IsDefault(string key)
    {
        var d = SettingsSchema.Get(key);
        var v = Get(key);
        return d.Kind == SettingKind.Folders
            ? List(key).Count == 0
            : Equals(v, d.Default);
    }

    // ---- writes

    /// <summary>Validates, clamps and stores a value, then saves and raises <see cref="Changed"/>.
    /// Returns false (and changes nothing) when the value is not acceptable.</summary>
    public bool Set(string key, object? value)
    {
        var d = SettingsSchema.Get(key);
        object? clean;
        switch (d.Kind)
        {
            case SettingKind.Toggle when value is bool b: clean = b; break;
            case SettingKind.Integer when value is IConvertible:
                clean = (int)Math.Round(Clamp(d, Convert.ToDouble(value, CultureInfo.InvariantCulture))); break;
            case SettingKind.Number when value is IConvertible:
                var n = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(n)) return false;
                clean = Math.Round(Clamp(d, n), 6);
                break;
            case SettingKind.Choice when value is string s && (d.Choices.Count == 0 || d.Choices.Any(c => c.Value == s)):
                clean = s; break;
            case SettingKind.Path when value is string p: clean = p; break;
            case SettingKind.Folders when value is IEnumerable<string> list:
                clean = list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); break;
            default: return false;
        }

        lock (_gate)
        {
            if (_values.TryGetValue(key, out var old) && SameValue(old, clean)) return true;
            _values[key] = clean;
            Save(d.Store);
        }
        Changed?.Invoke(key);
        return true;
    }

    private static bool SameValue(object? a, object? b) =>
        a is string[] x && b is string[] y ? x.SequenceEqual(y, StringComparer.OrdinalIgnoreCase) : Equals(a, b);

    /// <summary>Moves a stepped value (Integer/Number/Choice/Toggle) one notch.</summary>
    public void Step(string key, int direction)
    {
        var d = SettingsSchema.Get(key);
        switch (d.Kind)
        {
            case SettingKind.Toggle: Set(key, !Bool(key)); break;
            case SettingKind.Integer: Set(key, Int(key) + direction * (int)d.Step); break;
            case SettingKind.Number: Set(key, Math.Round(Double(key) + direction * d.Step, 6)); break;
            case SettingKind.Choice:
                var i = d.Choices.ToList().FindIndex(c => c.Value == String(key));
                var next = ((i < 0 ? 0 : i) + direction + d.Choices.Count) % d.Choices.Count;
                Set(key, d.Choices[next].Value);
                break;
        }
    }

    public void AddFolder(string key, string folder) => Set(key, List(key).Append(folder));
    public void RemoveFolder(string key, string folder) =>
        Set(key, List(key).Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));

    public void Reset(string key)
    {
        var d = SettingsSchema.Get(key);
        if (d.Kind == SettingKind.Action) return;
        Set(key, Clone(d.Default));
    }

    /// <summary>Every setting back to its default. Library folders are kept: they are
    /// the user's content, not a preference.</summary>
    public void ResetAll()
    {
        foreach (var d in SettingsSchema.All)
        {
            if (d.Kind is SettingKind.Action or SettingKind.Folders) continue;
            Reset(d.Key);
        }
    }

    private void Save(SettingStore which)
    {
        if (!Persist) return;
        if (which == SettingStore.Ini) SaveIni(); else SaveJson();
    }

    private void SaveJson()
    {
        var values = new JsonObject();
        foreach (var (k, v) in _unknown) values[k] = v?.DeepClone();
        foreach (var d in SettingsSchema.All.Where(d => d.Store == SettingStore.Json && d.Kind != SettingKind.Action))
        {
            values[d.Key] = _values[d.Key] switch
            {
                string[] arr => new JsonArray(arr.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                double x => JsonValue.Create(x),
                string s => JsonValue.Create(s),
                _ => null,
            };
        }
        var root = new JsonObject { ["schemaVersion"] = SettingsSchema.Version, ["values"] = values };
        AtomicFile.WriteAllText(_jsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SaveIni()
    {
        foreach (var d in SettingsSchema.All.Where(d => d.Store == SettingStore.Ini))
        {
            var v = _values[d.Key];
            switch (d.Kind)
            {
                case SettingKind.Toggle: _ini.SetBool(d.Key, v is true); break;
                case SettingKind.Integer: _ini.SetInt(d.Key, (int)v!); break;
                case SettingKind.Number: _ini.SetDouble(d.Key, (double)v!, d.Key is K.SubOutline or K.SubShadow ? "0.00" : "0.000"); break;
                case SettingKind.Choice when d.IniHex: _ini.SetHex(d.Key, uint.Parse((string)v!, NumberStyles.HexNumber)); break;
                case SettingKind.Choice: _ini.Set(d.Key, (string)v!); break;
            }
        }
        _ini.Save(_iniPath);
    }

    /// <summary>Writes both files now (first run, so the user sees every key).</summary>
    public void SaveAll()
    {
        if (!Persist) return;
        lock (_gate) { SaveJson(); SaveIni(); }
    }
}
