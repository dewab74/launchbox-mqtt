namespace LaunchBoxMqtt.Core.Commands;

/// <summary>A validated command from the broker. Only these exist; nothing carries a path or a command line.</summary>
public abstract record Command
{
    /// <summary>Topic suffix under <c>&lt;base&gt;/&lt;node&gt;/command/</c>.</summary>
    public abstract string Name { get; }
}

/// <summary>Ask the running game to close and return to the front end.</summary>
public sealed record ExitGameCommand : Command
{
    public const string TopicName = "exit_game";

    public override string Name => TopicName;
}

/// <summary>Launch a game already in the LaunchBox library, by its id.</summary>
public sealed record LaunchGameCommand(Guid GameId) : Command
{
    public const string TopicName = "launch_game";

    public override string Name => TopicName;
}

/// <summary>What became of a command; published (not retained) on the result topic.</summary>
public sealed record CommandOutcome(string Command, bool Accepted, string Detail)
{
    public static CommandOutcome Ok(string command, string detail) => new(command, true, detail);

    public static CommandOutcome Rejected(string command, string reason) => new(command, false, reason);
}
