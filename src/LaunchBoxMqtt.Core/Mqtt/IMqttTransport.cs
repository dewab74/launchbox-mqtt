namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>
/// The broker connection as the bridge sees it. <see cref="MqttNetTransport"/>
/// is the real one; tests substitute a fake.
/// </summary>
public interface IMqttTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Connects, registering the availability topic's offline payload as the Last Will.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Publishes at QoS 1. Throws if the broker did not accept the message.</summary>
    Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken);

    /// <summary>A clean disconnect: the broker discards the Last Will.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}
