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

    /// <summary>
    /// Subscribes at QoS 1, asking the broker not to replay retained messages and to
    /// keep the retain flag on live deliveries (MQTT 5 "retain as published"). A 3.1.1
    /// broker may replay them (flagged) and always clears the flag on live delivery.
    /// Throws if the broker refuses the subscription.
    /// </summary>
    Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken);

    /// <summary>Raised for every message on a subscribed topic, on the transport's thread: hand off, don't block.</summary>
    event Func<IncomingMessage, Task>? MessageReceived;

    /// <summary>A clean disconnect: the broker discards the Last Will.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}
