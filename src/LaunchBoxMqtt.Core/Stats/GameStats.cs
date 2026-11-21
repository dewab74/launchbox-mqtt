using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LaunchBoxMqtt.Core.Stats;

/// <summary>RetroAchievements progress for one game, in one mode (hardcore or softcore).</summary>
public sealed record AchievementStats(
    string Source,
    string? Mode,
    int Unlocked,
    int Total,
    decimal? Points,
    decimal? PointsTotal,
    bool Beaten,
    bool Completed);

/// <summary>One line of a game's high score table.</summary>
public sealed record ScoreEntry(int Rank, decimal Score, string? Name);

/// <summary>
/// This cabinet's own best score for a game (MAME's hiscore plugin table, factory
/// defaults removed), with its top entries. Never an online leaderboard.
/// </summary>
public sealed record HighScore(decimal Score, string? Name, IReadOnlyList<ScoreEntry> Entries);

/// <summary>What is known about a game's stats. Either part may be missing (no source applies).</summary>
public sealed record GameStats(AchievementStats? Achievements, HighScore? HighScore)
{
    public bool IsEmpty => Achievements is null && HighScore is null;
}

/// <summary>Host-side lookup of a game's stats; the plugin implements it with LaunchBox's services.</summary>
public interface IGameStatsSource
{
    /// <summary>The game's stats, or null when nothing is known. May go to the network (rate limited by the host).</summary>
    Task<GameStats?> GetStatsAsync(string gameId, CancellationToken cancellationToken);
}

/// <summary>The retained JSON on the stats topic. Nulls stay explicit so templates can tell "not applicable".</summary>
public static class StatsPayload
{
    public static string Build(string gameId, string title, GameStats stats, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var root = new JsonObject
        {
            ["game_id"] = gameId,
            ["title"] = title,
            ["observed_at"] = observedAt.ToString("o", CultureInfo.InvariantCulture),
            ["achievements"] = stats.Achievements is { } a
                ? new JsonObject
                {
                    ["source"] = a.Source,
                    ["mode"] = a.Mode,
                    ["unlocked"] = a.Unlocked,
                    ["total"] = a.Total,
                    ["percent"] = a.Total > 0 ? Math.Round(100.0 * a.Unlocked / a.Total, 1) : 0,
                    ["points"] = a.Points,
                    ["points_total"] = a.PointsTotal,
                    ["beaten"] = a.Beaten,
                    ["completed"] = a.Completed,
                }
                : null,
            ["high_score"] = stats.HighScore is { } h
                ? new JsonObject
                {
                    ["score"] = h.Score,
                    ["name"] = h.Name,
                    ["entries"] = new JsonArray([.. h.Entries.Select(e => (JsonNode)new JsonObject
                    {
                        ["rank"] = e.Rank,
                        ["score"] = e.Score,
                        ["name"] = e.Name,
                    })]),
                }
                : null,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
