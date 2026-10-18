using System.Numerics;
using Lounge.Core.Games;
using Xunit;

namespace Lounge.Tests;

public class StarfallTests
{
    private static readonly StarfallInput Idle = new(0, 0, false, false, false);
    private static readonly StarfallInput StartPress = new(0, 0, false, false, true);

    private static StarfallGame Started(int seed = 3)
    {
        var g = new StarfallGame(seed);
        g.Update(0.05f, Idle);         // the start button must be let go first
        g.Update(0.05f, StartPress);
        return g;
    }

    private static void Run(StarfallGame g, float seconds, Func<StarfallInput>? input = null)
    {
        for (var t = 0f; t < seconds; t += 1 / 60f) g.Update(1 / 60f, input?.Invoke() ?? Idle);
    }

    [Fact]
    public void Start_begins_stage_one_with_three_lives_and_bombs()
    {
        var g = new StarfallGame();
        Assert.Equal(StarfallPhase.Title, g.Phase);
        var started = Started();
        Assert.Equal(StarfallPhase.Playing, started.Phase);
        Assert.Equal(1, started.Stage);
        Assert.Equal(3, started.Lives);
        Assert.Equal(3, started.Bombs);
    }

    [Fact]
    public void Waves_arrive_shoot_and_can_be_shot_down()
    {
        var g = Started();
        Run(g, 25, g.Autopilot);
        Assert.True(g.Score > 0, "nothing was shot down in 25 s");
        Assert.True(g.Phase is StarfallPhase.Playing or StarfallPhase.GameOver);
        Run(g, 3);
        Assert.True(g.Bullets.Count > 0 || g.Enemies.Count > 0 || g.Phase == StarfallPhase.GameOver);
    }

    [Fact]
    public void Beating_the_boss_clears_the_stage_and_the_next_is_harder()
    {
        var g = Started();
        var d1 = g.Difficulty;
        // Clear the waves, then the boss arrives.
        for (var i = 0; i < 400 && g.Boss is null; i++) { g.DamageAll(1000); Run(g, 0.5f); }
        Assert.NotNull(g.Boss);
        Run(g, 4);                                   // it flies in and is briefly untouchable
        for (var i = 0; i < 20 && g.Phase == StarfallPhase.Playing; i++) { g.DamageAll(g.Boss!.MaxHp / 3 + 1); Run(g, 2); }
        Assert.Equal(StarfallPhase.StageClear, g.Phase);
        Run(g, 4.5f);
        Assert.Equal(StarfallPhase.Playing, g.Phase);
        Assert.Equal(2, g.Stage);
        Assert.True(g.Difficulty > d1);
    }

    [Fact]
    public void A_bullet_on_the_ship_costs_a_life_and_grants_a_moment_of_safety()
    {
        var g = Started();
        Run(g, 0.2f);
        g.Bullets.Add(new Bullet { P = g.Player, V = Vector2.Zero, R = 6 });
        Run(g, 0.05f);
        Assert.Equal(2, g.Lives);
        Assert.True(g.Invulnerable > 2);
        g.Bullets.Add(new Bullet { P = g.Player, V = Vector2.Zero, R = 6 });
        Run(g, 0.05f);
        Assert.Equal(2, g.Lives);                    // still invulnerable
    }

    [Fact]
    public void Losing_the_last_life_ends_the_game_and_keeps_the_high_score()
    {
        var g = Started();
        for (var i = 0; i < 3; i++)
        {
            Run(g, 3.2f);                            // wait out the invulnerability
            g.Bullets.Add(new Bullet { P = g.Player, V = Vector2.Zero, R = 6 });
            Run(g, 0.05f);
        }
        Assert.Equal(StarfallPhase.GameOver, g.Phase);
        Assert.True(g.HiScore >= g.Score);
        g.Update(0.05f, Idle);
        g.Update(0.05f, StartPress);                // A again plays again
        Assert.Equal(StarfallPhase.Playing, g.Phase);
        Assert.Equal(3, g.Lives);
    }

    [Fact]
    public void A_bomb_clears_every_bullet_and_is_spent()
    {
        var g = Started();
        Run(g, 0.2f);
        for (var i = 0; i < 50; i++) g.Bullets.Add(new Bullet { P = new(100 + i * 8, 200), V = new(0, 10), R = 6 });
        g.Update(1 / 60f, new StarfallInput(0, 0, false, true, false));
        Assert.Empty(g.Bullets);
        Assert.Equal(2, g.Bombs);
        g.Update(1 / 60f, new StarfallInput(0, 0, false, true, false));   // held: no second bomb
        Assert.Equal(2, g.Bombs);
    }

    [Fact]
    public void A_bullet_brushing_past_counts_as_a_graze()
    {
        var g = Started();
        Run(g, 0.2f);
        var at = g.Player + new Vector2(12, -200);
        g.Bullets.Add(new Bullet { P = at, V = new(0, 400), R = 5 });   // passes 12 units to the side
        Run(g, 1);
        Assert.Equal(3, g.Lives);
        Assert.Equal(1, g.Graze);
    }

    [Fact]
    public void Same_seed_same_game()
    {
        var a = Started(7);
        var b = Started(7);
        Run(a, 20, a.Autopilot);
        Run(b, 20, b.Autopilot);
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.Bullets.Count, b.Bullets.Count);
    }

    [Fact]
    public void Later_bosses_fill_the_screen_faster()
    {
        int BulletsAfterBossOpens(int stage)
        {
            var g = Started(11);
            g.SkipToStage(stage);
            for (var i = 0; i < 400 && g.Boss is null; i++) { g.DamageAll(1e9f); Run(g, 0.5f); }
            g.Bullets.Clear();
            Run(g, 4.5f);                             // fly in, then shoot for a moment
            var count = 0;
            for (var i = 0; i < 60; i++) { Run(g, 0.05f); count = Math.Max(count, g.Bullets.Count); }
            return count;
        }
        var early = BulletsAfterBossOpens(1);
        var late = BulletsAfterBossOpens(7);
        Assert.True(early > 20, $"stage 1 boss fired only {early}");
        Assert.True(late > early, $"stage 7 ({late}) should be denser than stage 1 ({early})");
    }
}
