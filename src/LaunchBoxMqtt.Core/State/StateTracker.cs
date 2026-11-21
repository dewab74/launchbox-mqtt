namespace LaunchBoxMqtt.Core.State;

/// <summary>
/// Turns LaunchBox events into <see cref="FrontEndSnapshot"/>s. Thread-safe:
/// LaunchBox raises events on its UI thread while the publisher reads from a
/// background thread. Every mutator returns whether the snapshot changed.
/// </summary>
public sealed class StateTracker
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private FrontEndSnapshot _current;
    private DateTimeOffset? _launchRequestedAt;

    public StateTracker(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _current = FrontEndSnapshot.Initial with { UpdatedAt = time.GetUtcNow() };
    }

    public FrontEndSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>LaunchBox is about to start <paramref name="game"/>.</summary>
    public bool GameLaunching(GameInfo game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            _launchRequestedAt = _time.GetUtcNow();
            return Apply(s => s with { Activity = PlayState.Launching, CurrentGame = game, StartedAt = null });
        }
    }

    /// <summary>LaunchBox reports <paramref name="game"/> running.</summary>
    public bool GameLaunched(GameInfo game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            _launchRequestedAt = null;
            if (_current.Activity == PlayState.Playing && _current.CurrentGame?.Id == game.Id)
            {
                return false;
            }

            return Apply(s => s with { Activity = PlayState.Playing, CurrentGame = game, StartedAt = _time.GetUtcNow() });
        }
    }

    /// <summary>
    /// The running game exited. A session is recorded only if the game was
    /// confirmed running for at least <paramref name="minimumSession"/>: an exit
    /// straight from <see cref="PlayState.Launching"/>, or seconds after the
    /// launch, is a failed launch (LaunchBox reports a missing ROM as launched,
    /// then exited), and must not replace the real last session.
    /// </summary>
    public bool GameExited(TimeSpan minimumSession = default) => GameExited(minimumSession, out _);

    /// <inheritdoc cref="GameExited(TimeSpan)"/>
    /// <param name="minimumSession">Shorter sessions are not recorded.</param>
    /// <param name="discardedLength">Set when a confirmed session was too short to record.</param>
    public bool GameExited(TimeSpan minimumSession, out TimeSpan? discardedLength)
    {
        discardedLength = null;
        lock (_gate)
        {
            _launchRequestedAt = null;
            if (_current.Activity == PlayState.Idle)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            var session = _current is { CurrentGame: { } g, StartedAt: { } started }
                ? new LastSession(g, started, now)
                : null;
            if (session is not null && session.Length < minimumSession)
            {
                discardedLength = session.Length;
                session = null;
            }

            return Apply(s => s with
            {
                Activity = PlayState.Idle,
                CurrentGame = null,
                StartedAt = null,
                LastSession = session ?? s.LastSession,
            });
        }
    }

    /// <summary>
    /// LaunchBox does not raise <c>OnGameExited</c> when a launch fails, so a
    /// launch that is not confirmed within <paramref name="timeout"/> is
    /// abandoned and the state returns to idle. Called from the publisher's
    /// periodic tick.
    /// </summary>
    public bool ExpireStaleLaunch(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_current.Activity != PlayState.Launching || _launchRequestedAt is not { } requested)
            {
                return false;
            }

            if (_time.GetUtcNow() - requested < timeout)
            {
                return false;
            }

            _launchRequestedAt = null;
            return Apply(s => s with { Activity = PlayState.Idle, CurrentGame = null, StartedAt = null });
        }
    }

    public bool SetBigBoxRunning(bool running)
    {
        lock (_gate)
        {
            // Attract mode and the lock are BigBox states; they cannot outlive it.
            return running
                ? Apply(s => s with { BigBoxRunning = true })
                : Apply(s => s with { BigBoxRunning = false, AttractMode = false, Locked = false });
        }
    }

    public bool SetAttractMode(bool active)
    {
        lock (_gate)
        {
            return Apply(s => s with { AttractMode = active });
        }
    }

    public bool SetLocked(bool locked)
    {
        lock (_gate)
        {
            return Apply(s => s with { Locked = locked });
        }
    }

    // Caller holds _gate.
    private bool Apply(Func<FrontEndSnapshot, FrontEndSnapshot> change)
    {
        var next = change(_current) with { UpdatedAt = _current.UpdatedAt };
        if (next == _current)
        {
            return false;
        }

        _current = next with { UpdatedAt = _time.GetUtcNow() };
        return true;
    }
}
