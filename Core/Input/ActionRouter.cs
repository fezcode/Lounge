namespace Lounge.Core.Input;

public enum ActionPhase
{
    /// <summary>A press, tap, hold or chord completed; or an analog input started.</summary>
    Pressed,
    /// <summary>Auto-repeat while a repeatable press is held.</summary>
    Repeated,
    /// <summary>An analog input moved (trigger pressure, stick vector).</summary>
    Changed,
    /// <summary>An analog input went back to rest.</summary>
    Released,
}

public readonly record struct ActionEvent(string Action, ActionPhase Phase, float Value = 1, float X = 0, float Y = 0);

public sealed class RouterOptions
{
    public int HoldMs { get; set; } = 600;
    public int ReturnHoldMs { get; set; } = 1000;
    public int RepeatDelayMs { get; set; } = 380;
    public int RepeatRateMs { get; set; } = 90;
    public float Deadzone { get; set; } = 0.24f;
    public float TriggerThreshold { get; set; } = 0.12f;
    public int CaptureDoubleBMs { get; set; } = 600;
}

/// <summary>What the Remote screen gets back from a capture.</summary>
public readonly record struct CaptureResult(bool Cancelled, InputBinding Binding);

/// <summary>Turns controller snapshots into actions for the active context.
/// <list type="bullet">
/// <item>A press fires at once, unless the same button also starts a hold or a chord in
/// this context; then it fires on release, and only if the hold or chord did not.</item>
/// <item>Holds and chords consume their buttons, so View+Menu never also opens the menu.</item>
/// <item>Repeatable actions repeat after <see cref="RouterOptions.RepeatDelayMs"/>.</item>
/// <item>Analog bindings report pressure or stick vectors every update.</item>
/// <item>Switching context swallows whatever is still held, so a button pressed in one
/// screen never fires in the next.</item>
/// </list>
/// Time comes in as milliseconds so tests can drive it exactly.</summary>
public sealed class ActionRouter
{
    private readonly Keymap _keymap;
    private string _context = "Shell";
    private List<BoundAction>? _effective;

    private PadState _prev = PadState.Disconnected;
    private readonly Dictionary<PadButton, long> _downAt = new();
    private PadButton _consumed;
    private PadButton _pendingTap;
    private readonly Dictionary<PadButton, long> _nextRepeat = new();
    private long? _lastUpdate;
    /// <summary>A gap between updates longer than this means the UI thread was busy.</summary>
    public const int StallMs = 100;
    private readonly HashSet<BoundAction> _firedHolds = new();
    private readonly HashSet<BoundAction> _activeAnalog = new();

    private Action<CaptureResult>? _capture;
    private PadButton _captureSet;
    private long _captureLastPress;
    private bool _captureHeld;
    private long? _captureFirstB;

    public RouterOptions Options { get; } = new();

    public event Action<ActionEvent>? Fired;
    public event Action<bool>? ConnectionChanged;
    /// <summary>Any input at all, for idle timers.</summary>
    public event Action? Activity;

    public ActionRouter(Keymap keymap)
    {
        _keymap = keymap;
        _keymap.Changed += () => _effective = null;
    }

    public Keymap Keymap => _keymap;
    public bool Connected => _prev.Connected;
    public PadState Last => _prev;

    public string Context
    {
        get => _context;
        set
        {
            if (_context == value) return;
            EndAnalog();
            _context = value;
            _effective = null;
            _consumed |= _prev.Buttons;     // whatever is held belongs to the old screen
            _pendingTap = PadButton.None;
            _nextRepeat.Clear();
            _firedHolds.Clear();
        }
    }

    private List<BoundAction> Effective => _effective ??= Build(_context);

    private List<BoundAction> Build(string context)
    {
        var result = new List<BoundAction>();
        foreach (var c in Keymap.Chain(context))
            foreach (var b in _keymap.In(c))
                if (!result.Any(r => Shadows(r.Input, b.Input)))
                    result.Add(b);
        return result;
    }

    /// <summary>A more specific binding hides an inherited one with the same input; an
    /// analog binding also hides any inherited binding sharing its buttons (Player's RT
    /// seek replaces Global's RT section jump).</summary>
    private static bool Shadows(InputBinding specific, InputBinding inherited) =>
        specific.SameInput(inherited) ||
        ((specific.Gesture == Gesture.Analog || inherited.Gesture == Gesture.Analog) && (specific.Buttons & inherited.Buttons) != 0);

    public void BeginCapture(Action<CaptureResult> done)
    {
        _capture = done;
        _captureSet = PadButton.None;
        _captureHeld = false;
        _captureFirstB = null;
        _consumed |= _prev.Buttons;
    }

    public void CancelCapture() => _capture = null;

    public bool Capturing => _capture is not null;

    private int HoldFor(BoundAction b) =>
        b.Input.HoldMs ?? (b.Action == Act.Return ? Options.ReturnHoldMs : Options.HoldMs);

    public void Update(PadState raw, long now)
    {
        var state = raw.Connected ? raw.WithDerived(Options.Deadzone, Options.TriggerThreshold) : PadState.Disconnected;

        if (state.Connected != _prev.Connected)
        {
            if (!state.Connected) { EndAnalog(); ResetHeld(); }
            _prev = state.Connected ? _prev with { Connected = true } : PadState.Disconnected;
            ConnectionChanged?.Invoke(state.Connected);
            if (!state.Connected) return;
        }

        var pressed = state.Buttons & ~_prev.Buttons;
        var released = _prev.Buttons & ~state.Buttons;
        if (pressed != 0 || released != 0) Activity?.Invoke();

        foreach (var b in PadButtons.Split(pressed)) { _downAt[b] = now; _consumed &= ~b; }

        if (_capture is not null)
        {
            UpdateCapture(state, pressed, now);
            Finish(state, released);
            return;
        }

        var bindings = Effective;

        // Holds and chords, biggest first so View+Menu wins over View.
        var holds = bindings.Where(b => b.Input.Gesture == Gesture.Hold).OrderByDescending(b => PadButtons.Count(b.Input.Buttons)).ToList();
        foreach (var b in holds)
        {
            var buttons = b.Input.Buttons;
            if ((state.Buttons & buttons) != buttons || _firedHolds.Contains(b)) continue;
            if ((_consumed & buttons) != 0) continue;
            // Holding View + Menu must not fire "hold View" on the way: a hold waits while a
            // bigger held chord that contains it is still counting.
            if (holds.Any(big => big != b && (big.Input.Buttons & buttons) == buttons && big.Input.Buttons != buttons &&
                                 (state.Buttons & big.Input.Buttons) == big.Input.Buttons && !_firedHolds.Contains(big))) continue;
            var since = PadButtons.Split(buttons).Max(x => _downAt.GetValueOrDefault(x, now));
            if (now - since < HoldFor(b)) continue;
            _firedHolds.Add(b);
            _consumed |= buttons;
            _pendingTap &= ~buttons;
            foreach (var x in PadButtons.Split(buttons)) _nextRepeat.Remove(x);
            Raise(new ActionEvent(b.Action, ActionPhase.Pressed));
        }

        // Chords made of presses fire the moment the last button goes down.
        foreach (var b in bindings.Where(b => b.Input.Gesture == Gesture.Press && b.Input.IsChord))
        {
            var buttons = b.Input.Buttons;
            if ((state.Buttons & buttons) != buttons || (pressed & buttons) == 0 || (_consumed & buttons) != 0) continue;
            _consumed |= buttons;
            _pendingTap &= ~buttons;
            Raise(new ActionEvent(b.Action, ActionPhase.Pressed));
        }

        // Single presses.
        foreach (var btn in PadButtons.Split(pressed))
        {
            if ((_consumed & btn) != 0) continue;
            var bound = bindings.FirstOrDefault(b => b.Input.Gesture == Gesture.Press && b.Input.Buttons == btn);
            if (bound is null) continue;
            if (Deferred(bindings, btn)) { _pendingTap |= btn; continue; }
            Raise(new ActionEvent(bound.Action, ActionPhase.Pressed));
            if (Act.Describe(bound.Action).Repeats) _nextRepeat[btn] = now + Options.RepeatDelayMs;
        }

        // Auto-repeat. Repeats never catch up: after the UI was busy (a screen building,
        // pictures loading) the first frame back must not fire the repeats it missed one
        // per frame, or a single press jumps several items.
        var stalled = _lastUpdate is { } last && now - last > StallMs;
        _lastUpdate = now;
        foreach (var (btn, due) in _nextRepeat.ToList())
        {
            if ((state.Buttons & btn) == 0 || (_consumed & btn) != 0) { _nextRepeat.Remove(btn); continue; }
            if (stalled) { _nextRepeat[btn] = Math.Max(due, now + Options.RepeatRateMs); continue; }
            if (now < due) continue;
            var bound = bindings.FirstOrDefault(b => b.Input.Gesture == Gesture.Press && b.Input.Buttons == btn);
            if (bound is null) { _nextRepeat.Remove(btn); continue; }
            Raise(new ActionEvent(bound.Action, ActionPhase.Repeated));
            _nextRepeat[btn] = now + Options.RepeatRateMs;
        }

        // Taps fire on release when nothing bigger used the button.
        foreach (var btn in PadButtons.Split(released))
        {
            if ((_pendingTap & btn) == 0) continue;
            _pendingTap &= ~btn;
            if ((_consumed & btn) != 0) continue;
            var bound = bindings.FirstOrDefault(b => b.Input.Gesture == Gesture.Press && b.Input.Buttons == btn);
            if (bound is not null) Raise(new ActionEvent(bound.Action, ActionPhase.Pressed));
        }

        UpdateAnalog(bindings, raw, state);
        Finish(state, released);
    }

    private void Finish(PadState state, PadButton released)
    {
        foreach (var btn in PadButtons.Split(released))
        {
            _downAt.Remove(btn);
            _consumed &= ~btn;
            _nextRepeat.Remove(btn);
            _firedHolds.RemoveWhere(h => (h.Input.Buttons & btn) != 0);
        }
        _prev = state;
    }

    /// <summary>A press waits for release when the same button also starts a hold or
    /// belongs to a chord in this context.</summary>
    private static bool Deferred(List<BoundAction> bindings, PadButton btn) =>
        bindings.Any(b => b.Input.Gesture != Gesture.Analog && (b.Input.Buttons & btn) != 0 &&
                          (b.Input.Gesture == Gesture.Hold || b.Input.IsChord));

    private void UpdateAnalog(List<BoundAction> bindings, PadState raw, PadState state)
    {
        foreach (var b in bindings.Where(b => b.Input.Gesture == Gesture.Analog))
        {
            float value = 0, x = 0, y = 0;
            var buttons = b.Input.Buttons;
            if ((buttons & PadButton.RT) != 0) value = Rescale(raw.RightTrigger, Options.TriggerThreshold);
            else if ((buttons & PadButton.LT) != 0) value = Rescale(raw.LeftTrigger, Options.TriggerThreshold);
            else if ((buttons & (PadButton.LSUp | PadButton.LSDown | PadButton.LSLeft | PadButton.LSRight)) != 0)
                (value, x, y) = Stick(raw.LX, raw.LY, Options.Deadzone);
            else if ((buttons & (PadButton.RSUp | PadButton.RSDown | PadButton.RSLeft | PadButton.RSRight)) != 0)
                (value, x, y) = Stick(raw.RX, raw.RY, Options.Deadzone);

            var active = _activeAnalog.Contains(b);
            if (value > 0 && (_consumed & buttons & (PadButton.LT | PadButton.RT)) == 0)
            {
                Raise(new ActionEvent(b.Action, active ? ActionPhase.Changed : ActionPhase.Pressed, value, x, y));
                _activeAnalog.Add(b);
            }
            else if (active)
            {
                _activeAnalog.Remove(b);
                Raise(new ActionEvent(b.Action, ActionPhase.Released, 0));
            }
        }
    }

    /// <summary>0 below the threshold, then 0..1 across the remaining travel.</summary>
    public static float Rescale(float v, float threshold) =>
        v < threshold ? 0 : Math.Clamp((v - threshold) / (1 - threshold), 0.0001f, 1);

    private static (float, float, float) Stick(float x, float y, float dz)
    {
        var mag = MathF.Sqrt(x * x + y * y);
        if (mag < dz) return (0, 0, 0);
        var scaled = Math.Clamp((mag - dz) / (1 - dz), 0.0001f, 1);
        return (scaled, x / mag * scaled, y / mag * scaled);
    }

    private void EndAnalog()
    {
        foreach (var b in _activeAnalog.ToList()) Raise(new ActionEvent(b.Action, ActionPhase.Released, 0));
        _activeAnalog.Clear();
    }

    private void ResetHeld()
    {
        _downAt.Clear();
        _consumed = PadButton.None;
        _pendingTap = PadButton.None;
        _nextRepeat.Clear();
        _firedHolds.Clear();
    }

    private void UpdateCapture(PadState state, PadButton pressed, long now)
    {
        var fresh = state.Buttons & ~_consumed;
        if (pressed != 0) { _captureSet |= PadButtons.OneDirectionPerStick(_captureSet, fresh & pressed); _captureLastPress = now; }
        _captureSet |= PadButtons.OneDirectionPerStick(_captureSet, fresh);
        if (_captureSet != 0 && (state.Buttons & _captureSet) == _captureSet && now - _captureLastPress >= Options.HoldMs)
            _captureHeld = true;

        var allUp = (state.Buttons & ~_consumed) == 0;
        if (_captureSet != 0 && allUp)
        {
            var result = new InputBinding(_captureSet, _captureHeld ? Gesture.Hold : Gesture.Press);
            _captureSet = PadButton.None;
            _captureHeld = false;
            if (result is { Buttons: PadButton.B, Gesture: Gesture.Press })
            {
                if (_captureFirstB is { } first && now - first <= Options.CaptureDoubleBMs)
                {
                    CompleteCapture(new CaptureResult(true, default));
                    return;
                }
                _captureFirstB = now;
                return;
            }
            _captureFirstB = null;
            CompleteCapture(new CaptureResult(false, result));
            return;
        }

        if (_captureFirstB is { } firstB && _captureSet == 0 && now - firstB > Options.CaptureDoubleBMs)
            CompleteCapture(new CaptureResult(false, new InputBinding(PadButton.B, Gesture.Press)));
    }

    private void CompleteCapture(CaptureResult result)
    {
        var done = _capture;
        _capture = null;
        _captureFirstB = null;
        done?.Invoke(result);
    }

    private void Raise(ActionEvent e) => Fired?.Invoke(e);
}
