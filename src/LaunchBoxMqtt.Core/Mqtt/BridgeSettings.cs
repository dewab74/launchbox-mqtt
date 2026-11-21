using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>
/// Broker and topic settings. The plugin shell fills this from LaunchBox's
/// configuration values; the password comes from LaunchBox's plugin secret
/// store and is never written to a plain config file.
/// </summary>
public sealed record BridgeSettings
{
    public const int DefaultPort = 1883;
    public const int DefaultTlsPort = 8883;

    public required string Host { get; init; }

    public int? Port { get; init; }

    public bool UseTls { get; init; }

    /// <summary>
    /// PEM file with the CA certificate(s) that issued the broker's certificate,
    /// for brokers behind a private CA. Null uses the operating system's trust store.
    /// </summary>
    public string? CaCertificatePath { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>The PC's name, shown in Home Assistant and normalised into the node id.</summary>
    public required string NodeName { get; init; }

    public string BaseTopic { get; init; } = TopicLayout.DefaultBaseTopic;

    public string DiscoveryPrefix { get; init; } = TopicLayout.DefaultDiscoveryPrefix;

    public bool DiscoveryEnabled { get; init; } = true;

    public int EffectivePort => Port ?? (UseTls ? DefaultTlsPort : DefaultPort);

    public string NodeId => Topics.NodeId.FromName(NodeName);

    /// <summary>MQTT client id; defaults to one per node. A connection test uses its own so it cannot kick the running bridge off.</summary>
    public string ClientId
    {
        get => _clientId ?? $"launchbox-mqtt-{NodeId}";
        init => _clientId = value;
    }

    private readonly string? _clientId;

    public TopicLayout CreateTopicLayout() => new(BaseTopic, DiscoveryPrefix, NodeId);

    /// <summary>Every problem with these settings; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host))
        {
            errors.Add("Broker host is required.");
        }

        if (Port is < 1 or > 65535)
        {
            errors.Add($"Broker port {Port} is outside 1-65535.");
        }

        if (TopicLayout.CheckPrefix(BaseTopic) is { } baseError)
        {
            errors.Add($"Base topic {baseError}.");
        }

        if (TopicLayout.CheckPrefix(DiscoveryPrefix) is { } discoveryError)
        {
            errors.Add($"Discovery prefix {discoveryError}.");
        }

        if (!string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(Username))
        {
            errors.Add("A password was given without a username.");
        }

        if (CaCertificatePath is { Length: > 0 } && !UseTls)
        {
            errors.Add("A CA certificate was given but TLS is off.");
        }

        if (CaCertificatePath is { Length: > 0 } path && !File.Exists(path))
        {
            errors.Add($"CA certificate file '{path}' does not exist.");
        }

        return errors;
    }

    // The password must never reach a log line through record ToString().
    public override string ToString() =>
        $"{(UseTls ? "mqtts" : "mqtt")}://{(string.IsNullOrEmpty(Username) ? string.Empty : Username + "@")}{Host}:{EffectivePort} node={NodeId} base={BaseTopic} discovery={(DiscoveryEnabled ? DiscoveryPrefix : "off")}";
}
