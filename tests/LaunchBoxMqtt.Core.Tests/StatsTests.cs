using System.Collections.Concurrent;
using System.Text.Json;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Mqtt;
using LaunchBoxMqtt.Core.Stats;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class StatsTests : IAsyncDisposable
{
    private const string StatsTopic = "launchbox/cocktail/stats";

    private readonly FakeTransport _transport = new();
    private readonly RecordingLog _log = new();
    private readonly FakeStatsSource _source = new();
    private LaunchBoxBridge? _bridge;

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
        }
    }

    [Fact]
    public void Payload_keeps_missing_parts_explicit()
    {
        var at = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var json = JsonElement.Parse(StatsPayload.Build("id-1", "Sonic", new GameStats(
            new AchievementStats("RetroAchievements", "hardcore", 12, 40, 120m, 400m, Beaten: true, Completed: false), null), at));
        Assert.Equal("Sonic", json.GetProperty("title").GetString());
        var a = json.GetProperty("achievements");
        Assert.Equal(12, a.GetProperty("unlocked").GetInt32());
        Assert.Equal(40, a.GetProperty("total").GetInt32());
        Assert.Equal(30.0, a.GetProperty("percent").GetDouble());
        Assert.Equal("hardcore", a.GetProperty("mode").GetString());
        Assert.True(a.GetProperty("beaten").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("high_score").ValueKind);
    }

    [Fact]
    public void Payload_with_the_cabinets_high_score()
    {
        var json = JsonElement.Parse(StatsPayload.Build("id-2", "Galaga", new GameStats(null,
            new HighScore(111000m, "DEW", [new ScoreEntry(1, 111000m, "DEW"), new ScoreEntry(2, 109460m, "DEW")])), DateTimeOffset.UnixEpoch));
        var h = json.GetProperty("high_score");
        Assert.Equal(111000m, h.GetProperty("score").GetDecimal());
        Assert.Equal("DEW", h.GetProperty("name").GetString());
        Assert.Equal(2, h.GetProperty("entries").GetArrayLength());
        Assert.Equal(109460m, h.GetProperty("entries")[1].GetProperty("score").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("achievements").ValueKind);
    }

    [Fact]
    public async Task Launch_publishes_stats_retained()
    {
        await StartAsync(stats: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        _bridge.OnGameLaunched(TestGames.Galaga);

        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "stats published");
        var message = Stats().Single(m => m.Payload.Length > 0);
        Assert.True(message.Retain);
        var json = JsonElement.Parse(message.Payload);
        Assert.Equal(TestGames.Galaga.Id, json.GetProperty("game_id").GetString());
        Assert.Equal(1000m, json.GetProperty("high_score").GetProperty("score").GetDecimal());
        Assert.Equal(1, _source.Requests.Count(r => r == TestGames.Galaga.Id));
        Assert.Contains(_log.Lines, l => l.Contains("Published stats for Galaga: high score 1000 (ABC)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Latest_game_wins_when_an_earlier_fetch_is_slow()
    {
        await StartAsync(stats: true);
        _source.Delays[TestGames.Galaga.Id] = TimeSpan.FromMilliseconds(500);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        _bridge.OnGameExited();
        _bridge.OnGameLaunching(TestGames.PacMan);

        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "stats published");
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.All(Stats().Where(m => m.Payload.Length > 0), m =>
            Assert.Equal(TestGames.PacMan.Id, JsonElement.Parse(m.Payload).GetProperty("game_id").GetString()));
    }

    [Fact]
    public async Task A_game_without_stats_publishes_none_known_not_the_previous_game()
    {
        await StartAsync(stats: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "first stats");

        _source.Missing.Add(TestGames.PacMan.Id);
        _bridge.OnGameExited();
        _bridge.OnGameLaunching(TestGames.PacMan);
        await Eventually.True(() => Stats().Count(m => m.Payload.Length > 0) == 2, "second stats");

        var last = JsonElement.Parse(Stats().Last(m => m.Payload.Length > 0).Payload);
        Assert.Equal(TestGames.PacMan.Id, last.GetProperty("game_id").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("high_score").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("achievements").ValueKind);
    }

    [Fact]
    public async Task A_failing_source_never_breaks_the_bridge()
    {
        await StartAsync(stats: true);
        _source.Throw = true;
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => _log.Lines.Any(l => l.Contains("Stats for Galaga could not be read", StringComparison.Ordinal)), "failure logged");
        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "none-known published");
        Assert.True(_bridge.IsConnected);
    }

    [Fact]
    public async Task Exit_reads_the_game_again_for_the_new_score()
    {
        await StartAsync(stats: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "stats on launch");

        _bridge.OnGameExited();
        await Eventually.True(() => _source.Requests.Count(r => r == TestGames.Galaga.Id) == 2, "read again after exit");
        await Eventually.True(() => Stats().Any(m => m.Payload.Contains("\"score\":1010", StringComparison.Ordinal)), "new score published");
    }

    [Fact]
    public async Task Stats_off_clears_the_topic_and_never_asks()
    {
        await StartAsync(stats: false);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(_source.Requests);
        var clear = Assert.Single(Stats());
        Assert.True(clear.Retain);
        Assert.Equal(string.Empty, clear.Payload);
        Assert.Contains(_transport.Published, m => m.Topic.EndsWith("/sensor/launchbox_cocktail/high_score/config", StringComparison.Ordinal) && m.Payload.Length == 0);
    }

    [Fact]
    public async Task Reconnect_republishes_the_current_stats_without_asking_again()
    {
        await StartAsync(stats: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "stats published");

        _transport.Drop();
        while (_transport.Published.TryDequeue(out _))
        {
        }

        await Eventually.True(() => Stats().Any(m => m.Payload.Length > 0), "stats republished");
        Assert.Single(_source.Requests);
    }

    private async Task StartAsync(bool stats)
    {
        var settings = new BridgeSettings { Host = "broker.test", NodeName = "COCKTAIL", StatsEnabled = stats };
        var options = new BridgeOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(20),
            BackoffFactory = () => new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40)),
            StatsRefreshDelay = TimeSpan.FromMilliseconds(50),
        };
        _bridge = new LaunchBoxBridge(settings, _transport, _log, "0.0.0-test", options: options, stats: _source);
        _bridge.Start();
        await Eventually.True(() => _transport.Published.Any(m => m.Topic == "launchbox/cocktail/availability"), "connected");
    }

    private List<OutboundMessage> Stats() => [.. _transport.Published.Where(m => m.Topic == StatsTopic)];

    private sealed class FakeStatsSource : IGameStatsSource
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentDictionary<string, TimeSpan> Delays { get; } = new();

        public ConcurrentBag<string> Missing { get; } = [];

        public bool Throw { get; set; }

        /// <summary>Goes up on every read, like a table after someone plays.</summary>
        public decimal Score { get; set; } = 990m;

        public async Task<GameStats?> GetStatsAsync(string gameId, CancellationToken cancellationToken)
        {
            Requests.Enqueue(gameId);
            if (Delays.TryGetValue(gameId, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (Throw)
            {
                throw new InvalidOperationException("provider down");
            }

            Score += 10;
            return Missing.Contains(gameId) ? null : new GameStats(null, new HighScore(Score, "ABC", [new ScoreEntry(1, Score, "ABC")]));
        }
    }
}
