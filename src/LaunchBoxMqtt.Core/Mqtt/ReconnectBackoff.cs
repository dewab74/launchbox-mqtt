namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>
/// Exponential backoff with full jitter for reconnect attempts, so a broker
/// restart does not get every client reconnecting in the same instant.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private readonly Func<double> _random;
    private int _attempt;

    public ReconnectBackoff(TimeSpan initial, TimeSpan max, Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, initial);
        _initial = initial;
        _max = max;
        _random = random ?? Random.Shared.NextDouble;
    }

    public static ReconnectBackoff CreateDefault() => new(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    /// <summary>The ceiling for the next delay, before jitter.</summary>
    public TimeSpan CurrentCeiling
    {
        get
        {
            var factor = Math.Pow(2, Math.Min(_attempt, 30));
            var ticks = Math.Min(_initial.Ticks * factor, _max.Ticks);
            return TimeSpan.FromTicks((long)ticks);
        }
    }

    /// <summary>The delay before the next attempt: half the ceiling plus up to half again at random.</summary>
    public TimeSpan NextDelay()
    {
        var ceiling = CurrentCeiling;
        _attempt++;
        var jittered = (ceiling.Ticks / 2) + (long)(ceiling.Ticks / 2 * _random());
        return TimeSpan.FromTicks(Math.Max(jittered, 1));
    }

    public void Reset() => _attempt = 0;
}
