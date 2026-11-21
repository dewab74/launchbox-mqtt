using System.Reflection;
using System.Text;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Config;
using LaunchBoxMqtt.Core.Mqtt;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Services.Logging;
using Unbroken.LaunchBox.Plugins.Services.Notifications;
using Unbroken.LaunchBox.Plugins.Services.Storage;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// One bridge per LaunchBox/Big Box process. LaunchBox may create the
/// entrypoint class more than once (once per plugin interface), so everything
/// lives here, created on first use.
/// </summary>
internal static class PluginHost
{
    internal const string PasswordSecretKey = "mqtt-password"; // pragma: allowlist secret (the key name, not a secret)
    private static readonly TimeSpan AttractPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static readonly Lock Gate = new();
    private static bool _initialised;
    private static LaunchBoxBridge? _bridge;
    private static PluginLog? _log;
    private static Timer? _statePoll;

    public static string Version { get; } =
        typeof(PluginHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static LaunchBoxBridge? Bridge => Volatile.Read(ref _bridge);

    public static void EnsureStarted(string dataDirectory, IPluginLogger? logger, IPluginSecretStore? secrets, INotificationManager? notifications)
    {
        lock (Gate)
        {
            if (_initialised)
            {
                return;
            }

            _initialised = true;
        }

        Directory.CreateDirectory(dataDirectory);
        var log = new PluginLog(new FileLog(Path.Combine(dataDirectory, "launchbox-mqtt.log")), logger, notifications);
        _log = log;
        log.Log(BridgeLogLevel.Info, $"launchbox-mqtt {Version} loading in {Environment.ProcessPath} (data: {dataDirectory}).");

        // Settings and the secret store are read off the UI thread; LaunchBox callbacks must stay brief.
        _ = Task.Run(() => StartAsync(dataDirectory, secrets, log));
    }

    public static void Stop(string reason)
    {
        var bridge = Interlocked.Exchange(ref _bridge, null);
        _statePoll?.Dispose();
        if (bridge is null)
        {
            return;
        }

        _log?.Log(BridgeLogLevel.Info, $"Stopping: {reason}.");
        try
        {
            // Called from LaunchBox's shutdown callback; bounded, and StopAsync never captures the UI context.
            bridge.StopAsync(StopTimeout).Wait(StopTimeout + TimeSpan.FromSeconds(1));
        }
#pragma warning disable CA1031 // Shutdown must not throw into LaunchBox; the Last Will covers a failed stop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Log(BridgeLogLevel.Warning, "Stop failed.", ex);
        }
    }

    /// <summary>Runs an event forward, never letting an exception reach LaunchBox.</summary>
    public static void Forward(Action<LaunchBoxBridge> action, string name)
    {
        _log?.Log(BridgeLogLevel.Debug, $"Event: {name}");
        try
        {
            if (Bridge is { } bridge)
            {
                action(bridge);
            }
        }
#pragma warning disable CA1031 // LaunchBox event callbacks must never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Log(BridgeLogLevel.Error, $"{name} failed.", ex);
        }
    }

    // The poll runs every two seconds; logging each run would drown the log.
    private static void ForwardQuietly(Action<LaunchBoxBridge> action, string name)
    {
        try
        {
            if (Bridge is { } bridge)
            {
                action(bridge);
            }
        }
#pragma warning disable CA1031 // Timer callbacks must never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Log(BridgeLogLevel.Error, $"{name} failed.", ex);
        }
    }

    private static async Task StartAsync(string dataDirectory, IPluginSecretStore? secrets, PluginLog log)
    {
        try
        {
            var file = Path.Combine(dataDirectory, SettingsFile.FileName);
            if (SettingsFile.WriteTemplateIfMissing(file, Environment.MachineName))
            {
                log.Log(BridgeLogLevel.Info, $"Wrote a starter settings file: {file}. Fill it in and restart LaunchBox.");
                return;
            }

            var content = SettingsFile.Load(file, Environment.MachineName);
            if (!content.Enabled)
            {
                log.Log(BridgeLogLevel.Info, $"Disabled in {file} (\"enabled\": false). Not connecting.");
                return;
            }

            var password = await ResolvePasswordAsync(file, content, secrets, log).ConfigureAwait(false);
            var settings = content.Settings with { Password = password };
            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                log.NotifyUser($"LaunchBox MQTT is not running: {string.Join(" ", errors)} ({file})");
                return;
            }

            var bridge = new LaunchBoxBridge(settings, new MqttNetTransport(settings), log, Version);
            SeedState(bridge);
            bridge.Start();
            Volatile.Write(ref _bridge, bridge);
            _statePoll = new Timer(_ => PollState(), null, AttractPollInterval, AttractPollInterval);
        }
#pragma warning disable CA1031 // A bad settings file must leave LaunchBox running, with the reason shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Log(BridgeLogLevel.Error, "Start failed.", ex);
            log.NotifyUser($"LaunchBox MQTT is not running: {ex.Message}");
        }
    }

    private static async Task<string?> ResolvePasswordAsync(string file, SettingsFileContent content, IPluginSecretStore? secrets, PluginLog log)
    {
        if (content.PlaintextPassword is { } plaintext)
        {
            if (secrets is null)
            {
                throw new InvalidOperationException("LaunchBox did not provide a secret store, so the password cannot be moved out of the settings file.");
            }

            await secrets.SetValueAsync(new PluginSecretStoreSetRequest(PasswordSecretKey, Encoding.UTF8.GetBytes(plaintext)), CancellationToken.None).ConfigureAwait(false);
            var stored = await secrets.GetValueAsync(new PluginSecretStoreGetRequest(PasswordSecretKey), CancellationToken.None).ConfigureAwait(false);
            if (stored is null || Encoding.UTF8.GetString(stored) != plaintext)
            {
                throw new InvalidOperationException("The password did not read back from LaunchBox's secret store; leaving it in the settings file.");
            }

            SettingsFile.RemovePassword(file);
            log.Log(BridgeLogLevel.Info, "Moved the MQTT password from the settings file into LaunchBox's secret store.");
            return plaintext;
        }

        if (secrets is null || string.IsNullOrEmpty(content.Settings.Username))
        {
            return null;
        }

        var bytes = await secrets.GetValueAsync(new PluginSecretStoreGetRequest(PasswordSecretKey), CancellationToken.None).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static void SeedState(LaunchBoxBridge bridge)
    {
        var state = PluginHelper.StateManager;
        if (state is null)
        {
            return;
        }

        bridge.OnBigBoxRunningChanged(state.IsBigBox);
        bridge.OnLockedChanged(state.IsBigBox && state.IsBigBoxLocked);
        bridge.OnAttractModeChanged(state.IsBigBox && state.IsBigBoxInAttractMode);
    }

    // There is no system event for attract mode, so it is polled. The lock has events, but polling it too
    // corrects any missed transition.
    private static void PollState()
    {
        ForwardQuietly(
            bridge =>
            {
                if (PluginHelper.StateManager is { IsBigBox: true } state)
                {
                    bridge.OnAttractModeChanged(state.IsBigBoxInAttractMode);
                    bridge.OnLockedChanged(state.IsBigBoxLocked);
                }
            },
            nameof(PollState));
    }
}
