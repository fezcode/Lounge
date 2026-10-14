using Lounge.Core.Input;

namespace Lounge.Core.Launch;

/// <summary>When LoungeAgent may ask "Would you like to go to Lounge?". Pure, so the
/// quiet rules are tested rather than trusted.</summary>
public static class AgentPolicy
{
    public static bool ShouldAsk(bool enabled, DateTime now, DateTime quietUntil, bool loungeRunning, string when, bool fullscreenAppInFront)
    {
        if (!enabled) return false;
        if (now < quietUntil) return false;
        if (loungeRunning) return false;
        if (when == "notFullscreen" && fullscreenAppInFront) return false;
        return true;
    }

    /// <summary>A controller arrived when there are more connected than last time.</summary>
    public static bool ControllerArrived(int before, int now) => now > before;
}

/// <summary>The prompt's answer from the pad: A is yes, B is no. Buttons already held
/// when it appears (the press that woke the controller) only count after a full release.</summary>
public sealed class PromptAnswer
{
    private PadButton _prev;
    private bool _armed;

    public PromptAnswer(PadButton heldAtStart)
    {
        _prev = heldAtStart;
        _armed = heldAtStart == PadButton.None;
    }

    /// <summary>true = yes, false = no, null = nothing decided yet.</summary>
    public bool? Update(PadButton buttons)
    {
        var pressed = buttons & ~_prev;
        _prev = buttons;
        if (buttons == PadButton.None) { _armed = true; return null; }
        if (!_armed) return null;
        if ((pressed & PadButton.A) != 0) return true;
        if ((pressed & PadButton.B) != 0) return false;
        return null;
    }
}
