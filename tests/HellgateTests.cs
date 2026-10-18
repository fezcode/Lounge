using System.Numerics;
using Lounge.Core.Games;
using Xunit;

namespace Lounge.Tests;

public class HellgateTests
{
    private static readonly HellgateInput Idle = new(0, 0, 0, false, false, false, false);

    private static HellgateGame Started(int seed = 5, int level = 1)
    {
        var g = new HellgateGame(seed);
        g.Start(level);
        return g;
    }

    private static void Run(HellgateGame g, float seconds, HellgateInput? input = null)
    {
        for (var t = 0f; t < seconds; t += 1 / 60f) g.Update(1 / 60f, input ?? Idle);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    public void Every_level_has_a_reachable_exit_a_safe_start_and_monsters(int level)
    {
        var g = Started(level: level);
        Assert.False(g.Solid((int)g.Player.X, (int)g.Player.Y));
        Assert.NotEmpty(g.Monsters);
        // The exit switch can be walked to (doors count as passable: you open them).
        var exit = Enumerable.Range(0, g.MapW).SelectMany(x => Enumerable.Range(0, g.MapH).Select(y => (x, y))).Single(c => g.Map[c.x, c.y] == HellgateMap.ExitCell);
        var seen = new HashSet<(int, int)>();
        var q = new Queue<(int X, int Y)>();
        q.Enqueue(((int)g.Player.X, (int)g.Player.Y));
        var reached = false;
        while (q.Count > 0 && !reached)
        {
            var (x, y) = q.Dequeue();
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = (x + dx, y + dy);
                if (n == exit) { reached = true; break; }
                if (n.Item1 < 0 || n.Item2 < 0 || n.Item1 >= g.MapW || n.Item2 >= g.MapH || !seen.Add(n)) continue;
                if (g.Map[n.Item1, n.Item2] is 0 or HellgateMap.DoorCell) q.Enqueue(n);
            }
        }
        Assert.True(reached, $"level {level}: the exit can't be reached");
        // Nobody spawns inside a wall.
        Assert.All(g.Monsters, m => Assert.False(g.Solid((int)m.P.X, (int)m.P.Y)));
    }

    [Fact]
    public void Later_levels_are_bigger_busier_and_tougher()
    {
        var one = Started(level: 1);
        var six = Started(level: 6);
        Assert.True(six.MapW > one.MapW);
        Assert.True(six.Monsters.Count > one.Monsters.Count);
        Assert.Contains(six.Monsters, m => m.Kind == MonsterKind.Brute);
        Assert.DoesNotContain(one.Monsters, m => m.Kind == MonsterKind.Brute);
        Assert.True(six.Toughness > one.Toughness);
    }

    [Fact]
    public void The_pistol_kills_an_imp_in_front_and_uses_ammo()
    {
        var g = Started();
        g.Monsters.Clear();
        var imp = g.SpawnAhead(MonsterKind.Imp, 2.5f);
        var ammo = g.Bullets;
        Run(g, 3, new HellgateInput(0, 0, 0, true, false, false, false));
        Assert.True(imp.Dead);
        Assert.Equal(1, g.Kills);
        Assert.True(g.Bullets < ammo);
    }

    [Fact]
    public void Walls_stop_the_player_and_bullets()
    {
        var g = Started();
        Run(g, 8, new HellgateInput(1, 0, 0, false, false, false, false));   // run straight ahead for a while
        Assert.False(g.Solid((int)g.Player.X, (int)g.Player.Y));
        Assert.True(g.CastRay(g.Player, g.Angle) < 1);                     // up against a wall
    }

    [Fact]
    public void An_awake_imp_hurts_the_player_and_dying_offers_the_level_again()
    {
        var g = Started();
        g.Monsters.Clear();
        var imp = g.SpawnAhead(MonsterKind.Imp, 3);
        imp.Cooldown = 0;
        Run(g, 1.5f);
        Assert.True(g.Health < 100, "the imp's fireball should have landed");
        Run(g, 60);
        Assert.Equal(HellgatePhase.Dead, g.Phase);
        g.Update(0.05f, Idle);
        g.Update(0.05f, new HellgateInput(0, 0, 0, false, false, false, true));
        Assert.Equal(HellgatePhase.Playing, g.Phase);
        Assert.Equal(100, g.Health);
        Assert.Equal(1, g.Level);
    }

    [Fact]
    public void The_exit_switch_ends_the_level_and_the_next_one_follows()
    {
        var g = Started();
        g.FaceExit();
        g.Update(1 / 60f, new HellgateInput(0, 0, 0, false, true, false, false));
        Assert.Equal(HellgatePhase.LevelDone, g.Phase);
        Assert.Equal(2, g.BestLevel);
        g.Update(0.05f, Idle);
        g.Update(0.05f, new HellgateInput(0, 0, 0, false, false, false, true));
        Assert.Equal(HellgatePhase.Playing, g.Phase);
        Assert.Equal(2, g.Level);
    }

    [Fact]
    public void The_renderer_draws_a_full_frame()
    {
        var g = Started();
        var r = new HellgateRenderer();
        r.Draw(g);
        Assert.DoesNotContain(r.Pixels, p => p == 0);   // every pixel written
        Assert.True(r.Pixels.Distinct().Count() > 50);   // walls, floor, ceiling, weapon
    }
}
