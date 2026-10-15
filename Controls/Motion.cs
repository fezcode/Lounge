using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lounge.Controls;

/// <summary>Spring-driven motion. Every animated value in Lounge is a spring: focus
/// scale, glow, scrolling, panels sliding in. Springs feel physical because they carry
/// velocity, so a quick second move blends instead of restarting.
/// <see cref="Enabled"/> off (previews, "reduce effects") snaps values to their target.</summary>
public static class Motion
{
    private static readonly List<Spring> Active = new();
    private static TopLevel? _top;
    private static bool _scheduled;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _last;
    private static DispatcherTimer? _fallback;

    public static bool Enabled { get; set; } = true;
    /// <summary>Animation speed multiplier from settings (0.5 = slower, 2 = faster).</summary>
    public static double Speed { get; set; } = 1;

    public static void Attach(TopLevel top) => _top = top;

    internal static void Run(Spring s)
    {
        if (!Enabled) { s.Snap(); return; }
        if (!Active.Contains(s)) Active.Add(s);
        Schedule();
    }

    private static void Schedule()
    {
        if (_scheduled) return;
        _scheduled = true;
        _last = Clock.Elapsed.TotalSeconds;
        if (_top is not null) _top.RequestAnimationFrame(_ => Tick());
        else
        {
            _fallback ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => { _fallback!.Stop(); Tick(); });
            _fallback.Start();
        }
    }

    /// <summary>Animations that threw and were dropped (checks assert this stays 0).</summary>
    public static int Failures { get; private set; }

    private static void Tick()
    {
        _scheduled = false;
        var now = Clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _last, 0.001, 0.05) * Speed;
        _last = now;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var s = Active[i];
            try
            {
                if (s.Step(dt)) Active.RemoveAt(i);
            }
            catch (Exception e)
            {
                // One bad animation must not stop the others: without this the next frame
                // was never scheduled and every animation froze mid-way.
                Active.RemoveAt(i);
                Failures++;
                Lounge.Shell.Log.Error("animation", e);
            }
        }
        if (Active.Count > 0) Schedule();
    }
}

/// <summary>A damped spring heading for <see cref="Target"/>. Calls back with each new value.</summary>
public sealed class Spring
{
    private readonly Action<double> _apply;
    public double Value { get; private set; }
    public double Target { get; private set; }
    public double Velocity { get; private set; }
    public double Stiffness { get; set; }
    public double Damping { get; set; }

    public Spring(double initial, Action<double> apply, double stiffness = 320, double damping = 26)
    {
        Value = Target = initial;
        _apply = apply;
        Stiffness = stiffness;
        Damping = damping;
        apply(initial);
    }

    public void To(double target)
    {
        Target = target;
        if (Math.Abs(Value - Target) < 1e-4 && Math.Abs(Velocity) < 1e-4) return;
        Motion.Run(this);
    }

    public void Snap(double? value = null)
    {
        if (value is { } v) Target = v;
        Value = Target;
        Velocity = 0;
        _apply(Value);
    }

    /// <summary>Advances the spring; returns true when it has settled.</summary>
    internal bool Step(double dt)
    {
        // Sub-step for stability with stiff springs.
        var steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        var h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            var force = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += force * h;
            Value += Velocity * h;
        }
        var settled = Math.Abs(Value - Target) < 0.0005 * Math.Max(1, Math.Abs(Target)) && Math.Abs(Velocity) < 0.01;
        if (settled) { Value = Target; Velocity = 0; }
        _apply(Value);
        return settled;
    }
}
