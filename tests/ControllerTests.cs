using Lounge.Core.Input;
using Xunit;

namespace Lounge.Tests;

public class ControllerTests
{
    private static RawPad Raw(params (string Source, float Value)[] inputs)
    {
        var r = new RawPad();
        foreach (var (s, v) in inputs) r.Inputs[s] = v;
        return r;
    }

    [Fact]
    public void An_xinput_pad_comes_through_its_default_layout_unchanged()
    {
        var s = ControllerLayout.Apply(ControllerLayout.XInputDefault(),
            Raw(("A", 1), ("DPadLeft", 1), ("Menu", 1), ("lx", -0.5f), ("ly", 0.8f), ("rt", 0.7f), ("B", 0)));
        Assert.Equal(PadButton.A | PadButton.DPadLeft | PadButton.Menu, s.Buttons);
        Assert.Equal(-0.5f, s.LX, 3);
        Assert.Equal(0.8f, s.LY, 3);
        Assert.Equal(0.7f, s.RightTrigger, 3);
        Assert.Equal(0f, s.LeftTrigger);
    }

    [Fact]
    public void A_switch_pro_pad_maps_by_position_hat_and_inverted_y()
    {
        var layout = ControllerLayout.HidDefault(0x057E);
        // Nintendo's B is the bottom face button (Xbox A); its hat is the D-pad; HID Y grows downwards.
        var s = ControllerLayout.Apply(layout, Raw(("b1", 1), ("hat:up", 1), ("y", -1f), ("x", 0.25f), ("b7", 1)));
        Assert.True(s.IsDown(PadButton.A));
        Assert.True(s.IsDown(PadButton.DPadUp));
        Assert.Equal(1f, s.LY, 3);                // pushed up
        Assert.Equal(0.25f, s.LX, 3);
        Assert.Equal(1f, s.LeftTrigger);          // ZL is a button, read as a full pull
    }

    [Fact]
    public void An_hid_trigger_axis_resting_at_minus_one_reads_zero_to_one()
    {
        var layout = new Dictionary<string, SourceBinding> { ["RT"] = new("ry", 1, -1) };
        Assert.Equal(0f, ControllerLayout.Apply(layout, Raw(("ry", -1))).RightTrigger, 3);
        Assert.Equal(0.5f, ControllerLayout.Apply(layout, Raw(("ry", 0))).RightTrigger, 3);
        Assert.Equal(1f, ControllerLayout.Apply(layout, Raw(("ry", 1))).RightTrigger, 3);
    }

    [Fact]
    public void A_d_pad_on_an_axis_counts_past_half_way()
    {
        var layout = new Dictionary<string, SourceBinding> { ["DPadLeft"] = new("x", -1), ["DPadRight"] = new("x", 1) };
        Assert.Equal(PadButton.DPadLeft, ControllerLayout.Apply(layout, Raw(("x", -1))).Buttons);
        Assert.Equal(PadButton.None, ControllerLayout.Apply(layout, Raw(("x", -0.3f))).Buttons);
    }

    [Fact]
    public void A_short_press_assigns_and_a_hold_skips_then_stops()
    {
        var idle = Raw(("b1", 0), ("b2", 0), ("x", 0));
        var c = new LayoutCapture(idle);
        Assert.Equal(LearnOutcome.None, c.Update(Raw(("b2", 1)), 0).Outcome);
        var r = c.Update(Raw(("b2", 0)), 200);
        Assert.Equal(LearnOutcome.Assigned, r.Outcome);
        Assert.Equal("b2", r.Binding!.Source);

        c.NextStep(idle);
        c.Update(Raw(("b1", 1)), 1000);
        Assert.Equal(LearnOutcome.Skipped, c.Update(Raw(("b1", 1)), 1000 + LayoutCapture.SkipMs).Outcome);
        c.NextStep(Raw(("b1", 1)));
        // Still held from the skip: letting go assigns nothing to the next step.
        Assert.Equal(LearnOutcome.None, c.Update(Raw(("b1", 0)), 3000).Outcome);

        c.NextStep(idle);
        c.Update(Raw(("b1", 1)), 4000);
        c.Update(Raw(("b1", 1)), 4000 + LayoutCapture.SkipMs);
        c.NextStep(Raw(("b1", 1)));
        Assert.Equal(LearnOutcome.Stopped, c.Update(Raw(("b1", 1)), 4000 + LayoutCapture.StopMs).Outcome);
    }

    [Fact]
    public void An_axis_is_learned_with_its_direction_and_rest()
    {
        var c = new LayoutCapture(Raw(("y", 0.02f), ("rz", -1f)));
        c.Update(Raw(("y", -0.9f), ("rz", -1f)), 0);
        var up = c.Update(Raw(("y", 0f), ("rz", -1f)), 300);
        Assert.Equal(new SourceBinding("y", -1, 0.02f), up.Binding);

        c.NextStep(Raw(("y", 0f), ("rz", -1f)));
        c.Update(Raw(("y", 0f), ("rz", 0.9f)), 1000);
        var trigger = c.Update(Raw(("y", 0f), ("rz", -1f)), 1300);
        Assert.Equal(new SourceBinding("rz", 1, -1f), trigger.Binding);
    }

    [Fact]
    public void Profiles_keep_each_controllers_id_name_and_layout()
    {
        using var dir = new TempDir();
        var path = dir.File("controllers.json");
        var store = new ControllerProfiles(path);
        var added = new List<string>();
        store.Added += p => added.Add(p.Id);
        var micro = new ControllerInfo("hid-2dc8-9020-abc123", "8BitDo Micro gamepad", ControllerKind.Hid, 0x2DC8, 0x9020);
        var xbox = new ControllerInfo(ControllerHub.XInputId(0x045E, 0x0B13, 0), "Xbox Wireless Controller (slot 1)", ControllerKind.XInput, 0x045E, 0x0B13);
        Assert.False(store.For(micro).Configured);    // an HID pad starts on a guess
        Assert.True(store.For(xbox).Configured);       // an Xbox pad is already right
        store.For(micro);
        Assert.Equal(new[] { micro.Id, xbox.Id }, added);   // each one announced once

        var layout = new Dictionary<string, SourceBinding>(store.Find(micro.Id)!.Layout) { ["A"] = new("b5") };
        store.SetLayout(micro.Id, layout);
        store.Rename(micro.Id, "Micro");

        var again = new ControllerProfiles(path).Find(micro.Id)!;
        Assert.Equal("Micro", again.Name);
        Assert.Equal(ControllerKind.Hid, again.Kind);
        Assert.Equal(0x2DC8, again.VendorId);
        Assert.True(again.Configured);
        Assert.Equal(new SourceBinding("b5"), again.Layout["A"]);

        store.Forget(micro.Id);
        Assert.Null(new ControllerProfiles(path).Find(micro.Id));
        Assert.Equal("xinput-045e-0b13-1", xbox.Id);
    }

    [Fact]
    public void Every_target_has_a_prompt_and_a_default_for_each_kind()
    {
        foreach (var t in ControllerLayout.Targets)
        {
            Assert.False(string.IsNullOrWhiteSpace(ControllerLayout.Prompt(t)));
            Assert.True(ControllerLayout.XInputDefault().ContainsKey(t), t);
            Assert.True(ControllerLayout.HidDefault(0).ContainsKey(t), t);
        }
    }
}
