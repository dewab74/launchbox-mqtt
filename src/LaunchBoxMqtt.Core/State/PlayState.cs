namespace LaunchBoxMqtt.Core.State;

public enum PlayState
{
    /// <summary>No game is running.</summary>
    Idle,

    /// <summary>LaunchBox is starting a game and has not confirmed it yet.</summary>
    Launching,

    /// <summary>A game is running.</summary>
    Playing,
}
