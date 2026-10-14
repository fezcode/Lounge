using Lounge.Core.Input;
using Lounge.Core.Launch;
using Xunit;

namespace Lounge.Tests;

public class AgentTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 21, 0, 0);

    [Fact]
    public void Asks_only_when_every_rule_allows()
    {
        Assert.True(AgentPolicy.ShouldAsk(true, Now, DateTime.MinValue, false, "notFullscreen", false));
        Assert.False(AgentPolicy.ShouldAsk(false, Now, DateTime.MinValue, false, "always", false));          // turned off
        Assert.False(AgentPolicy.ShouldAsk(true, Now, Now.AddMinutes(5), false, "always", false));          // quiet after "No"
        Assert.True(AgentPolicy.ShouldAsk(true, Now, Now.AddMinutes(-1), false, "always", false));          // quiet period over
        Assert.False(AgentPolicy.ShouldAsk(true, Now, DateTime.MinValue, true, "always", false));           // Lounge already open
        Assert.False(AgentPolicy.ShouldAsk(true, Now, DateTime.MinValue, false, "notFullscreen", true));    // a game is full screen
        Assert.True(AgentPolicy.ShouldAsk(true, Now, DateTime.MinValue, false, "always", true));            // ...unless told to always ask
    }

    [Fact]
    public void Only_a_new_controller_counts_as_arriving()
    {
        Assert.True(AgentPolicy.ControllerArrived(0, 1));
        Assert.True(AgentPolicy.ControllerArrived(1, 2));
        Assert.False(AgentPolicy.ControllerArrived(1, 1));
        Assert.False(AgentPolicy.ControllerArrived(2, 1));
    }

    [Fact]
    public void A_is_yes_and_B_is_no()
    {
        var yes = new PromptAnswer(PadButton.None);
        Assert.Null(yes.Update(PadButton.DPadUp));
        Assert.True(yes.Update(PadButton.DPadUp | PadButton.A));
        var no = new PromptAnswer(PadButton.None);
        Assert.False(no.Update(PadButton.B));
    }

    [Fact]
    public void The_press_that_woke_the_pad_does_not_answer()
    {
        // The Xbox button and A are held while the controller connects.
        var p = new PromptAnswer(PadButton.A | PadButton.Guide);
        Assert.Null(p.Update(PadButton.A));
        Assert.Null(p.Update(PadButton.A | PadButton.B));   // still not armed: nothing was released yet
        Assert.Null(p.Update(PadButton.None));
        Assert.True(p.Update(PadButton.A));
    }
}
