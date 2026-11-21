namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>One message to publish. QoS 1 throughout: state and discovery must arrive.</summary>
public sealed record OutboundMessage(string Topic, string Payload, bool Retain)
{
    /// <summary>A binary payload (artwork); when set it is sent instead of <see cref="Payload"/>.</summary>
    public byte[]? Binary { get; init; }
}

/// <summary>A message received on a subscribed topic.</summary>
public sealed record IncomingMessage(string Topic, byte[] Payload, bool Retained);
