namespace Lounge.Core.Input;

public enum LearnOutcome { None, Assigned, Skipped, Stopped }

public readonly record struct LearnResult(LearnOutcome Outcome, SourceBinding? Binding = null);

/// <summary>Learns one input at a time from a single controller for its button layout:
/// press (and let go of) a button or push an axis to assign it; hold anything for
/// <see cref="SkipMs"/> to keep the step as it was; keep holding to <see cref="StopMs"/>
/// to stop. Driven by raw snapshots, so it works the same for every kind of controller.</summary>
public sealed class LayoutCapture
{
    public const int SkipMs = 1500, StopMs = 4000;
    private const float AxisOn = 0.6f, AxisOff = 0.3f;

    private Dictionary<string, float> _rest = new();
    private string? _active;
    private float _activeRest;
    private int _sign;
    private long _since;
    private bool _skipped;

    public LayoutCapture(RawPad untouched) => NextStep(untouched);

    /// <summary>How long the current input has been held, for a progress ring (0 when none).</summary>
    public long HeldMs(long now) => _active is null ? 0 : now - _since;

    /// <summary>Starts the next step. Axes are measured from where they sit now; an input
    /// still held from a skip carries over, so letting it go assigns nothing.</summary>
    public void NextStep(RawPad untouched)
    {
        var carried = _active is not null && _skipped ? _rest.GetValueOrDefault(_active) : (float?)null;
        _rest = new Dictionary<string, float>(untouched.Inputs);
        if (carried is { } r && _active is not null) _rest[_active] = r;
        if (!_skipped) _active = null;
    }

    public LearnResult Update(RawPad raw, long now)
    {
        if (_active is null)
        {
            string? best = null;
            float bestAmount = 0;
            foreach (var (input, v) in raw.Inputs)
            {
                float amount;
                if (RawPad.IsAxis(input)) amount = Math.Abs(v - _rest.GetValueOrDefault(input)) > AxisOn ? Math.Abs(v - _rest.GetValueOrDefault(input)) : 0;
                else amount = v > 0.5f ? 1 : 0;
                if (amount > bestAmount) { best = input; bestAmount = amount; }
            }
            if (best is null) return default;
            _active = best;
            _activeRest = RawPad.IsAxis(best) ? _rest.GetValueOrDefault(best) : 0;
            _sign = RawPad.IsAxis(best) ? Math.Sign(raw[best] - _activeRest) : 1;
            _since = now;
            _skipped = false;
            return default;
        }

        var held = RawPad.IsAxis(_active) ? Math.Abs(raw[_active] - _activeRest) > AxisOff : raw[_active] > 0.5f;
        if (held)
        {
            if (now - _since >= StopMs) { _active = null; _skipped = false; return new(LearnOutcome.Stopped); }
            if (!_skipped && now - _since >= SkipMs) { _skipped = true; return new(LearnOutcome.Skipped); }
            return default;
        }

        var source = _active;
        var wasSkip = _skipped;
        _active = null;
        _skipped = false;
        if (wasSkip) return default;
        return new(LearnOutcome.Assigned, RawPad.IsAxis(source) ? new SourceBinding(source, _sign == 0 ? 1 : _sign, _activeRest) : new SourceBinding(source));
    }
}
