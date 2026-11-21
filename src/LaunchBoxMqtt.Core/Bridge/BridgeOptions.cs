using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Bridge;

/// <summary>Timing knobs; the defaults are what the plugin uses.</summary>
public sealed record BridgeOptions
{
    /// <summary>A launch not confirmed within this long is treated as failed.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sessions shorter than this are treated as failed launches and do not
    /// replace "last played".
    /// </summary>
    public TimeSpan MinimumSessionLength { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the loop wakes without an event (stale-launch check).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(5);

    public Func<ReconnectBackoff> BackoffFactory { get; init; } = ReconnectBackoff.CreateDefault;
}
