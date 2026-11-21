using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

public class ReconnectBackoffTests
{
    [Fact]
    public void Ceiling_doubles_until_the_maximum()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), () => 1.0);
        var delays = Enumerable.Range(0, 6).Select(_ => backoff.NextDelay().TotalSeconds).ToArray();
        Assert.Equal([1, 2, 4, 8, 10, 10], delays);
    }

    [Fact]
    public void Jitter_stays_between_half_and_full_ceiling()
    {
        var low = new ReconnectBackoff(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8), () => 0.0);
        Assert.Equal(TimeSpan.FromSeconds(4), low.NextDelay());
    }

    [Fact]
    public void Reset_starts_over()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), () => 1.0);
        backoff.NextDelay();
        backoff.NextDelay();
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [Fact]
    public void Many_attempts_do_not_overflow()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), () => 1.0);
        for (var i = 0; i < 1_000; i++)
        {
            backoff.NextDelay();
        }

        Assert.Equal(TimeSpan.FromMinutes(1), backoff.NextDelay());
    }
}
