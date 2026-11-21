using System.Text.Json;
using LaunchBoxMqtt.Core.Payloads;
using LaunchBoxMqtt.Core.State;
using Microsoft.Extensions.Time.Testing;

namespace LaunchBoxMqtt.Core.Tests;

public class StatePayloadTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Idle_payload()
    {
        using var doc = JsonDocument.Parse(StatePayload.Build(new StateTracker(_time).Current));
        var root = doc.RootElement;

        Assert.Equal("idle", root.GetProperty("activity").GetString());
        Assert.False(root.GetProperty("playing").GetBoolean());
        Assert.Equal(StatePayload.IdleTitle, root.GetProperty("now_playing").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("platform").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("game").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("last_played").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("last_session_seconds").ValueKind);
        Assert.Equal("2026-11-20T20:00:00Z", root.GetProperty("updated_at").GetString());
    }

    [Fact]
    public void Playing_payload_carries_game_details()
    {
        var tracker = new StateTracker(_time);
        tracker.SetBigBoxRunning(true);
        tracker.GameLaunched(TestGames.Galaga);

        using var doc = JsonDocument.Parse(StatePayload.Build(tracker.Current));
        var root = doc.RootElement;
        var game = root.GetProperty("game");

        Assert.Equal("playing", root.GetProperty("activity").GetString());
        Assert.True(root.GetProperty("playing").GetBoolean());
        Assert.Equal("Galaga", root.GetProperty("now_playing").GetString());
        Assert.Equal("Arcade", root.GetProperty("platform").GetString());
        Assert.True(root.GetProperty("bigbox_running").GetBoolean());
        Assert.Equal(TestGames.Galaga.Id, game.GetProperty("id").GetString());
        Assert.Equal("MAME", game.GetProperty("emulator").GetString());
        Assert.Equal("Namco", game.GetProperty("developer").GetString());
        Assert.Equal("Midway", game.GetProperty("publisher").GetString());
        Assert.Equal("Shooter", game.GetProperty("genres")[0].GetString());
        Assert.Equal(1981, game.GetProperty("release_year").GetInt32());
        Assert.Equal(12, game.GetProperty("play_count").GetInt32());
        Assert.Equal("2026-11-20T20:00:00Z", game.GetProperty("started_at").GetString());
    }

    [Fact]
    public void Launching_is_not_reported_as_playing()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunching(TestGames.Galaga);

        using var doc = JsonDocument.Parse(StatePayload.Build(tracker.Current));
        var root = doc.RootElement;

        Assert.Equal("launching", root.GetProperty("activity").GetString());
        Assert.False(root.GetProperty("playing").GetBoolean());
        Assert.Equal(StatePayload.IdleTitle, root.GetProperty("now_playing").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("platform").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("game").GetProperty("started_at").ValueKind);
    }

    [Fact]
    public void Last_session_fields_after_exit()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunched(TestGames.Galaga);
        _time.Advance(TimeSpan.FromSeconds(754.6));
        tracker.GameExited();

        using var doc = JsonDocument.Parse(StatePayload.Build(tracker.Current));
        var root = doc.RootElement;

        Assert.Equal("Galaga", root.GetProperty("last_played").GetString());
        Assert.Equal("2026-11-20T20:12:34Z", root.GetProperty("last_played_at").GetString());
        Assert.Equal(755, root.GetProperty("last_session_seconds").GetInt64());
        Assert.Equal("Arcade", root.GetProperty("last_session").GetProperty("platform").GetString());
        Assert.Equal(755, root.GetProperty("last_session").GetProperty("session_seconds").GetInt64());
    }

    [Fact]
    public void Titles_with_json_metacharacters_round_trip()
    {
        var tracker = new StateTracker(_time);
        var odd = TestGames.PacMan with { Title = "\"Quoted\" \\ Game\n{}" };
        tracker.GameLaunched(odd);

        using var doc = JsonDocument.Parse(StatePayload.Build(tracker.Current));
        Assert.Equal(odd.Title, doc.RootElement.GetProperty("now_playing").GetString());
    }
}
