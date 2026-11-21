using System.Text.Json;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Commands;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class BridgeCommandTests : IAsyncDisposable
{
    private const string GameId = "6f1c2a4e-0000-4000-8000-000000000001";
    private const string Prefix = "launchbox/cocktail/command/";
    private const string ResultTopic = "launchbox/cocktail/command_result";

    private readonly FakeTransport _transport = new();
    private readonly RecordingLog _log = new();
    private readonly RecordingCommandHandler _handler = new();
    private LaunchBoxBridge? _bridge;

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
        }
    }

    [Fact]
    public async Task Commands_off_by_default_means_no_subscription_and_no_action()
    {
        await StartAsync(commandsEnabled: false);
        await _transport.Deliver(Prefix + "exit_game", "PRESS");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(_transport.Subscriptions);
        Assert.Empty(_handler.Handled);
        Assert.Contains(_transport.Published, m => m.Topic.EndsWith("/exit_game/config", StringComparison.Ordinal) && m.Payload.Length == 0);
    }

    [Fact]
    public async Task Enabled_commands_subscribe_and_reach_the_handler()
    {
        await StartAsync(commandsEnabled: true);
        Assert.Equal(["launchbox/cocktail/command/+"], _transport.Subscriptions);

        await _transport.Deliver(Prefix + "launch_game", GameId);
        await Eventually.True(() => Results().Count > 0, "result published");

        Assert.Equal(Guid.Parse(GameId), Assert.IsType<LaunchGameCommand>(Assert.Single(_handler.Handled)).GameId);
        var result = Results().Single();
        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Equal("launch_game", result.GetProperty("command").GetString());
        Assert.False(_transport.Published.Single(m => m.Topic == ResultTopic).Retain);
    }

    [Fact]
    public async Task Invalid_and_retained_commands_are_reported_not_run()
    {
        await StartAsync(commandsEnabled: true);
        await _transport.Deliver(Prefix + "launch_game", "C:\\evil.exe");
        await _transport.Deliver(Prefix + "launch_game", GameId, retained: true);
        await Eventually.True(() => Results().Count == 2, "two results");

        Assert.Empty(_handler.Handled);
        Assert.All(Results(), r => Assert.False(r.GetProperty("accepted").GetBoolean()));
    }

    [Fact]
    public async Task Commands_are_rate_limited()
    {
        await StartAsync(commandsEnabled: true, minInterval: TimeSpan.FromMinutes(1));
        await _transport.Deliver(Prefix + "exit_game", "");
        await Eventually.True(() => Results().Count == 1, "first result");
        await _transport.Deliver(Prefix + "exit_game", "");
        await Eventually.True(() => Results().Count == 2, "second result");

        Assert.Single(_handler.Handled);
        Assert.Contains("too soon", Results().Last().GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_command_at_a_time()
    {
        await StartAsync(commandsEnabled: true);
        _handler.Gate = new TaskCompletionSource();
        await _transport.Deliver(Prefix + "exit_game", "");
        await Eventually.True(() => _handler.Handled.Count == 1, "first command running");
        await _transport.Deliver(Prefix + "exit_game", "");
        await Eventually.True(() => Results().Count > 0, "second refused");
        _handler.Gate.SetResult();

        Assert.Contains("still running", Results().First().GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Single(_handler.Handled);
    }

    [Fact]
    public async Task A_hung_handler_times_out()
    {
        await StartAsync(commandsEnabled: true, timeout: TimeSpan.FromMilliseconds(200));
        _handler.Gate = new TaskCompletionSource();
        await _transport.Deliver(Prefix + "exit_game", "");
        await Eventually.True(() => Results().Count > 0, "timeout reported");
        Assert.Contains("did not finish", Results().Single().GetProperty("detail").GetString(), StringComparison.Ordinal);
        _handler.Gate.SetResult();
    }

    [Fact]
    public async Task Resubscribes_after_reconnect()
    {
        await StartAsync(commandsEnabled: true);
        _transport.Drop();
        await Eventually.True(() => _transport.Subscriptions.Count == 2, "resubscribed");
    }

    private async Task StartAsync(bool commandsEnabled, TimeSpan? minInterval = null, TimeSpan? timeout = null)
    {
        var settings = new BridgeSettings { Host = "broker.test", NodeName = "COCKTAIL", CommandsEnabled = commandsEnabled };
        var options = new BridgeOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(20),
            MinCommandInterval = minInterval ?? TimeSpan.Zero,
            CommandTimeout = timeout ?? TimeSpan.FromSeconds(5),
            BackoffFactory = () => new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40)),
        };
        _bridge = new LaunchBoxBridge(settings, _transport, _log, "0.0.0-test", options: options, commands: _handler);
        _bridge.Start();
        await Eventually.True(() => _transport.Published.Any(m => m.Topic == "launchbox/cocktail/availability"), "connected");
        if (commandsEnabled)
        {
            await Eventually.True(() => !_transport.Subscriptions.IsEmpty, "subscribed");
        }
    }

    private List<JsonElement> Results() =>
        _transport.Published.Where(m => m.Topic == ResultTopic).Select(m => JsonElement.Parse(m.Payload)).ToList();
}
