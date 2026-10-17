using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lounge.Core.Input;

/// <summary>One controller Lounge has seen: its ID and name, and its button layout.</summary>
public sealed class ControllerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ControllerKind Kind { get; set; }
    public int VendorId { get; set; }
    public int ProductId { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>False while the layout is still Lounge's guess (an HID pad nobody set up).</summary>
    public bool Configured { get; set; }
    public Dictionary<string, SourceBinding> Layout { get; set; } = new();

    public ControllerInfo Info => new(Id, Name, Kind, VendorId, ProductId);
}

/// <summary>Every controller's saved layout, in controllers.json in Lounge's data folder.
/// A controller is filed under its ID the first time it is seen, with the default layout
/// for its kind, so its name and ID are kept even before it is set up.</summary>
public sealed class ControllerProfiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly string? _path;
    private readonly Dictionary<string, ControllerProfile> _byId;
    private readonly object _gate = new();

    public event Action? Changed;
    /// <summary>A controller seen for the first time.</summary>
    public event Action<ControllerProfile>? Added;

    public ControllerProfiles(string? path)
    {
        _path = path;
        _byId = Load().ToDictionary(p => p.Id);
    }

    public static ControllerProfiles InMemory() => new(null);

    public IReadOnlyList<ControllerProfile> All { get { lock (_gate) return _byId.Values.OrderByDescending(p => p.LastSeen).ToList(); } }

    public ControllerProfile? Find(string id) { lock (_gate) return _byId.GetValueOrDefault(id); }

    /// <summary>The profile for a connected controller, filed now if it is new.</summary>
    public ControllerProfile For(ControllerInfo info)
    {
        ControllerProfile? added = null;
        ControllerProfile p;
        lock (_gate)
        {
            if (!_byId.TryGetValue(info.Id, out p!))
            {
                p = new ControllerProfile
                {
                    Id = info.Id, Name = info.Name, Kind = info.Kind, VendorId = info.VendorId, ProductId = info.ProductId,
                    FirstSeen = DateTime.Now, LastSeen = DateTime.Now,
                    Configured = info.Kind == ControllerKind.XInput,
                    Layout = info.Kind == ControllerKind.XInput ? ControllerLayout.XInputDefault() : ControllerLayout.HidDefault(info.VendorId),
                };
                _byId[p.Id] = p;
                added = p;
            }
            else p.LastSeen = DateTime.Now;
        }
        if (added is not null) { Save(); Added?.Invoke(added); }
        return p;
    }

    public void SetLayout(string id, Dictionary<string, SourceBinding> layout)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var p)) return;
            p.Layout = new Dictionary<string, SourceBinding>(layout);
            p.Configured = true;
        }
        Save();
    }

    public void Rename(string id, string name)
    {
        lock (_gate) { if (_byId.TryGetValue(id, out var p) && !string.IsNullOrWhiteSpace(name)) p.Name = name.Trim(); }
        Save();
    }

    public void ResetLayout(string id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var p)) return;
            p.Layout = p.Kind == ControllerKind.XInput ? ControllerLayout.XInputDefault() : ControllerLayout.HidDefault(p.VendorId);
            p.Configured = p.Kind == ControllerKind.XInput;
        }
        Save();
    }

    public void Forget(string id)
    {
        lock (_gate) _byId.Remove(id);
        Save();
    }

    /// <summary>Writes controllers.json. Last-seen times change in memory on every read and
    /// are written with the next change or on exit.</summary>
    public void Save()
    {
        if (_path is not null)
        {
            string text;
            lock (_gate) text = JsonSerializer.Serialize(_byId.Values.OrderBy(p => p.FirstSeen).ToList(), Json);
            try { AtomicFile.WriteAllText(_path, text); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private List<ControllerProfile> Load()
    {
        if (_path is null || !File.Exists(_path)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<ControllerProfile>>(File.ReadAllText(_path), Json) ?? [];
            return list.Where(p => p.Id.Length > 0).GroupBy(p => p.Id).Select(g => g.First()).ToList();
        }
        catch (JsonException)
        {
            AtomicFile.SetAside(_path);
            return [];
        }
    }
}
