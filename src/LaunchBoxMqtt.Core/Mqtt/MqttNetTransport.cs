using System.Security.Cryptography.X509Certificates;
using LaunchBoxMqtt.Core.Payloads;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>
/// <see cref="IMqttTransport"/> on MQTTnet. Tries MQTT 5 and falls back to
/// 3.1.1 for brokers that refuse it, then remembers what worked.
/// </summary>
public sealed class MqttNetTransport : IMqttTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly BridgeSettings _settings;
    private readonly string _availabilityTopic;
    private readonly X509Certificate2Collection? _caBundle;
    private readonly IMqttClient _client;
    private MqttProtocolVersion _protocol = MqttProtocolVersion.V500;
    private string? _lastTlsRejection;

    public MqttNetTransport(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _availabilityTopic = settings.CreateTopicLayout().AvailabilityTopic;
        _caBundle = settings.UseTls && !string.IsNullOrEmpty(settings.CaCertificatePath)
            ? CertificateValidation.LoadCaBundle(settings.CaCertificatePath)
            : null;
        _client = new MqttClientFactory().CreateMqttClient();
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>The protocol the last successful connection used.</summary>
    public MqttProtocolVersion NegotiatedProtocol => _protocol;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _lastTlsRejection = null;
        try
        {
            await ConnectAnyProtocolAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (_lastTlsRejection is { } reason && ex is not OperationCanceledException)
        {
            // MQTTnet only says "rejected by the callback"; say why.
            throw new InvalidOperationException(reason, ex);
        }
    }

    private async Task ConnectAnyProtocolAsync(CancellationToken cancellationToken)
    {
        if (await TryConnectAsync(_protocol, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (_protocol == MqttProtocolVersion.V500
            && await TryConnectAsync(MqttProtocolVersion.V311, cancellationToken).ConfigureAwait(false))
        {
            _protocol = MqttProtocolVersion.V311;
            return;
        }

        throw new InvalidOperationException($"Broker refused MQTT protocol {_protocol}.");
    }

    public async Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(message.Topic)
            .WithPayload(message.Payload)
            .WithRetainFlag(message.Retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var result = await _client.PublishAsync(mqttMessage, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Broker rejected publish to '{message.Topic}': {result.ReasonCode} {result.ReasonString}".TrimEnd());
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        var options = new MqttClientDisconnectOptionsBuilder()
            .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
            .Build();
        await _client.DisconnectAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    internal MqttClientOptions BuildOptions(MqttProtocolVersion protocol)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Host, _settings.EffectivePort)
            .WithClientId(_settings.ClientId)
            .WithProtocolVersion(protocol)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(ConnectTimeout)
            .WithWillTopic(_availabilityTopic)
            .WithWillPayload(Availability.Offline)
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(_settings.Username))
        {
            builder.WithCredentials(_settings.Username, _settings.Password ?? string.Empty);
        }

        if (_settings.UseTls)
        {
            var caBundle = _caBundle;
            builder.WithTlsOptions(tls =>
            {
                tls.UseTls();
                tls.WithTargetHost(_settings.Host);
                if (caBundle is not null)
                {
                    tls.WithCertificateValidationHandler(args =>
                    {
                        _lastTlsRejection = CertificateValidation.Check(args.Certificate, args.Chain, args.SslPolicyErrors, caBundle);
                        return _lastTlsRejection is null;
                    });
                }
            });
        }

        return builder.Build();
    }

    /// <summary>Connects, or returns false if the broker refused the protocol version.</summary>
    private async Task<bool> TryConnectAsync(MqttProtocolVersion protocol, CancellationToken cancellationToken)
    {
        MqttClientConnectResult result;
        try
        {
            result = await _client.ConnectAsync(BuildOptions(protocol), cancellationToken).ConfigureAwait(false);
        }
        catch (MqttConnectingFailedException ex)
            when (ex.Message.Contains(nameof(MqttClientConnectResultCode.UnsupportedProtocolVersion), StringComparison.Ordinal))
        {
            // MQTTnet 5 reports a refused CONNACK as an exception whose message names the result code.
            return false;
        }

        return result.ResultCode switch
        {
            MqttClientConnectResultCode.Success => true,
            MqttClientConnectResultCode.UnsupportedProtocolVersion => false,
            _ => throw new InvalidOperationException($"Broker refused connection: {result.ResultCode} {result.ReasonString}".TrimEnd()),
        };
    }
}
