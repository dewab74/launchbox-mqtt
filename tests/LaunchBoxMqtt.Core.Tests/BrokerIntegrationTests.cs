using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Mqtt;
using LaunchBoxMqtt.Core.Payloads;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace LaunchBoxMqtt.Core.Tests;

/// <summary>
/// The real transport against a real (in-process MQTTnet) broker: what a
/// subscriber such as Home Assistant actually receives.
/// </summary>
public sealed class BrokerIntegrationTests : IAsyncLifetime
{
    private const string StateTopic = "launchbox/arcade_pc/state";
    private const string AvailabilityTopic = "launchbox/arcade_pc/availability";

    private readonly string _dir = Directory.CreateTempSubdirectory("lbmqtt-broker").FullName;
    private readonly List<IAsyncDisposable> _cleanup = [];
    private readonly ConcurrentQueue<MqttProtocolVersion> _connectProtocols = new();
    private MqttServer? _server;
    private int _port;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
        {
            await item.DisposeAsync();
        }

        if (_server is not null)
        {
            await _server.StopAsync();
            _server.Dispose();
        }

        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Subscriber_sees_discovery_state_and_online()
    {
        await StartBrokerAsync();
        var bridge = await StartBridgeAsync(Settings());
        bridge.OnGameLaunched(TestGames.Galaga);

        var seen = await SubscribeAsync();
        await Eventually.True(() => seen.TryGetValue(AvailabilityTopic, out var a) && a == Availability.Online, "online retained");
        await Eventually.True(() => seen.TryGetValue(StateTopic, out var s) && s.Contains("Galaga", StringComparison.Ordinal), "state retained");

        await Eventually.True(() => seen.Keys.Count(k => k.StartsWith("homeassistant/", StringComparison.Ordinal)) == DiscoveryBuilderCount, "all discovery configs retained");
        var config = seen["homeassistant/sensor/launchbox_arcade_pc/now_playing/config"];
        Assert.Equal(StateTopic, JsonElement.Parse(config).GetProperty("state_topic").GetString());
        Assert.Equal(DiscoveryBuilderCount, seen.Keys.Count(k => k.StartsWith("homeassistant/", StringComparison.Ordinal)));
        Assert.Equal(MqttProtocolVersion.V500, _connectProtocols.Last());
    }

    [Fact]
    public async Task Clean_stop_leaves_offline_retained()
    {
        await StartBrokerAsync();
        var bridge = await StartBridgeAsync(Settings());
        await bridge.StopAsync(TimeSpan.FromSeconds(5));

        var seen = await SubscribeAsync();
        await Eventually.True(() => seen.TryGetValue(AvailabilityTopic, out var a) && a == Availability.Offline, "offline retained");
    }

    [Fact]
    public async Task Abrupt_drop_triggers_the_last_will()
    {
        await StartBrokerAsync();
        var transport = new MqttNetTransport(Settings());
        await transport.ConnectAsync(CancellationToken.None);
        await transport.PublishAsync(new OutboundMessage(AvailabilityTopic, Availability.Online, Retain: true), CancellationToken.None);

        // No DISCONNECT packet: the broker sees the session die, as when the PC loses power.
        await _server!.DisconnectClientAsync(Settings().ClientId, new MqttServerClientDisconnectOptions
        {
            ReasonCode = MqttDisconnectReasonCode.UnspecifiedError,
        });
        await transport.DisposeAsync();

        var seen = await SubscribeAsync();
        await Eventually.True(() => seen.TryGetValue(AvailabilityTopic, out var a) && a == Availability.Offline, "will published");
    }

    [Fact]
    public async Task Falls_back_to_mqtt_311_when_the_broker_refuses_v5()
    {
        await StartBrokerAsync(rejectV5: true);
        var transport = new MqttNetTransport(Settings());
        _cleanup.Add(transport);

        await transport.ConnectAsync(CancellationToken.None);

        Assert.True(transport.IsConnected);
        Assert.Equal(MqttProtocolVersion.V311, transport.NegotiatedProtocol);
        Assert.Equal([MqttProtocolVersion.V500, MqttProtocolVersion.V311], _connectProtocols.ToArray());
    }

    [Fact]
    public async Task Wrong_password_is_an_error_not_a_silent_success()
    {
        await StartBrokerAsync(requiredPassword: "correct-horse");
        var transport = new MqttNetTransport(Settings() with { Username = "launchbox", Password = "wrong" }); // pragma: allowlist secret (test value)
        _cleanup.Add(transport);

        await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(CancellationToken.None));
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task Tls_with_a_private_ca_connects()
    {
        using var ca = TestCertificates.CreateCa("Test Root CA");
        using var serverCert = TestCertificates.CreateServer(ca, "localhost");
        await StartBrokerAsync(tlsCertificate: serverCert);

        var transport = new MqttNetTransport(Settings() with
        {
            UseTls = true,
            CaCertificatePath = TestCertificates.WritePem(_dir, ca),
        });
        _cleanup.Add(transport);

        await transport.ConnectAsync(CancellationToken.None);
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task Tls_with_an_untrusted_ca_is_refused()
    {
        using var ca = TestCertificates.CreateCa("Test Root CA");
        using var otherCa = TestCertificates.CreateCa("Other CA");
        using var serverCert = TestCertificates.CreateServer(ca, "localhost");
        await StartBrokerAsync(tlsCertificate: serverCert);

        var transport = new MqttNetTransport(Settings() with
        {
            UseTls = true,
            CaCertificatePath = TestCertificates.WritePem(_dir, otherCa),
        });
        _cleanup.Add(transport);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(CancellationToken.None));
        Assert.False(transport.IsConnected);
        Assert.Contains("does not chain to the configured CA", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_arrive_over_a_real_broker_but_a_retained_one_never_runs()
    {
        await StartBrokerAsync();

        // Left on the broker before the bridge connects, e.g. by a misconfigured automation.
        var publisher = await ConnectObserverAsync("publisher");
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("launchbox/arcade_pc/command/exit_game").WithPayload("PRESS").WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), TestContext.Current.CancellationToken);

        var handler = new RecordingCommandHandler();
        var settings = Settings() with { CommandsEnabled = true };
        var bridge = new LaunchBoxBridge(settings, new MqttNetTransport(settings), new RecordingLog(), "0.0.0-test", commands: handler);
        _cleanup.Add(bridge);
        bridge.Start();
        await Eventually.True(() => bridge.IsConnected, "bridge connected");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Empty(handler.Handled);

        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("launchbox/arcade_pc/command/launch_game").WithPayload("6f1c2a4e-0000-4000-8000-000000000001")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), TestContext.Current.CancellationToken);
        await Eventually.True(() => handler.Handled.Count == 1, "live command handled");
        Assert.IsType<LaunchBoxMqtt.Core.Commands.LaunchGameCommand>(handler.Handled.Single());

        // A retained command published while the bridge is subscribed is refused too (MQTT 5).
        await Task.Delay(3_100, TestContext.Current.CancellationToken); // past the minimum command interval
        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("launchbox/arcade_pc/command/exit_game").WithPayload("PRESS").WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Single(handler.Handled);
    }

    // With commands off (the default) their configs are published empty, which deletes the retained
    // message on the broker, so a subscriber sees only the state entities.
    [Fact]
    public async Task Binary_artwork_arrives_intact_over_a_real_broker()
    {
        await StartBrokerAsync();
        var bytes = new byte[300_000];
        new Random(42).NextBytes(bytes);
        var transport = new MqttNetTransport(Settings());
        _cleanup.Add(transport);
        await transport.ConnectAsync(CancellationToken.None);
        await transport.PublishAsync(new OutboundMessage("launchbox/arcade_pc/artwork", string.Empty, Retain: true) { Binary = bytes }, CancellationToken.None);

        var received = new TaskCompletionSource<byte[]>();
        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            received.TrySetResult(System.Buffers.BuffersExtensions.ToArray(e.ApplicationMessage.Payload));
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("localhost", _port).WithClientId("artwork-observer").Build(), TestContext.Current.CancellationToken);
        _cleanup.Add(new ClientCleanup(client));
        await client.SubscribeAsync("launchbox/arcade_pc/artwork", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private static int DiscoveryBuilderCount => Discovery.DiscoveryBuilder.Entities.Count;

    private async Task<IMqttClient> ConnectObserverAsync(string clientId)
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("localhost", _port).WithClientId(clientId).Build());
        _cleanup.Add(new ClientCleanup(client));
        return client;
    }

    private BridgeSettings Settings() => new() { Host = "localhost", Port = _port, NodeName = "Arcade PC" };

    private async Task StartBrokerAsync(bool rejectV5 = false, string? requiredPassword = null, X509Certificate2? tlsCertificate = null)
    {
        // FreePort releases the port before the broker binds it, so another process can take it in
        // between ("Address already in use"); try a few fresh ports before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TryStartBrokerAsync(rejectV5, requiredPassword, tlsCertificate);
                return;
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse && attempt < 5)
            {
                _server?.Dispose();
                _server = null;
            }
        }
    }

    private async Task TryStartBrokerAsync(bool rejectV5, string? requiredPassword, X509Certificate2? tlsCertificate)
    {
        _port = FreePort();
        var factory = new MqttServerFactory();
        var builder = factory.CreateServerOptionsBuilder();
        builder = tlsCertificate is null
            ? builder.WithDefaultEndpoint().WithDefaultEndpointPort(_port)
            : builder.WithoutDefaultEndpoint().WithEncryptedEndpoint().WithEncryptedEndpointPort(_port)
                .WithEncryptionCertificate(tlsCertificate);

        _server = factory.CreateMqttServer(builder.Build());
        _server.ValidatingConnectionAsync += e =>
        {
            if (e.ClientId == Settings().ClientId)
            {
                _connectProtocols.Enqueue(e.ProtocolVersion);
                if (rejectV5 && e.ProtocolVersion == MqttProtocolVersion.V500)
                {
                    e.ReasonCode = MqttConnectReasonCode.UnsupportedProtocolVersion;
                }
                else if (requiredPassword is not null && e.Password != requiredPassword)
                {
                    e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                }
            }

            return Task.CompletedTask;
        };
        await _server.StartAsync();
    }

    private async Task<LaunchBoxBridge> StartBridgeAsync(BridgeSettings settings)
    {
        var transport = new MqttNetTransport(settings);
        var bridge = new LaunchBoxBridge(settings, transport, new RecordingLog(), "0.0.0-test");
        _cleanup.Add(bridge);
        bridge.Start();
        await Eventually.True(() => bridge.IsConnected, "bridge connected");
        return bridge;
    }

    private async Task<ConcurrentDictionary<string, string>> SubscribeAsync()
    {
        var seen = new ConcurrentDictionary<string, string>();
        var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            seen[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("localhost", _port).WithClientId("observer").Build());
        await client.SubscribeAsync("#");
        _cleanup.Add(new ClientCleanup(client));
        return seen;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ClientCleanup(IMqttClient client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync();
            }

            client.Dispose();
        }
    }
}
