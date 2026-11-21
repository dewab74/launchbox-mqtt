namespace LaunchBoxMqtt.Core.Topics;

/// <summary>
/// Every topic the bridge uses, derived from the configured prefixes and the
/// node id. One place, so the discovery payloads and the publisher can never
/// disagree about where state lives.
/// </summary>
public sealed class TopicLayout
{
    public const string DefaultBaseTopic = "launchbox";
    public const string DefaultDiscoveryPrefix = "homeassistant";

    public TopicLayout(string baseTopic, string discoveryPrefix, string nodeId)
    {
        BaseTopic = ValidatePrefix(baseTopic, nameof(baseTopic));
        DiscoveryPrefix = ValidatePrefix(discoveryPrefix, nameof(discoveryPrefix));
        if (string.IsNullOrEmpty(nodeId) || NodeId.FromName(nodeId) != nodeId)
        {
            throw new ArgumentException($"Node id '{nodeId}' is not normalised; use NodeId.FromName.", nameof(nodeId));
        }

        Node = nodeId;
    }

    public string BaseTopic { get; }

    public string DiscoveryPrefix { get; }

    public string Node { get; }

    /// <summary>Home Assistant discovery node id, also the device identifier.</summary>
    public string DeviceId => $"launchbox_{Node}";

    public string StateTopic => $"{BaseTopic}/{Node}/state";

    public string AvailabilityTopic => $"{BaseTopic}/{Node}/availability";

    public string DiscoveryTopic(string component, string objectId) =>
        $"{DiscoveryPrefix}/{component}/{DeviceId}/{objectId}/config";

    /// <summary>
    /// Returns an error message, or null when <paramref name="prefix"/> is a
    /// usable topic prefix: non-empty, no wildcards, no leading or trailing
    /// slash, no empty levels.
    /// </summary>
    public static string? CheckPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "must not be empty";
        }

        if (prefix.Contains('+', StringComparison.Ordinal) || prefix.Contains('#', StringComparison.Ordinal))
        {
            return "must not contain MQTT wildcards (+ or #)";
        }

        if (prefix.StartsWith('/') || prefix.EndsWith('/') || prefix.Contains("//", StringComparison.Ordinal))
        {
            return "must not start or end with '/' or contain empty levels";
        }

        if (prefix.Any(char.IsWhiteSpace) || prefix.Contains('\0', StringComparison.Ordinal))
        {
            return "must not contain whitespace";
        }

        return null;
    }

    private static string ValidatePrefix(string prefix, string paramName)
    {
        var error = CheckPrefix(prefix);
        return error is null ? prefix : throw new ArgumentException($"Topic prefix '{prefix}' {error}.", paramName);
    }
}
