using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Config;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Plugin;

/// <summary>The outcome of a save or a connection test, for the settings window.</summary>
internal sealed record SettingsActionResult(bool Succeeded, string Message, IReadOnlyDictionary<string, string> FieldErrors)
{
    public static SettingsActionResult Ok(string message) => new(true, message, new Dictionary<string, string>());

    public static SettingsActionResult Fail(string message, IReadOnlyDictionary<string, string>? fieldErrors = null) =>
        new(false, message, fieldErrors ?? new Dictionary<string, string>());
}

/// <summary>
/// Load, save and test for the settings window, independent of the UI
/// toolkit. Values use the field ids in <see cref="SettingsForm"/>. The
/// password is never read back into the window; a blank one keeps the stored one.
/// </summary>
internal static class SettingsActions
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static async Task<(IReadOnlyDictionary<string, string> Values, bool HasPassword)> LoadAsync(CancellationToken cancellationToken)
    {
        var context = PluginHost.Context ?? throw new InvalidOperationException("The plugin has not finished loading.");
        bool enabled;
        BridgeSettings settings;
        try
        {
            var content = SettingsFile.Load(context.SettingsPath, Environment.MachineName);
            (enabled, settings) = (content.Enabled, content.Settings);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // A broken or missing file must not keep the user out of the window that can repair it.
            context.Log.Log(BridgeLogLevel.Warning, "Settings file unreadable; the settings window starts from defaults.", ex);
            (enabled, settings) = (false, new BridgeSettings { Host = string.Empty, NodeName = Environment.MachineName });
        }

        var hasPassword = await PluginHost.ReadPasswordAsync(cancellationToken).ConfigureAwait(false) is not null;
        return (SettingsForm.ToValues(enabled, settings), hasPassword);
    }

    /// <summary>Validates, stores the password, writes the file, and restarts the bridge with the new settings.</summary>
    public static async Task<SettingsActionResult> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var context = PluginHost.Context ?? throw new InvalidOperationException("The plugin has not finished loading.");
        var hasPassword = await PluginHost.ReadPasswordAsync(cancellationToken).ConfigureAwait(false) is not null;
        var submission = SettingsForm.Parse(values, Environment.MachineName, hasPassword);
        if (!submission.IsValid)
        {
            return SettingsActionResult.Fail("Check the highlighted settings.", submission.FieldErrors);
        }

        try
        {
            var settings = submission.Settings!;
            if (submission.NewPassword is { } password)
            {
                await PluginHost.WritePasswordAsync(password, cancellationToken).ConfigureAwait(false);
            }
            else if (settings.Username is null)
            {
                await PluginHost.RemovePasswordAsync(cancellationToken).ConfigureAwait(false);
            }

            SettingsFile.Save(context.SettingsPath, submission.Enabled, settings);
            context.Log.Log(BridgeLogLevel.Info, $"Settings saved from the settings window: {settings} enabled={submission.Enabled}.");
            await PluginHost.ReloadAsync("settings saved").ConfigureAwait(false);
            return SettingsActionResult.Ok(submission.Enabled ? "Saved. Publishing with the new settings." : "Saved. Publishing is off.");
        }
#pragma warning disable CA1031 // Report the failure in the window rather than crash LaunchBox.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            context.Log.Log(BridgeLogLevel.Error, "Saving settings failed.", ex);
            return SettingsActionResult.Fail($"Saving failed: {ex.Message}");
        }
    }

    /// <summary>Connects once with the given values, without saving them, under a separate client id.</summary>
    public static async Task<SettingsActionResult> TestAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var context = PluginHost.Context;
        var stored = await PluginHost.ReadPasswordAsync(cancellationToken).ConfigureAwait(false);
        var submission = SettingsForm.Parse(
            new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase) { [SettingsForm.Enabled] = "true" },
            Environment.MachineName,
            stored is not null);
        if (!submission.IsValid)
        {
            return SettingsActionResult.Fail("Check the highlighted settings.", submission.FieldErrors);
        }

        var settings = submission.Settings! with
        {
            Password = submission.NewPassword ?? (submission.Settings!.Username is null ? null : stored),
            // Its own client id, so the test cannot kick the running bridge off the broker.
            ClientId = $"launchbox-mqtt-test-{Guid.NewGuid():N}",
        };

        try
        {
            var transport = new MqttNetTransport(settings);
            await using var disposeTransport = transport.ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TestTimeout);
            await transport.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            context?.Log.Log(BridgeLogLevel.Info, $"Connection test succeeded: {settings} ({transport.NegotiatedProtocol}).");
            return SettingsActionResult.Ok($"Connected to {settings.Host}:{settings.EffectivePort} ({ProtocolName(transport)}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SettingsActionResult.Fail($"No answer from {settings.Host}:{settings.EffectivePort} within {TestTimeout.TotalSeconds:0} s.");
        }
#pragma warning disable CA1031 // Any failure is the answer to "does it connect?".
        catch (Exception ex)
#pragma warning restore CA1031
        {
            context?.Log.Log(BridgeLogLevel.Warning, $"Connection test failed: {settings}.", ex);
            return SettingsActionResult.Fail($"Could not connect: {Explain(ex)}");
        }
    }

    private static string ProtocolName(MqttNetTransport transport) =>
        transport.NegotiatedProtocol == MQTTnet.Formatter.MqttProtocolVersion.V500 ? "MQTT 5" : "MQTT 3.1.1";

    private static string Explain(Exception ex)
    {
        // Keep our own explanation (for example a certificate rejection) rather than MQTTnet's wrapper.
        if (ex is InvalidOperationException)
        {
            return ex.Message;
        }

        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }
}
