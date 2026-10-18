using Lounge.Core.Launch;
using Xunit;

namespace Lounge.Tests;

public class TileMigrationTests
{
    [Fact]
    public void Hellgate_tiles_from_1_0_20_are_removed_once_and_Starfall_stays()
    {
        using var dir = new TempDir();
        var path = dir.File("tiles.json");
        // A 1.0.20 setup: Starfall and Hellgate side by side, every earlier migration done.
        var saved = TileStore.Defaults();
        saved.Insert(saved.FindIndex(t => t.Id == "starfall") + 1,
            new Tile { Id = "hellgate", Kind = TileKind.Builtin, Name = "Hellgate", Target = "hellgate", IsGame = true });
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(saved, new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        File.WriteAllLines(Path.ChangeExtension(path, ".migrations"), ["logos", "prime-inside", "hbomax", "starfall", "hellgate"]);

        var store = new TileStore(path);
        Assert.Null(store.Find("hellgate"));
        Assert.NotNull(store.Find("starfall"));
        Assert.DoesNotContain(TileStore.Defaults(), t => t.Target == "hellgate");
        Assert.Null(new TileStore(path).Find("hellgate"));
    }

    [Fact]
    public void The_browser_joins_the_end_of_the_websites_once_and_stays_removed()
    {
        using var dir = new TempDir();
        var path = dir.File("tiles.json");
        // A 1.0.21 setup: no browser yet, every earlier migration done.
        var saved = TileStore.Defaults().Where(t => t.Id != "browser").ToList();
        var json = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(saved, json));
        File.WriteAllLines(Path.ChangeExtension(path, ".migrations"), ["logos", "prime-inside", "hbomax", "starfall", "hellgate-gone"]);

        var store = new TileStore(path);
        var browser = store.Find("browser");
        Assert.NotNull(browser);
        Assert.True(browser.IsBrowser && browser.InWebRow);
        Assert.Equal("browser", store.Web.Last().Id);
        Assert.DoesNotContain(store.Apps, t => t.Id == "browser");

        store.Remove("browser");
        Assert.Null(new TileStore(path).Find("browser"));
    }
}
