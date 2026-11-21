namespace LaunchBoxMqtt.Core.Commands;

/// <summary>
/// Carries out commands inside LaunchBox. Implemented by the plugin shell,
/// which marshals to LaunchBox's UI thread. Called off the MQTT thread; may
/// take its time, must not throw for an ordinary refusal (return a rejection).
/// </summary>
public interface ICommandHandler
{
    Task<CommandOutcome> HandleAsync(Command command, CancellationToken cancellationToken);
}
