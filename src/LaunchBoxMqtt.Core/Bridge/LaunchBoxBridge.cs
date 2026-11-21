using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LaunchBoxMqtt.Core.Artwork;
using LaunchBoxMqtt.Core.Commands;
using LaunchBoxMqtt.Core.Discovery;
using LaunchBoxMqtt.Core.Mqtt;
using LaunchBoxMqtt.Core.Payloads;
using LaunchBoxMqtt.Core.State;
using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Bridge;

/// <summary>
/// The whole bridge behind the plugin shell. The <c>On*</c> methods are called
/// from LaunchBox's event handlers: they update the state in memory, wake the
/// background loop and return at once. They never block, never touch the
/// network and never throw, so a dead broker cannot slow a game launch.
///
/// The background loop owns the connection: it connects with backoff,
/// publishes discovery, state and availability on every (re)connect, and then
/// publishes the latest state whenever it changes. State is coalesced, not
/// queued: after an outage the broker gets the current state, not a replay.
/// </summary>
public sealed class LaunchBoxBridge : IAsyncDisposable
{
    private readonly BridgeSettings _settings;
    private readonly IMqttTransport _transport;
    private readonly IBridgeLog _log;
    private readonly TimeProvider _time;
    private readonly BridgeOptions _options;
    private readonly StateTracker _tracker;
    private readonly DiscoveryBuilder _discovery;
    private readonly string _stateTopic;
    private readonly string _availabilityTopic;
    private readonly TopicLayout _topics;
    private readonly ICommandHandler? _commands;
    private readonly SemaphoreSlim _commandSlot = new(1, 1);
    private DateTimeOffset _lastCommandAt = DateTimeOffset.MinValue;
    private readonly IArtworkSource? _artwork;
    private readonly Lock _artworkGate = new();
    private string? _artworkGameId;
    private byte[]? _lastArtwork;
    private CancellationTokenSource? _artworkFetch;

    /// <summary>Larger artwork (after scaling) is not published.</summary>
    public const int MaxArtworkBytes = 2 * 1024 * 1024;
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private int _dirty;
    private bool _outageNotified;

    public LaunchBoxBridge(
        BridgeSettings settings,
        IMqttTransport transport,
        IBridgeLog log,
        string version,
        TimeProvider? time = null,
        BridgeOptions? options = null,
        ICommandHandler? commands = null,
        IArtworkSource? artwork = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(log);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid MQTT settings: " + string.Join(" ", errors), nameof(settings));
        }

        _settings = settings;
        _transport = transport;
        _log = log;
        _time = time ?? TimeProvider.System;
        _options = options ?? new BridgeOptions();
        _tracker = new StateTracker(_time);
        var topics = settings.CreateTopicLayout();
        _topics = topics;
        _commands = commands;
        _artwork = artwork;
        _discovery = new DiscoveryBuilder(
            topics,
            settings.NodeName.Trim(),
            version,
            commandsEnabled: settings.CommandsEnabled && commands is not null,
            artworkEnabled: ArtworkActive);
        _stateTopic = topics.StateTopic;
        _availabilityTopic = topics.AvailabilityTopic;
        _transport.MessageReceived += OnMessageAsync;
    }

    public FrontEndSnapshot Current => _tracker.Current;

    public bool IsConnected => _transport.IsConnected;

    /// <summary>Starts the background loop. Returns immediately.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The bridge is already started.");
        }

        _log.Log(BridgeLogLevel.Info, $"Starting MQTT bridge: {_settings}");
        _loop = Task.Run(() => RunAsync(_stopping.Token));
    }

    public void OnGameLaunching(GameInfo game)
    {
        Mutate(() => _tracker.GameLaunching(game), nameof(OnGameLaunching));
        RequestArtwork(game);
    }

    public void OnGameLaunched(GameInfo game)
    {
        Mutate(() => _tracker.GameLaunched(game), nameof(OnGameLaunched));
        RequestArtwork(game);
    }

    private bool ArtworkActive => _settings.ArtworkEnabled && _artwork is not null;

    public void OnGameExited() => Mutate(
        () =>
        {
            var changed = _tracker.GameExited(_options.MinimumSessionLength, out var discarded);
            if (discarded is { } length)
            {
                _log.Log(BridgeLogLevel.Info, $"Game exited after {length.TotalSeconds:0.#} s; treated as a failed launch, not recorded as last played.");
            }

            return changed;
        },
        nameof(OnGameExited));

    public void OnBigBoxRunningChanged(bool running) => Mutate(() => _tracker.SetBigBoxRunning(running), nameof(OnBigBoxRunningChanged));

    public void OnAttractModeChanged(bool active) => Mutate(() => _tracker.SetAttractMode(active), nameof(OnAttractModeChanged));

    public void OnLockedChanged(bool locked) => Mutate(() => _tracker.SetLocked(locked), nameof(OnLockedChanged));

    /// <summary>
    /// Stops the loop, then publishes <c>offline</c> and disconnects cleanly,
    /// all within <paramref name="timeout"/>. LaunchBox is shutting down when
    /// this runs, so it gives up rather than wait on a dead broker; the Last
    /// Will covers that case.
    /// </summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(timeout, _time);
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Log(BridgeLogLevel.Warning, "MQTT loop did not stop in time.");
            }
        }

        if (!_transport.IsConnected)
        {
            return;
        }

        try
        {
            // Flush the last state (often "BigBox stopped") that the cancelled loop may not have sent.
            await PublishStateAsync(deadline.Token).ConfigureAwait(false);
            await _transport.PublishAsync(new OutboundMessage(_availabilityTopic, Availability.Offline, Retain: true), deadline.Token).ConfigureAwait(false);
            await _transport.DisconnectAsync(deadline.Token).ConfigureAwait(false);
            _log.Log(BridgeLogLevel.Info, "MQTT bridge stopped.");
        }
#pragma warning disable CA1031 // Shutdown must not throw into LaunchBox; the Last Will still marks us offline.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Log(BridgeLogLevel.Warning, "Clean MQTT disconnect failed; the broker will publish the Last Will.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        _transport.MessageReceived -= OnMessageAsync;
        lock (_artworkGate)
        {
            _artworkFetch?.Cancel();
            _artworkFetch?.Dispose();
            _artworkFetch = null;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _commandSlot.Dispose();
    }

    private void Mutate(Func<bool> change, string source)
    {
        try
        {
            if (change())
            {
                Volatile.Write(ref _dirty, 1);
                _wake.Writer.TryWrite(0);
            }
        }
#pragma warning disable CA1031 // Event handlers run inside LaunchBox; an exception here could break a game launch.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Log(BridgeLogLevel.Error, $"{source} failed.", ex);
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var backoff = _options.BackoffFactory();
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!_transport.IsConnected)
                {
                    await _transport.ConnectAsync(stop).ConfigureAwait(false);
                    await PublishSessionStartAsync(stop).ConfigureAwait(false);
                    backoff.Reset();
                    _log.Log(BridgeLogLevel.Info, _outageNotified ? "Reconnected to the MQTT broker." : "Connected to the MQTT broker.");
                    _outageNotified = false;
                }

                await WaitForWorkAsync(stop).ConfigureAwait(false);
                if (_tracker.ExpireStaleLaunch(_options.LaunchTimeout))
                {
                    _log.Log(BridgeLogLevel.Warning, "A game launch was never confirmed; treating it as failed.");
                    Volatile.Write(ref _dirty, 1);
                }

                if (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    try
                    {
                        await PublishStateAsync(stop).ConfigureAwait(false);
                    }
                    catch
                    {
                        Volatile.Write(ref _dirty, 1);
                        throw;
                    }
                }
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                // Stopping. MQTTnet reports a cancelled publish as a timeout, so any exception counts
                // here; treating it as an outage would disconnect cleanly and discard the Last Will.
                break;
            }
#pragma warning disable CA1031 // Any broker failure is retried; the loop must not die.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await HandleFailureAsync(ex, backoff.NextDelay(), stop).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleFailureAsync(Exception ex, TimeSpan retryIn, CancellationToken stop)
    {
        _log.Log(BridgeLogLevel.Warning, $"MQTT broker unavailable ({ex.Message}); retrying in {retryIn.TotalSeconds:0.#} s.", ex);
        if (!_outageNotified)
        {
            _outageNotified = true;
            try
            {
                _log.NotifyUser($"LaunchBox MQTT: cannot reach the broker at {_settings.Host}:{_settings.EffectivePort}. Retrying in the background.");
            }
#pragma warning disable CA1031 // A broken notifier must not stop reconnects.
            catch (Exception notifyError)
#pragma warning restore CA1031
            {
                _log.Log(BridgeLogLevel.Error, "Showing the broker notification failed.", notifyError);
            }
        }

        try
        {
            // Force the next pass through ConnectAsync, even if the client still claims a connection.
            await _transport.DisconnectAsync(stop).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort; the reconnect is what matters.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            await Task.Delay(retryIn, _time, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishSessionStartAsync(CancellationToken stop)
    {
        if (_settings.DiscoveryEnabled)
        {
            foreach (var config in _discovery.BuildConfigs())
            {
                await _transport.PublishAsync(config, stop).ConfigureAwait(false);
            }
        }

        Volatile.Write(ref _dirty, 0);
        await PublishStateAsync(stop).ConfigureAwait(false);
        await _transport.PublishAsync(new OutboundMessage(_availabilityTopic, Availability.Online, Retain: true), stop).ConfigureAwait(false);

        // Artwork: republish the current image (the broker may have lost it), or clear a stale
        // retained one when artwork is off so nothing old lingers for other subscribers.
        byte[]? artwork;
        lock (_artworkGate)
        {
            artwork = _lastArtwork;
        }

        if (!ArtworkActive)
        {
            await _transport.PublishAsync(new OutboundMessage(_topics.ArtworkTopic, string.Empty, Retain: true), stop).ConfigureAwait(false);
        }
        else if (artwork is not null)
        {
            await _transport.PublishAsync(new OutboundMessage(_topics.ArtworkTopic, string.Empty, Retain: true) { Binary = artwork }, stop).ConfigureAwait(false);
        }

        if (_settings.CommandsEnabled)
        {
            if (_commands is null)
            {
                _log.Log(BridgeLogLevel.Warning, "Commands are enabled but this host has no command handler; not subscribing.");
            }
            else
            {
                // Subscriptions do not survive a clean-session reconnect, so this runs on every connect.
                await _transport.SubscribeAsync(_topics.CommandSubscription, stop).ConfigureAwait(false);
                _log.Log(BridgeLogLevel.Info, $"Accepting commands on {_topics.CommandSubscription}.");
            }
        }
    }

    private void RequestArtwork(GameInfo game)
    {
        if (!ArtworkActive || _stopping.IsCancellationRequested)
        {
            return;
        }

        CancellationToken token;
        lock (_artworkGate)
        {
            // Launching and launched both ask; the second is a no-op for the same game.
            if (_artworkGameId == game.Id)
            {
                return;
            }

            _artworkGameId = game.Id;
            _artworkFetch?.Cancel();
            _artworkFetch?.Dispose();
            _artworkFetch = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            token = _artworkFetch.Token;
        }

        // Decoding and scaling an image can take a moment; never on LaunchBox's thread.
        _ = Task.Run(() => FetchArtworkAsync(game, token), CancellationToken.None);
    }

    private async Task FetchArtworkAsync(GameInfo game, CancellationToken token)
    {
        ArtworkImage? image;
        try
        {
            image = await _artwork!.GetArtworkAsync(game.Id, _settings.ArtworkMaxWidth, token)
                .WaitAsync(_options.ArtworkTimeout, _time, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
#pragma warning disable CA1031 // Missing or broken artwork must never affect the game or the bridge.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Log(BridgeLogLevel.Warning, $"Artwork for {game.Title} could not be read.", ex);
            return;
        }

        if (image is null)
        {
            _log.Log(BridgeLogLevel.Info, $"No artwork for {game.Title}; keeping the previous image.");
            return;
        }

        if (image.Png.Length > MaxArtworkBytes)
        {
            _log.Log(BridgeLogLevel.Warning, $"Artwork for {game.Title} is {image.Png.Length / 1024} KB after scaling; not published (limit {MaxArtworkBytes / 1024} KB). Lower the artwork width.");
            return;
        }

        lock (_artworkGate)
        {
            if (_artworkGameId != game.Id || token.IsCancellationRequested)
            {
                return; // Another game started meanwhile.
            }

            _lastArtwork = image.Png;
        }

        try
        {
            await _transport.PublishAsync(new OutboundMessage(_topics.ArtworkTopic, string.Empty, Retain: true) { Binary = image.Png }, token).ConfigureAwait(false);
            _log.Log(BridgeLogLevel.Info, $"Published artwork for {game.Title}: {image.Kind} {image.Width}x{image.Height}, {image.Png.Length / 1024} KB.");
        }
#pragma warning disable CA1031 // Not connected: the next connect republishes the latest image.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Log(BridgeLogLevel.Debug, $"Artwork for {game.Title} will be published on reconnect.", ex);
        }
    }

    private Task OnMessageAsync(IncomingMessage message)
    {
        // Defence in depth: never act on a command unless enabled, whatever the broker sends.
        if (!_settings.CommandsEnabled || _commands is null || _stopping.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        // Off the transport's thread: a command may take seconds and must not stall MQTT.
        _ = Task.Run(() => ExecuteCommandAsync(message));
        return Task.CompletedTask;
    }

    private async Task ExecuteCommandAsync(IncomingMessage message)
    {
        var (command, rejection) = CommandParser.Parse(_topics, message.Topic, message.Payload, message.Retained);
        var name = command?.Name ?? message.Topic[Math.Min(message.Topic.Length, _topics.CommandTopicPrefix.Length)..];
        CommandOutcome outcome;
        if (command is null)
        {
            outcome = CommandOutcome.Rejected(name, rejection ?? "invalid command");
        }
        else if (!_commandSlot.Wait(0))
        {
            outcome = CommandOutcome.Rejected(name, "another command is still running");
        }
        else
        {
            try
            {
                var now = _time.GetUtcNow();
                if (now - _lastCommandAt < _options.MinCommandInterval)
                {
                    outcome = CommandOutcome.Rejected(name, $"too soon after the previous command (minimum {_options.MinCommandInterval.TotalSeconds:0} s apart)");
                }
                else
                {
                    _lastCommandAt = now;
                    _log.Log(BridgeLogLevel.Info, $"Command received: {command}.");
                    outcome = await _commands!.HandleAsync(command, _stopping.Token)
                        .WaitAsync(_options.CommandTimeout, _time, _stopping.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
                outcome = CommandOutcome.Rejected(name, $"did not finish within {_options.CommandTimeout.TotalSeconds:0} s");
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A failing command is reported on the result topic; it must not take the bridge down.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log.Log(BridgeLogLevel.Error, $"Command {name} failed.", ex);
                outcome = CommandOutcome.Rejected(name, ex.Message);
            }
            finally
            {
                _commandSlot.Release();
            }
        }

        _log.Log(outcome.Accepted ? BridgeLogLevel.Info : BridgeLogLevel.Warning, $"Command {outcome.Command}: {(outcome.Accepted ? "done" : "refused")}: {outcome.Detail}");
        await PublishOutcomeAsync(outcome).ConfigureAwait(false);
    }

    private async Task PublishOutcomeAsync(CommandOutcome outcome)
    {
        var payload = new JsonObject
        {
            ["command"] = outcome.Command,
            ["accepted"] = outcome.Accepted,
            ["detail"] = outcome.Detail,
            ["at"] = _time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        try
        {
            await _transport.PublishAsync(new OutboundMessage(_topics.CommandResultTopic, payload.ToJsonString(), Retain: false), _stopping.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The outcome is already logged; a lost result message is not worth more.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Log(BridgeLogLevel.Debug, "Could not publish the command result.", ex);
        }
    }

    private Task PublishStateAsync(CancellationToken stop) =>
        _transport.PublishAsync(new OutboundMessage(_stateTopic, StatePayload.Build(_tracker.Current), Retain: true), stop);

    private async Task WaitForWorkAsync(CancellationToken stop)
    {
        if (Volatile.Read(ref _dirty) == 1)
        {
            return;
        }

        using var tick = new CancellationTokenSource(_options.TickInterval, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, tick.Token);
        try
        {
            await _wake.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // Tick elapsed with no event.
        }

        while (_wake.Reader.TryRead(out _))
        {
        }
    }
}
