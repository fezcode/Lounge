namespace Lounge.Core.Input;

/// <summary>Every controller Lounge can read: XInput pads and HID gamepads. Each one goes
/// through its own button layout (from <see cref="Profiles"/>) into the Xbox layout, and
/// the results are merged, so several controllers drive Lounge at once, each with its
/// own buttons. A controller being set up drives nothing until its set-up ends.</summary>
public sealed class ControllerHub : IDisposable
{
    private readonly XInputPad _xinput = new();
    private readonly HidPads? _hid;
    private readonly Dictionary<string, ControllerProfile> _profiles = new();
    private List<(ControllerInfo Info, RawPad Raw)> _now = new();

    public ControllerProfiles Profiles { get; }

    /// <summary>Raised on the reading thread when controllers come or go.</summary>
    public event Action? ConnectedChanged;

    public ControllerHub(ControllerProfiles profiles, bool readHid = true)
    {
        Profiles = profiles;
        Profiles.Changed += () => _profiles.Clear();   // renamed, re-laid-out or forgotten: look it up again
        if (readHid) { _hid = new HidPads(); _hid.Start(); }
    }

    /// <summary>XInput only: Game Bar owns the Xbox button unless Lounge is asked to read it.</summary>
    public bool ReadGuide { get => _xinput.ReadGuide; set => _xinput.ReadGuide = value; }

    public int ConnectedCount => _now.Count;
    public IReadOnlyList<ControllerInfo> Connected => _now.Select(c => c.Info).ToList();

    /// <summary>The controller whose buttons are being set up; it drives nothing meanwhile.</summary>
    public string? CaptureId { get; set; }

    /// <summary>A connected controller's raw inputs as of the last read.</summary>
    public RawPad? RawOf(string id) => _now.FirstOrDefault(c => c.Info.Id == id).Raw;

    public static string XInputId(int vendorId, int productId, int slot) => $"xinput-{vendorId:x4}-{productId:x4}-{slot + 1}";

    /// <summary>All controllers, merged into one Xbox-layout state.</summary>
    public PadState Read(long nowMs)
    {
        var all = new List<(ControllerInfo, RawPad)>();
        foreach (var s in _xinput.ReadSlots(nowMs))
            all.Add((new ControllerInfo(XInputId(s.VendorId, s.ProductId, s.Index), $"{XInputPad.ModelName(s.VendorId, s.ProductId)} (slot {s.Index + 1})",
                ControllerKind.XInput, s.VendorId, s.ProductId), s.Raw));
        if (_hid is not null) all.AddRange(_hid.Snapshot());
        var changed = all.Count != _now.Count || all.Where((c, i) => c.Item1.Id != _now[i].Info.Id).Any();
        _now = all;
        if (changed) ConnectedChanged?.Invoke();

        if (all.Count == 0) return PadState.Disconnected;
        var states = new List<PadState>(all.Count);
        foreach (var (info, raw) in all)
        {
            if (!_profiles.TryGetValue(info.Id, out var profile)) _profiles[info.Id] = profile = Profiles.For(info);
            states.Add(info.Id == CaptureId ? new PadState(true, PadButton.None) : ControllerLayout.Apply(profile.Layout, raw));
        }
        return PadState.Merge(states);
    }

    public Task Pulse(float strength, int ms) => _xinput.Pulse(strength, ms);

    public void Dispose() => _hid?.Dispose();
}
