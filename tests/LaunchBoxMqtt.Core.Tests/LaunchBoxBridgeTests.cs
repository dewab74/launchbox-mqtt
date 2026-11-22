using System.Diagnostics;
using System.Text.Json;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Mqtt;
using LaunchBoxMqtt.Core.Payloads;
using LaunchBoxMqtt.Core.State;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class LaunchBoxBridgeTests : IAsyncDisposable
{
    private const string StateTopic = "launchbox/arcade_pc/state";
    private const string AvailabilityTopic = "launchbox/arcade_pc/availability";

    private static readonly BridgeOptions FastOptions = new()
    {
        TickInterval = TimeSpan.FromMilliseconds(20),
        LaunchTimeout = TimeSpan.FromMilliseconds(200),
        MinimumSessionLength = TimeSpan.Zero,
        BackoffFactory = () => new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40)),
    };

    private readonly FakeTransport _transport = new();
    private readonly RecordingLog _log = new();
    private LaunchBoxBridge? _bridge;

    private static BridgeSettings Settings => new() { Host = "broker.test", NodeName = "Arcade PC" };

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
        }
    }

    [Fact]
    public async Task Connect_publishes_discovery_then_state_then_online()
    {
        Start();
        await Eventually.True(() => _transport.Published.Any(m => m.Topic == AvailabilityTopic), "online published");

        var messages = _transport.Published.ToList();
        var discovery = messages.FindIndex(m => m.Topic.StartsWith("homeassistant/", StringComparison.Ordinal));
        var state = messages.FindIndex(m => m.Topic == StateTopic);
        var online = messages.FindIndex(m => m.Topic == AvailabilityTopic);

        Assert.True(discovery >= 0 && discovery < state && state < online);
        Assert.Equal(Availability.Online, messages[online].Payload);
        Assert.All(messages, m => Assert.True(m.Retain));
    }

    [Fact]
    public async Task Discovery_can_be_switched_off()
    {
        Start(Settings with { DiscoveryEnabled = false });
        await Eventually.True(() => _transport.Published.Any(m => m.Topic == AvailabilityTopic), "online published");
        Assert.DoesNotContain(_transport.Published, m => m.Topic.StartsWith("homeassistant/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Game_events_publish_state()
    {
        Start();
        await Connected();

        _bridge!.OnGameLaunching(TestGames.Galaga);
        _bridge.OnGameLaunched(TestGames.Galaga);
        await Eventually.True(() => LatestState()?.GetProperty("now_playing").GetString() == "Galaga", "Galaga published");

        _bridge.OnGameExited();
        await Eventually.True(() => LatestState()?.GetProperty("last_played").GetString() == "Galaga", "exit published");
        Assert.False(LatestState()!.Value.GetProperty("playing").GetBoolean());
    }

    [Fact]
    public async Task Events_return_immediately_while_the_broker_hangs()
    {
        Start();
        await Connected();
        _transport.PublishGate = new TaskCompletionSource();

        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
        {
            _bridge!.OnGameLaunched(i % 2 == 0 ? TestGames.Galaga : TestGames.PacMan);
            _bridge.OnGameExited();
        }

        timer.Stop();
        Assert.True(timer.ElapsedMilliseconds < 500, $"2000 events took {timer.ElapsedMilliseconds} ms");
        _transport.PublishGate.SetResult();
    }

    [Fact]
    public async Task State_is_coalesced_not_replayed()
    {
        Start();
        await Connected();
        _transport.PublishGate = new TaskCompletionSource();
        var before = _transport.Published.Count;

        for (var i = 0; i < 100; i++)
        {
            _bridge!.OnAttractModeChanged(i % 2 == 0);
        }

        _bridge!.OnGameLaunched(TestGames.PacMan);
        _transport.PublishGate.SetResult();

        await Eventually.True(() => LatestState()?.GetProperty("now_playing").GetString() == "Pac-Man", "final state published");
        Assert.True(_transport.Published.Count - before < 10, "events were coalesced");
    }

    [Fact]
    public async Task Broker_outage_retries_with_one_notification()
    {
        _transport.FailConnects = 5;
        Start();

        await Eventually.True(() => _transport.IsConnected, "connected after retries");
        Assert.Equal(6, _transport.ConnectAttempts);
        Assert.Single(_log.Notifications);
    }

    [Fact]
    public async Task Dropped_connection_reconnects_and_republishes_current_state()
    {
        Start();
        await Connected();
        _bridge!.OnGameLaunched(TestGames.Galaga);
        await Eventually.True(() => LatestState()?.GetProperty("now_playing").GetString() == "Galaga", "Galaga published");

        _transport.Drop();
        while (_transport.Published.TryDequeue(out _))
        {
        }

        await Eventually.True(() => _transport.Published.Any(m => m.Topic == AvailabilityTopic), "online republished");
        Assert.Equal("Galaga", LatestState()!.Value.GetProperty("now_playing").GetString());
    }

    [Fact]
    public async Task Unconfirmed_launch_falls_back_to_idle()
    {
        Start();
        await Connected();
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => LatestState()?.GetProperty("activity").GetString() == "launching", "launching published");
        await Eventually.True(() => LatestState()?.GetProperty("activity").GetString() == "idle", "launch expired");
        Assert.Equal(PlayState.Idle, _bridge.Current.Activity);
    }

    [Fact]
    public async Task Stop_publishes_offline_and_disconnects()
    {
        Start();
        await Connected();

        await _bridge!.StopAsync(TimeSpan.FromSeconds(2));

        var last = _transport.Published.Last();
        Assert.Equal(AvailabilityTopic, last.Topic);
        Assert.Equal(Availability.Offline, last.Payload);
        Assert.False(_transport.IsConnected);
    }

    [Fact]
    public async Task Stop_during_an_in_flight_publish_still_ends_offline_without_alarming_the_user()
    {
        // BigBox shutdown: the "BigBox stopped" state change and Stop race, and MQTTnet reports the
        // cancelled publish as a timeout. That must not count as a broker outage, and the last
        // retained availability must be offline (a clean disconnect discards the Last Will).
        Start();
        await Connected();
        _transport.NextPublishGate = new TaskCompletionSource();
        _bridge!.OnBigBoxRunningChanged(true);
        await Eventually.True(() => Volatile.Read(ref _transport.PendingPublishes) == 1, "publish in flight");

        await _bridge.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(_log.Notifications);
        var availability = _transport.Published.Last(m => m.Topic == AvailabilityTopic);
        Assert.Equal(Availability.Offline, availability.Payload);
        Assert.True(LatestState()!.Value.GetProperty("bigbox_running").GetBoolean(), "final state flushed before offline");
        Assert.False(_transport.IsConnected);
    }

    [Fact]
    public async Task Stop_gives_up_on_a_hung_broker()
    {
        Start();
        await Connected();
        _transport.PublishGate = new TaskCompletionSource();

        var timer = Stopwatch.StartNew();
        await _bridge!.StopAsync(TimeSpan.FromMilliseconds(200));
        Assert.True(timer.ElapsedMilliseconds < 2_000, $"stop took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Invalid_settings_fail_at_construction() =>
        Assert.Throws<ArgumentException>(() =>
            new LaunchBoxBridge(Settings with { Host = "" }, _transport, _log, "1.0.0"));

    private void Start(BridgeSettings? settings = null)
    {
        _bridge = new LaunchBoxBridge(settings ?? Settings, _transport, _log, "0.0.0-test", options: FastOptions);
        _bridge.Start();
    }

    private Task Connected() =>
        Eventually.True(() => _transport.Published.Any(m => m.Topic == AvailabilityTopic), "bridge connected");

    private JsonElement? LatestState()
    {
        var message = _transport.Published.LastOrDefault(m => m.Topic == StateTopic);
        return message is null ? null : JsonElement.Parse(message.Payload);
    }
}
