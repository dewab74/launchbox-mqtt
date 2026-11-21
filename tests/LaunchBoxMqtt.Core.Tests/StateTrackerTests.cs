using LaunchBoxMqtt.Core.State;
using Microsoft.Extensions.Time.Testing;

namespace LaunchBoxMqtt.Core.Tests;

public class StateTrackerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Starts_idle()
    {
        var state = new StateTracker(_time).Current;
        Assert.Equal(PlayState.Idle, state.Activity);
        Assert.Null(state.CurrentGame);
        Assert.Null(state.LastSession);
    }

    [Fact]
    public void Launch_then_exit_records_a_session()
    {
        var tracker = new StateTracker(_time);

        Assert.True(tracker.GameLaunching(TestGames.Galaga));
        Assert.Equal(PlayState.Launching, tracker.Current.Activity);

        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(tracker.GameLaunched(TestGames.Galaga));
        var started = _time.GetUtcNow();
        Assert.Equal(PlayState.Playing, tracker.Current.Activity);
        Assert.Equal(started, tracker.Current.StartedAt);

        _time.Advance(TimeSpan.FromMinutes(25));
        Assert.True(tracker.GameExited());

        var state = tracker.Current;
        Assert.Equal(PlayState.Idle, state.Activity);
        Assert.Null(state.CurrentGame);
        Assert.NotNull(state.LastSession);
        Assert.Equal(TestGames.Galaga, state.LastSession.Game);
        Assert.Equal(TimeSpan.FromMinutes(25), state.LastSession.Length);
    }

    [Fact]
    public void Exit_without_confirmed_launch_is_not_a_session()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunching(TestGames.Galaga);
        Assert.True(tracker.GameExited());
        Assert.Equal(PlayState.Idle, tracker.Current.Activity);
        Assert.Null(tracker.Current.LastSession);
    }

    [Fact]
    public void Failed_launch_keeps_the_previous_session()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunched(TestGames.Galaga);
        _time.Advance(TimeSpan.FromMinutes(1));
        tracker.GameExited();

        tracker.GameLaunching(TestGames.PacMan);
        tracker.GameExited();

        Assert.Equal(TestGames.Galaga, tracker.Current.LastSession?.Game);
    }

    [Fact]
    public void Launch_that_dies_at_once_does_not_replace_the_last_session()
    {
        // Seen on a real cabinet: a missing ROM is reported as launched, then exited 0.5 s later.
        var tracker = new StateTracker(_time);
        tracker.GameLaunched(TestGames.Galaga);
        _time.Advance(TimeSpan.FromMinutes(5));
        tracker.GameExited(TimeSpan.FromSeconds(10));

        tracker.GameLaunching(TestGames.PacMan);
        tracker.GameLaunched(TestGames.PacMan);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(tracker.GameExited(TimeSpan.FromSeconds(10), out var discarded));

        Assert.Equal(PlayState.Idle, tracker.Current.Activity);
        Assert.Equal(TestGames.Galaga, tracker.Current.LastSession?.Game);
        Assert.Equal(TimeSpan.FromMilliseconds(500), discarded);
    }

    [Fact]
    public void Session_at_the_minimum_is_recorded()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunched(TestGames.Galaga);
        _time.Advance(TimeSpan.FromSeconds(10));
        tracker.GameExited(TimeSpan.FromSeconds(10), out var discarded);
        Assert.Null(discarded);
        Assert.Equal(TestGames.Galaga, tracker.Current.LastSession?.Game);
    }

    [Fact]
    public void Unconfirmed_launch_expires_after_the_timeout()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunching(TestGames.Galaga);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(tracker.ExpireStaleLaunch(TimeSpan.FromMinutes(1)));
        Assert.Equal(PlayState.Launching, tracker.Current.Activity);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.ExpireStaleLaunch(TimeSpan.FromMinutes(1)));
        Assert.Equal(PlayState.Idle, tracker.Current.Activity);
        Assert.Null(tracker.Current.CurrentGame);
    }

    [Fact]
    public void Confirmed_launch_never_expires()
    {
        var tracker = new StateTracker(_time);
        tracker.GameLaunching(TestGames.Galaga);
        tracker.GameLaunched(TestGames.Galaga);
        _time.Advance(TimeSpan.FromHours(3));
        Assert.False(tracker.ExpireStaleLaunch(TimeSpan.FromMinutes(1)));
        Assert.Equal(PlayState.Playing, tracker.Current.Activity);
    }

    [Fact]
    public void Repeated_events_report_no_change()
    {
        var tracker = new StateTracker(_time);
        Assert.True(tracker.SetBigBoxRunning(true));
        Assert.False(tracker.SetBigBoxRunning(true));
        Assert.True(tracker.SetAttractMode(true));
        Assert.False(tracker.SetAttractMode(true));
        Assert.True(tracker.GameLaunched(TestGames.Galaga));
        Assert.False(tracker.GameLaunched(TestGames.Galaga with { PlayCount = 13 }));
        Assert.True(tracker.GameExited());
        Assert.False(tracker.GameExited());
    }

    [Fact]
    public void UpdatedAt_moves_only_on_change()
    {
        var tracker = new StateTracker(_time);
        var first = tracker.Current.UpdatedAt;
        _time.Advance(TimeSpan.FromSeconds(10));
        tracker.SetLocked(false);
        Assert.Equal(first, tracker.Current.UpdatedAt);
        tracker.SetLocked(true);
        Assert.Equal(_time.GetUtcNow(), tracker.Current.UpdatedAt);
    }

    [Fact]
    public void BigBox_exit_clears_attract_mode_and_lock()
    {
        var tracker = new StateTracker(_time);
        tracker.SetBigBoxRunning(true);
        tracker.SetAttractMode(true);
        tracker.SetLocked(true);

        tracker.SetBigBoxRunning(false);

        Assert.False(tracker.Current.BigBoxRunning);
        Assert.False(tracker.Current.AttractMode);
        Assert.False(tracker.Current.Locked);
    }

    [Fact]
    public async Task Concurrent_events_do_not_corrupt_state()
    {
        var tracker = new StateTracker(_time);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 2_000; n++)
            {
                tracker.GameLaunched(i % 2 == 0 ? TestGames.Galaga : TestGames.PacMan);
                tracker.SetAttractMode(n % 2 == 0);
                tracker.GameExited();
            }
        })));

        tracker.GameExited();
        Assert.Equal(PlayState.Idle, tracker.Current.Activity);
        Assert.Null(tracker.Current.CurrentGame);
    }
}
