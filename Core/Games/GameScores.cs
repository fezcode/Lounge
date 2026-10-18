using System.Text.Json;

namespace Lounge.Core.Games;

/// <summary>High scores for Lounge's own games, in games.json in the data folder.</summary>
public static class GameScores
{
    private static readonly object Gate = new();
    private static string File => Path.Combine(AppPaths.Root, "games.json");

    public static long Get(string game)
    {
        lock (Gate) return Read().GetValueOrDefault(game);
    }

    /// <summary>Keeps the better of the stored score and <paramref name="score"/>.</summary>
    public static void Set(string game, long score)
    {
        lock (Gate)
        {
            var all = Read();
            if (all.GetValueOrDefault(game) >= score) return;
            all[game] = score;
            try { AtomicFile.WriteAllText(File, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Dictionary<string, long> Read()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Dictionary<string, long>>(System.IO.File.ReadAllText(File)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
}
