namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>One message to publish. QoS 1 throughout: state and discovery must arrive.</summary>
public sealed record OutboundMessage(string Topic, string Payload, bool Retain);
