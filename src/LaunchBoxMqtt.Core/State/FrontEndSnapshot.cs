namespace LaunchBoxMqtt.Core.State;

/// <summary>Everything published on the state topic, at one point in time.</summary>
public sealed record FrontEndSnapshot
{
    public static FrontEndSnapshot Initial { get; } = new();

    public PlayState Activity { get; init; } = PlayState.Idle;

    /// <summary>The game being launched or played; null when idle.</summary>
    public GameInfo? CurrentGame { get; init; }

    /// <summary>When the current game was confirmed running.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public LastSession? LastSession { get; init; }

    public bool BigBoxRunning { get; init; }

    public bool AttractMode { get; init; }

    public bool Locked { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The most recent completed play session.</summary>
public sealed record LastSession(GameInfo Game, DateTimeOffset StartedAt, DateTimeOffset EndedAt)
{
    public TimeSpan Length => EndedAt - StartedAt;
}
