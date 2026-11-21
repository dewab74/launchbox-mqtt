using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using Unbroken.LaunchBox.Plugins.Services.Logging;
using Unbroken.LaunchBox.Plugins.Services.Notifications;
using Unbroken.LaunchBox.Plugins.Services.Storage;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// The LaunchBox entrypoint. Thin on purpose: every callback forwards to the
/// bridge, which returns at once and never throws, so the plugin cannot delay
/// or break a game launch. LaunchBox supplies the services by constructor
/// injection.
/// </summary>
public sealed class LaunchBoxMqttPlugin : IGameLaunchingPlugin, ISystemEventsPlugin
{
    public LaunchBoxMqttPlugin(IPluginFileSystemStore files, IPluginLogger logger, IPluginSecretStore secrets, INotificationManager notifications)
    {
        ArgumentNullException.ThrowIfNull(files);
        PluginHost.EnsureStarted(files.RootDirectory, logger, secrets, notifications);
    }

    public void OnBeforeGameLaunching(IGame? game, IAdditionalApplication? app, IEmulator? emulator) =>
        PluginHost.Forward(
            b =>
            {
                if (game is not null)
                {
                    b.OnGameLaunching(GameMapper.From(game, app, emulator));
                }
            },
            nameof(OnBeforeGameLaunching));

    public void OnAfterGameLaunched(IGame game, IAdditionalApplication? app, IEmulator? emulator) =>
        PluginHost.Forward(b => b.OnGameLaunched(GameMapper.From(game, app, emulator)), nameof(OnAfterGameLaunched));

    public void OnGameExited() =>
        PluginHost.Forward(b => b.OnGameExited(), nameof(OnGameExited));

    public void OnEventRaised(string eventType)
    {
        switch (eventType)
        {
            case SystemEventTypes.BigBoxStartupCompleted:
                PluginHost.Forward(b => b.OnBigBoxRunningChanged(true), eventType);
                break;
            case SystemEventTypes.BigBoxLocked:
                PluginHost.Forward(b => b.OnLockedChanged(true), eventType);
                break;
            case SystemEventTypes.BigBoxUnlocked:
                PluginHost.Forward(b => b.OnLockedChanged(false), eventType);
                break;
            case SystemEventTypes.BigBoxShutdownBeginning:
            case SystemEventTypes.LaunchBoxShutdownBeginning:
                PluginHost.Forward(b => b.OnBigBoxRunningChanged(false), eventType);
                PluginHost.Stop(eventType);
                break;
            default:
                PluginHost.Forward(_ => { }, eventType);
                break;
        }
    }
}
