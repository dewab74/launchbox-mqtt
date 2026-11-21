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
    private const string StateTopic = "launchbox/cocktail/state";
    private const string AvailabilityTopic = "launchbox/cocktail/availability";

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
        var config = seen["homeassistant/sensor/launchbox_cocktail/now_playing/config"];
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

        await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(CancellationToken.None));
        Assert.False(transport.IsConnected);
    }

    private static int DiscoveryBuilderCount => Discovery.DiscoveryBuilder.Entities.Count;

    private BridgeSettings Settings() => new() { Host = "localhost", Port = _port, NodeName = "COCKTAIL" };

    private async Task StartBrokerAsync(bool rejectV5 = false, string? requiredPassword = null, X509Certificate2? tlsCertificate = null)
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
