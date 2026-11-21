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

/// <summary>What the plugin was given by LaunchBox at load time.</summary>
internal sealed record PluginContext(string DataDirectory, PluginLog Log, IPluginSecretStore? Secrets, SynchronizationContext? UiContext)
{
    public string SettingsPath => Path.Combine(DataDirectory, SettingsFile.FileName);
}

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
    private static readonly SemaphoreSlim Lifecycle = new(1, 1);
    private static PluginContext? _context;
    private static LaunchBoxBridge? _bridge;
    private static System.Threading.Timer? _statePoll;

    public static string Version { get; } =
        typeof(PluginHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static LaunchBoxBridge? Bridge => Volatile.Read(ref _bridge);

    public static PluginContext? Context => Volatile.Read(ref _context);

    public static void EnsureStarted(string dataDirectory, IPluginLogger? logger, IPluginSecretStore? secrets, INotificationManager? notifications)
    {
        PluginContext context;
        lock (Gate)
        {
            if (_context is not null)
            {
                return;
            }

            Directory.CreateDirectory(dataDirectory);
            var log = new PluginLog(new FileLog(Path.Combine(dataDirectory, "launchbox-mqtt.log")), logger, notifications);
            // LaunchBox constructs plugins on its UI thread; launching a game must happen there.
            context = new PluginContext(dataDirectory, log, secrets, SynchronizationContext.Current);
            Volatile.Write(ref _context, context);
        }

        context.Log.Log(BridgeLogLevel.Info, $"launchbox-mqtt {Version} loading in {Environment.ProcessPath} (data: {dataDirectory}, UI context: {context.UiContext?.GetType().Name ?? "none"}).");

        // Settings and the secret store are read off the UI thread; LaunchBox callbacks must stay brief.
        _ = Task.Run(() => RunLockedAsync(() => StartAsync(context)));
    }

    /// <summary>Stops the bridge and starts it again from the saved settings.</summary>
    public static Task ReloadAsync(string reason) =>
        Context is { } context
            ? RunLockedAsync(async () =>
            {
                context.Log.Log(BridgeLogLevel.Info, $"Reloading: {reason}.");
                await StopBridgeAsync(reason).ConfigureAwait(false);
                await StartAsync(context).ConfigureAwait(false);
            })
            : Task.CompletedTask;

    /// <summary>Called from LaunchBox's shutdown callback: bounded, never throws.</summary>
    public static void Stop(string reason)
    {
        try
        {
            StopBridgeAsync(reason).Wait(StopTimeout + TimeSpan.FromSeconds(1));
        }
#pragma warning disable CA1031 // Shutdown must not throw into LaunchBox; the Last Will covers a failed stop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Context?.Log.Log(BridgeLogLevel.Warning, "Stop failed.", ex);
        }
    }

    /// <summary>Runs an event forward, never letting an exception reach LaunchBox.</summary>
    public static void Forward(Action<LaunchBoxBridge> action, string name)
    {
        Context?.Log.Log(BridgeLogLevel.Debug, $"Event: {name}");
        ForwardQuietly(action, name);
    }

    public static async Task<string?> ReadPasswordAsync(CancellationToken cancellationToken)
    {
        if (Context?.Secrets is not { } secrets)
        {
            return null;
        }

        var bytes = await secrets.GetValueAsync(new PluginSecretStoreGetRequest(PasswordSecretKey), cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Stores the password and checks that it reads back.</summary>
    public static async Task WritePasswordAsync(string password, CancellationToken cancellationToken)
    {
        var secrets = Context?.Secrets
            ?? throw new InvalidOperationException("LaunchBox did not provide a secret store, so the password cannot be saved.");
        await secrets.SetValueAsync(new PluginSecretStoreSetRequest(PasswordSecretKey, Encoding.UTF8.GetBytes(password)), cancellationToken).ConfigureAwait(false);
        if (await ReadPasswordAsync(cancellationToken).ConfigureAwait(false) != password)
        {
            throw new InvalidOperationException("The password did not read back from LaunchBox's secret store.");
        }
    }

    public static async Task RemovePasswordAsync(CancellationToken cancellationToken)
    {
        if (Context?.Secrets is { } secrets)
        {
            await secrets.RemoveValueAsync(new PluginSecretStoreRemoveRequest(PasswordSecretKey), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunLockedAsync(Func<Task> action)
    {
        await Lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Lifecycle.Release();
        }
    }

    private static async Task StopBridgeAsync(string reason)
    {
        var bridge = Interlocked.Exchange(ref _bridge, null);
        Interlocked.Exchange(ref _statePoll, null)?.Dispose();
        if (bridge is null)
        {
            return;
        }

        Context?.Log.Log(BridgeLogLevel.Info, $"Stopping: {reason}.");
        await bridge.StopAsync(StopTimeout).ConfigureAwait(false);
        await bridge.DisposeAsync().ConfigureAwait(false);
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
#pragma warning disable CA1031 // LaunchBox callbacks and timer callbacks must never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Context?.Log.Log(BridgeLogLevel.Error, $"{name} failed.", ex);
        }
    }

    // Caller holds Lifecycle.
    private static async Task StartAsync(PluginContext context)
    {
        var log = context.Log;
        try
        {
            var file = context.SettingsPath;
            if (SettingsFile.WriteTemplateIfMissing(file, Environment.MachineName))
            {
                log.Log(BridgeLogLevel.Info, $"Wrote a starter settings file: {file}. Configure it from LaunchBox's Tools menu (LaunchBox MQTT settings…) or edit the file and restart.");
                return;
            }

            var content = SettingsFile.Load(file, Environment.MachineName);
            if (!content.Enabled)
            {
                log.Log(BridgeLogLevel.Info, $"Disabled in {file} (\"enabled\": false). Not connecting.");
                return;
            }

            string? password;
            if (content.PlaintextPassword is { } plaintext)
            {
                await WritePasswordAsync(plaintext, CancellationToken.None).ConfigureAwait(false);
                SettingsFile.RemovePassword(file);
                log.Log(BridgeLogLevel.Info, "Moved the MQTT password from the settings file into LaunchBox's secret store.");
                password = plaintext;
            }
            else
            {
                password = content.Settings.Username is null ? null : await ReadPasswordAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var settings = content.Settings with { Password = password };
            var errors = settings.Validate();
            if (errors.Count > 0)
            {
                log.NotifyUser($"LaunchBox MQTT is not running: {string.Join(" ", errors)} ({file})");
                return;
            }

            var bridge = new LaunchBoxBridge(settings, new MqttNetTransport(settings), log, Version, commands: new LaunchBoxCommandHandler(context.UiContext, log),
                artwork: new GameArtworkSource(log));
            SeedState(bridge);
            bridge.Start();
            Volatile.Write(ref _bridge, bridge);
            Volatile.Write(ref _statePoll, new System.Threading.Timer(_ => PollState(), null, AttractPollInterval, AttractPollInterval));
        }
#pragma warning disable CA1031 // A bad settings file must leave LaunchBox running, with the reason shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Log(BridgeLogLevel.Error, "Start failed.", ex);
            log.NotifyUser($"LaunchBox MQTT is not running: {ex.Message}");
        }
    }

    private static void SeedState(LaunchBoxBridge bridge)
    {
        if (PluginHelper.StateManager is not { } state)
        {
            return;
        }

        bridge.OnBigBoxRunningChanged(state.IsBigBox);
        bridge.OnLockedChanged(state.IsBigBox && state.IsBigBoxLocked);
        bridge.OnAttractModeChanged(state.IsBigBox && state.IsBigBoxInAttractMode);
    }

    // There is no system event for attract mode, so it is polled. The lock has events, but polling it too
    // corrects any missed transition.
    private static void PollState() =>
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
