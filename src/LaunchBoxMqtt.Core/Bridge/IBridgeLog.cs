namespace LaunchBoxMqtt.Core.Bridge;

public enum BridgeLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Where the bridge reports. The plugin shell maps this to LaunchBox's
/// <c>IPluginLogger</c> and <c>INotificationManager</c>.
/// </summary>
public interface IBridgeLog
{
    void Log(BridgeLogLevel level, string message, Exception? exception = null);

    /// <summary>
    /// Something the user should see in the front end. The bridge calls this
    /// once per outage, never once per event.
    /// </summary>
    void NotifyUser(string message);
}
