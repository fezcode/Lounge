using Lounge.Core.Input;
using Xunit;

namespace Lounge.Tests;

public class RouterTests
{
    private sealed class Rig
    {
        public readonly ActionRouter Router;
        public readonly List<ActionEvent> Events = new();
        public long Now;

        public Rig(string context = "Shell", Keymap? keymap = null)
        {
            Router = new ActionRouter(keymap ?? Keymap.Defaults()) { Context = context };
            Router.Fired += Events.Add;
            Pad(PadButton.None);
        }

        public void Pad(PadButton b, float lt = 0, float rt = 0, float lx = 0, float ly = 0) =>
            Router.Update(new PadState(true, b, lt, rt, lx, ly), Now);

        public void Advance(long ms, PadButton held, int step = 16, float rt = 0)
        {
            for (long t = 0; t < ms; t += step) { Now += step; Pad(held, rt: rt); }
        }

        public List<string> Names => Events.Select(e => e.Action).ToList();
    }

    [Fact]
    public void Empty_controller_slots_are_probed_first_and_then_every_second()
    {
        Assert.True(XInputPad.ShouldProbe(null, 0));            // the very first read
        Assert.True(XInputPad.ShouldProbe(null, long.MaxValue));
        Assert.False(XInputPad.ShouldProbe(5_000, 5_500));
        Assert.True(XInputPad.ShouldProbe(5_000, 6_000));
    }

    [Fact]
    public void Plain_press_fires_immediately()
    {
        var r = new Rig();
        r.Pad(PadButton.DPadUp);
        Assert.Equal([Act.Up], r.Names);
    }

    [Fact]
    public void On_menus_A_selects_on_release_and_holding_it_moves_a_tile()
    {
        var r = new Rig();
        r.Pad(PadButton.A);
        Assert.Empty(r.Events);
        r.Pad(PadButton.None);
        Assert.Equal([Act.Accept], r.Names);
        r.Events.Clear();
        r.Pad(PadButton.A);
        r.Advance(700, PadButton.A);
        r.Pad(PadButton.None);
        Assert.Equal([Act.MoveMode], r.Names);
    }

    [Fact]
    public void While_watching_A_still_pauses_at_once()
    {
        var r = new Rig("Player");
        r.Pad(PadButton.A);
        Assert.Equal([Act.PlayPause], r.Names);
    }

    [Fact]
    public void View_tap_opens_menu_on_release_only()
    {
        var r = new Rig();
        r.Pad(PadButton.View);
        Assert.Empty(r.Events);
        r.Advance(100, PadButton.View);
        r.Pad(PadButton.None);
        Assert.Equal([Act.ContextMenu], r.Names);
    }

    [Fact]
    public void View_hold_goes_home_and_suppresses_the_tap()
    {
        var r = new Rig();
        r.Pad(PadButton.View);
        r.Advance(700, PadButton.View);
        r.Pad(PadButton.None);
        Assert.Equal([Act.Home], r.Names);
    }

    [Fact]
    public void Chord_suppresses_both_single_buttons()
    {
        var r = new Rig("Dormant");
        r.Router.Options.ReturnHoldMs = 1000;
        r.Pad(PadButton.View);
        r.Pad(PadButton.View | PadButton.Menu);
        r.Advance(1100, PadButton.View | PadButton.Menu);
        r.Pad(PadButton.None);
        Assert.Equal([Act.Return], r.Names);
    }

    [Fact]
    public void Dormant_ignores_everything_but_the_return_chord()
    {
        var r = new Rig("Dormant");
        foreach (var b in new[] { PadButton.A, PadButton.B, PadButton.Menu, PadButton.RB, PadButton.DPadUp })
        {
            r.Pad(b); r.Advance(50, b); r.Pad(PadButton.None);
        }
        r.Pad(PadButton.View); r.Advance(2000, PadButton.View); r.Pad(PadButton.None);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Return_chord_short_of_its_hold_time_does_nothing()
    {
        var r = new Rig("Dormant");
        r.Router.Options.ReturnHoldMs = 1000;
        r.Pad(PadButton.View | PadButton.Menu);
        r.Advance(600, PadButton.View | PadButton.Menu);
        r.Pad(PadButton.None);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Held_direction_repeats_after_delay_then_at_rate()
    {
        var r = new Rig();
        r.Router.Options.RepeatDelayMs = 400;
        r.Router.Options.RepeatRateMs = 100;
        r.Pad(PadButton.DPadDown);
        r.Advance(390, PadButton.DPadDown, step: 10);
        Assert.Single(r.Events);
        r.Advance(320, PadButton.DPadDown, step: 10);
        Assert.Equal(5, r.Events.Count);   // press + repeats at 400, 500, 600, 700
        Assert.All(r.Events.Skip(1), e => Assert.Equal(ActionPhase.Repeated, e.Phase));
    }

    [Fact]
    public void Accept_does_not_repeat()
    {
        var r = new Rig("Files");
        r.Pad(PadButton.A);
        r.Advance(2000, PadButton.A);
        Assert.Single(r.Events);
    }

    [Fact]
    public void Player_triggers_report_pressure_and_release()
    {
        var r = new Rig("Player");
        r.Pad(PadButton.None, rt: 0.5f);
        r.Pad(PadButton.None, rt: 1.0f);
        r.Pad(PadButton.None, rt: 0.0f);
        Assert.Equal([ActionPhase.Pressed, ActionPhase.Changed, ActionPhase.Released], r.Events.Select(e => e.Phase).ToList());
        Assert.All(r.Events, e => Assert.Equal(Act.SeekForward, e.Action));
        Assert.Equal(1.0f, r.Events[1].Value, 3);
    }

    [Fact]
    public void Player_trigger_shadows_inherited_section_jump()
    {
        var r = new Rig("Player");
        r.Pad(PadButton.None, rt: 1f);
        Assert.DoesNotContain(Act.NextSection, r.Names);
    }

    [Fact]
    public void Player_inherits_global_volume()
    {
        var r = new Rig("Player");
        r.Pad(PadButton.RB);
        Assert.Equal([Act.VolumeUp], r.Names);
    }

    [Fact]
    public void Keyboard_context_overrides_volume_with_cursor()
    {
        var r = new Rig("Keyboard");
        r.Pad(PadButton.RB);
        Assert.Equal([Act.KeyCursorRight], r.Names);
    }

    [Fact]
    public void Button_held_across_a_context_switch_does_not_fire_in_the_new_context()
    {
        var r = new Rig("Files");
        r.Pad(PadButton.A);                     // Accept in Files opens the player...
        r.Router.Context = "Player";
        r.Advance(200, PadButton.A);
        r.Pad(PadButton.None);                  // ...and releasing A must not pause it
        Assert.Equal([Act.Accept], r.Names);
    }

    [Fact]
    public void Disconnect_ends_analog_and_raises_connection_change()
    {
        var r = new Rig("Player");
        var changes = new List<bool>();
        r.Router.ConnectionChanged += changes.Add;
        r.Pad(PadButton.None, rt: 1f);
        r.Router.Update(PadState.Disconnected, r.Now);
        Assert.Equal([false], changes);
        Assert.Equal(ActionPhase.Released, r.Events.Last().Phase);
        r.Pad(PadButton.None);
        Assert.Equal([false, true], changes);
    }

    [Fact]
    public void Stick_direction_navigates_once_past_deadzone()
    {
        var r = new Rig();
        r.Pad(PadButton.None, ly: 0.1f);
        Assert.Empty(r.Events);
        r.Pad(PadButton.None, ly: 0.9f);
        Assert.Equal([Act.Up], r.Names);
    }

    [Fact]
    public void Capture_reports_a_chord_hold()
    {
        var r = new Rig();
        CaptureResult? got = null;
        r.Router.BeginCapture(c => got = c);
        r.Pad(PadButton.LB);
        r.Pad(PadButton.LB | PadButton.Y);
        r.Advance(700, PadButton.LB | PadButton.Y);
        r.Pad(PadButton.None);
        Assert.NotNull(got);
        Assert.False(got!.Value.Cancelled);
        Assert.Equal(PadButton.LB | PadButton.Y, got.Value.Binding.Buttons);
        Assert.Equal(Gesture.Hold, got.Value.Binding.Gesture);
        Assert.Empty(r.Events);
    }

    [Fact]
    public void Capture_double_B_cancels_single_B_binds_B()
    {
        var r = new Rig();
        CaptureResult? got = null;
        r.Router.BeginCapture(c => got = c);
        r.Pad(PadButton.B); r.Pad(PadButton.None);
        r.Now += 100; r.Pad(PadButton.B); r.Pad(PadButton.None);
        Assert.True(got!.Value.Cancelled);

        got = null;
        r.Router.BeginCapture(c => got = c);
        r.Now += 10; r.Pad(PadButton.B); r.Pad(PadButton.None);
        Assert.Null(got);
        r.Advance(700, PadButton.None);
        Assert.False(got!.Value.Cancelled);
        Assert.Equal(PadButton.B, got.Value.Binding.Buttons);
    }

    [Fact]
    public void Remapped_button_takes_effect_immediately()
    {
        var k = Keymap.Defaults();
        var r = new Rig("Player", k);
        k.Rebind("Player", Act.PlayPause, new InputBinding(PadButton.A, Gesture.Press),
            new InputBinding(PadButton.X, Gesture.Press), ConflictMode.Swap);
        r.Pad(PadButton.X); r.Pad(PadButton.None);
        r.Pad(PadButton.A); r.Pad(PadButton.None);
        Assert.Equal([Act.PlayPause, Act.Info], r.Names);
    }
}
