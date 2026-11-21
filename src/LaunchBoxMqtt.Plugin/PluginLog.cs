using LaunchBoxMqtt.Core.Bridge;
using Unbroken.LaunchBox.Plugins.Models.Localization;
using Unbroken.LaunchBox.Plugins.Services.Logging;
using Unbroken.LaunchBox.Plugins.Services.Notifications;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// Sends bridge diagnostics to the plugin's own log file and to LaunchBox's
/// debug log, and user-facing problems to LaunchBox notifications. Big Box has
/// no notification system, so there they only reach the logs.
/// </summary>
internal sealed class PluginLog(FileLog file, IPluginLogger? host, INotificationManager? notifications) : IBridgeLog
{
    public void Log(BridgeLogLevel level, string message, Exception? exception = null)
    {
        file.Write(level, message, exception);
        try
        {
            if (host is { IsEnabled: true })
            {
                host.Log($"[launchbox-mqtt] {level}: {message}{(exception is null ? string.Empty : " (" + exception.Message + ")")}");
            }
        }
#pragma warning disable CA1031 // The host logger must not take the plugin down.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    public void NotifyUser(string message)
    {
        Log(BridgeLogLevel.Warning, "Notify: " + message);
        if (notifications is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await notifications.ShowErrorAsync(
                    new PluginNotificationDisplayRequest(new PluginText(message.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal))),
                    CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Unavailable in Big Box by design; the message is already logged.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                file.Write(BridgeLogLevel.Debug, "Notification not shown.", ex);
            }
        });
    }
}
