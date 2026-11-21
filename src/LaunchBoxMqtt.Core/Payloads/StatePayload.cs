using System.Globalization;
using System.Text.Json.Nodes;
using LaunchBoxMqtt.Core.State;

namespace LaunchBoxMqtt.Core.Payloads;

/// <summary>
/// Serialises a <see cref="FrontEndSnapshot"/> to the JSON document published
/// (retained) on the state topic. Field names are part of the public contract:
/// Home Assistant discovery templates and users' own automations read them, so
/// renaming one is a breaking change.
/// </summary>
public static class StatePayload
{
    /// <summary>Shown as "now playing" when no game is running.</summary>
    public const string IdleTitle = "Idle";

    public static string Build(FrontEndSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var game = snapshot.CurrentGame;
        var last = snapshot.LastSession;

        var doc = new JsonObject
        {
            ["activity"] = snapshot.Activity.ToString().ToLowerInvariant(),
            ["playing"] = snapshot.Activity == PlayState.Playing,
            ["now_playing"] = snapshot.Activity == PlayState.Playing && game is not null ? game.Title : IdleTitle,
            ["platform"] = snapshot.Activity == PlayState.Playing ? game?.Platform : null,
            ["game"] = game is null ? null : GameNode(game, snapshot.StartedAt),
            ["bigbox_running"] = snapshot.BigBoxRunning,
            ["attract_mode"] = snapshot.AttractMode,
            ["locked"] = snapshot.Locked,
            ["last_played"] = last?.Game.Title,
            ["last_played_at"] = last is null ? null : Timestamp(last.EndedAt),
            ["last_session_seconds"] = last is null ? null : (long)Math.Round(last.Length.TotalSeconds),
            ["last_session"] = last is null ? null : LastSessionNode(last),
            ["updated_at"] = Timestamp(snapshot.UpdatedAt),
        };

        return doc.ToJsonString();
    }

    private static JsonObject GameNode(GameInfo game, DateTimeOffset? startedAt)
    {
        var genres = new JsonArray();
        foreach (var genre in game.Genres)
        {
            genres.Add(genre);
        }

        return new JsonObject
        {
            ["id"] = game.Id,
            ["title"] = game.Title,
            ["platform"] = game.Platform,
            ["emulator"] = game.Emulator,
            ["developer"] = game.Developer,
            ["publisher"] = game.Publisher,
            ["genres"] = genres,
            ["release_year"] = game.ReleaseYear,
            ["play_count"] = game.PlayCount,
            ["started_at"] = startedAt is { } s ? Timestamp(s) : null,
        };
    }

    private static JsonObject LastSessionNode(LastSession last) => new()
    {
        ["id"] = last.Game.Id,
        ["title"] = last.Game.Title,
        ["platform"] = last.Game.Platform,
        ["started_at"] = Timestamp(last.StartedAt),
        ["ended_at"] = Timestamp(last.EndedAt),
        ["session_seconds"] = (long)Math.Round(last.Length.TotalSeconds),
    };

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
