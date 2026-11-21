using System.Globalization;
using LaunchBoxMqtt.Core.Mqtt;
using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Config;

/// <summary>What a settings form submission turned into.</summary>
/// <param name="Enabled">Whether the bridge should run.</param>
/// <param name="Settings">The parsed settings, without the password; null when there are errors.</param>
/// <param name="NewPassword">A password typed into the form, or null to keep the stored one.</param>
/// <param name="FieldErrors">Errors keyed by field id; empty when the values are usable.</param>
public sealed record SettingsFormSubmission(
    bool Enabled,
    BridgeSettings? Settings,
    string? NewPassword,
    IReadOnlyDictionary<string, string> FieldErrors)
{
    public bool IsValid => FieldErrors.Count == 0 && Settings is not null;
}

/// <summary>
/// The settings form as plain string values, independent of LaunchBox's form
/// types: the plugin shell maps these field ids to host-rendered controls.
/// The password is never pre-filled; a blank password keeps the stored one.
/// </summary>
public static class SettingsForm
{
    public const string Enabled = "enabled";
    public const string Host = "host";
    public const string Port = "port";
    public const string Tls = "tls";
    public const string CaCertificatePath = "caCertificatePath";
    public const string Username = "username";
    public const string Password = "password"; // pragma: allowlist secret (a field id, not a secret)
    public const string NodeName = "nodeName";
    public const string BaseTopic = "baseTopic";
    public const string DiscoveryPrefix = "discoveryPrefix";
    public const string DiscoveryEnabled = "discoveryEnabled";
    public const string CommandsEnabled = "commandsEnabled";

    /// <summary>Initial form values from the current settings (no password).</summary>
    public static IReadOnlyDictionary<string, string> ToValues(bool enabled, BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Enabled] = Bool(enabled),
            [Host] = settings.Host,
            [Port] = settings.EffectivePort.ToString(CultureInfo.InvariantCulture),
            [Tls] = Bool(settings.UseTls),
            [CaCertificatePath] = settings.CaCertificatePath ?? string.Empty,
            [Username] = settings.Username ?? string.Empty,
            [NodeName] = settings.NodeName,
            [BaseTopic] = settings.BaseTopic,
            [DiscoveryPrefix] = settings.DiscoveryPrefix,
            [DiscoveryEnabled] = Bool(settings.DiscoveryEnabled),
            [CommandsEnabled] = Bool(settings.CommandsEnabled),
        };
    }

    /// <summary>
    /// Parses and validates submitted values. <paramref name="hasStoredPassword"/>
    /// lets a blank password field mean "keep the saved one".
    /// </summary>
    public static SettingsFormSubmission Parse(IReadOnlyDictionary<string, string> values, string defaultNodeName, bool hasStoredPassword)
    {
        ArgumentNullException.ThrowIfNull(values);
        var lookup = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string Get(string key) => lookup.TryGetValue(key, out var v) ? v.Trim() : string.Empty;
        bool GetBool(string key, bool fallback) =>
            lookup.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

        var enabled = GetBool(Enabled, false);
        var tls = GetBool(Tls, false);
        int? port = null;
        var portText = Get(Port);
        if (portText.Length > 0)
        {
            if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535)
            {
                port = p;
            }
            else
            {
                errors[Port] = "Enter a port between 1 and 65535.";
            }
        }

        var host = Get(Host);
        if (enabled && host.Length == 0)
        {
            errors[Host] = "Enter the broker host name.";
        }

        var username = Get(Username);
        // Secrets are not trimmed: a password may legitimately start or end with a space.
        var password = lookup.TryGetValue(Password, out var pw) && pw.Length > 0 ? pw : null;
        if (username.Length == 0 && password is not null)
        {
            errors[Username] = "A password needs a username.";
        }

        if (enabled && username.Length > 0 && password is null && !hasStoredPassword)
        {
            errors[Password] = "Enter the password for this username.";
        }

        var ca = Get(CaCertificatePath);
        if (ca.Length > 0 && tls && !File.Exists(ca))
        {
            errors[CaCertificatePath] = "This file does not exist.";
        }

        var baseTopic = Get(BaseTopic) is { Length: > 0 } b ? b : TopicLayout.DefaultBaseTopic;
        if (TopicLayout.CheckPrefix(baseTopic) is { } baseError)
        {
            errors[BaseTopic] = $"The topic {baseError}.";
        }

        var discoveryPrefix = Get(DiscoveryPrefix) is { Length: > 0 } d ? d : TopicLayout.DefaultDiscoveryPrefix;
        if (TopicLayout.CheckPrefix(discoveryPrefix) is { } discoveryError)
        {
            errors[DiscoveryPrefix] = $"The prefix {discoveryError}.";
        }

        if (errors.Count > 0)
        {
            return new SettingsFormSubmission(enabled, null, password, errors);
        }

        var settings = new BridgeSettings
        {
            Host = host,
            Port = port,
            UseTls = tls,
            // A CA file only means something with TLS; drop it rather than fail on a hidden field.
            CaCertificatePath = tls && ca.Length > 0 ? ca : null,
            Username = username.Length > 0 ? username : null,
            NodeName = Get(NodeName) is { Length: > 0 } n ? n : defaultNodeName,
            BaseTopic = baseTopic,
            DiscoveryPrefix = discoveryPrefix,
            DiscoveryEnabled = GetBool(DiscoveryEnabled, true),
            CommandsEnabled = GetBool(CommandsEnabled, false),
        };
        return new SettingsFormSubmission(enabled, settings, password, errors);
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
