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

    /// <summary>Accepted commands are at least this far apart; a burst (or a loop in an automation) is refused.</summary>
    public TimeSpan MinCommandInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A command that has not finished after this long is reported as failed.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public Func<ReconnectBackoff> BackoffFactory { get; init; } = ReconnectBackoff.CreateDefault;
}
