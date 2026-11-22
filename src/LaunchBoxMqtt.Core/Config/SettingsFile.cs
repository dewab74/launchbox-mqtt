using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Config;

/// <summary>What <see cref="SettingsFile.Load"/> found.</summary>
/// <param name="Enabled">False until the user has filled the file in.</param>
/// <param name="Settings">The settings, without the password.</param>
/// <param name="PlaintextPassword">
/// A password still sitting in the file. The caller moves it into LaunchBox's
/// secret store and then calls <see cref="SettingsFile.RemovePassword"/>.
/// </param>
public sealed record SettingsFileContent(bool Enabled, BridgeSettings Settings, string? PlaintextPassword);

/// <summary>
/// The plugin's settings file, <c>launchbox-mqtt.json</c> in the plugin data
/// directory. It allows headless setup (a script can write it) and holds
/// everything except the password, which lives in the secret store: a password
/// written here is picked up once, moved, and removed from the file.
/// </summary>
public static class SettingsFile
{
    public const string FileName = "launchbox-mqtt.json";

    // Relaxed escaping keeps the file readable by hand ("LaunchBox's", not "LaunchBox\u0027s"); it is never embedded in HTML.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "enabled", "host", "port", "tls", "verifyCertificate", "caCertificatePath", "username", "password",
        "nodeName", "baseTopic", "discoveryPrefix", "discoveryEnabled", "commandsEnabled", "artworkEnabled", "artworkMaxWidth", "statsEnabled", "$comment",
    };

    /// <summary>Reads and checks the file. Throws <see cref="InvalidDataException"/> with a readable message when it is wrong.</summary>
    public static SettingsFileContent Load(string path, string defaultNodeName)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new InvalidDataException($"{path}: the top level must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path}: not valid JSON ({ex.Message}).", ex);
        }

        var unknown = root.Select(p => p.Key).Where(k => !KnownKeys.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            // A typo such as "Host" would otherwise be ignored and the default used instead.
            throw new InvalidDataException($"{path}: unknown setting(s) {string.Join(", ", unknown)}. Keys are case-sensitive.");
        }

        var nodeName = String(root, "nodeName", path);
        var settings = new BridgeSettings
        {
            Host = String(root, "host", path) ?? string.Empty,
            Port = Int(root, "port", path),
            UseTls = Bool(root, "tls", path) ?? false,
            VerifyCertificate = Bool(root, "verifyCertificate", path) ?? true,
            CaCertificatePath = String(root, "caCertificatePath", path),
            Username = String(root, "username", path),
            NodeName = string.IsNullOrWhiteSpace(nodeName) ? defaultNodeName : nodeName,
            BaseTopic = String(root, "baseTopic", path) ?? Topics.TopicLayout.DefaultBaseTopic,
            DiscoveryPrefix = String(root, "discoveryPrefix", path) ?? Topics.TopicLayout.DefaultDiscoveryPrefix,
            DiscoveryEnabled = Bool(root, "discoveryEnabled", path) ?? true,
            CommandsEnabled = Bool(root, "commandsEnabled", path) ?? false,
            ArtworkEnabled = Bool(root, "artworkEnabled", path) ?? false,
            ArtworkMaxWidth = Int(root, "artworkMaxWidth", path) ?? BridgeSettings.DefaultArtworkMaxWidth,
            StatsEnabled = Bool(root, "statsEnabled", path) ?? false,
        };

        var password = String(root, "password", path);
        return new SettingsFileContent(Bool(root, "enabled", path) ?? false, settings, string.IsNullOrEmpty(password) ? null : password);
    }

    /// <summary>Rewrites the file without its <c>password</c> key, then checks that it is gone.</summary>
    public static void RemovePassword(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        })!.AsObject();
        root.Remove("password");
        WriteAtomically(path, root.ToJsonString(WriteOptions));

        if (JsonNode.Parse(File.ReadAllText(path))!.AsObject().ContainsKey("password"))
        {
            throw new IOException($"{path}: the password is still in the file after rewriting it.");
        }
    }

    /// <summary>
    /// Writes the settings, never the password, and reads them back to check.
    /// Comments in the existing file are not kept.
    /// </summary>
    public static void Save(string path, bool enabled, BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = new JsonObject
        {
            ["$comment"] = "launchbox-mqtt settings, written by the settings window (LaunchBox: Tools > LaunchBox MQTT settings). The password is kept in LaunchBox's secret store, not here. Reference: https://github.com/dewab74/launchbox-mqtt#configuration",
            ["enabled"] = enabled,
            ["host"] = settings.Host,
            ["port"] = settings.EffectivePort,
            ["tls"] = settings.UseTls,
            ["verifyCertificate"] = settings.VerifyCertificate,
            ["caCertificatePath"] = settings.CaCertificatePath,
            ["username"] = settings.Username,
            ["nodeName"] = settings.NodeName,
            ["baseTopic"] = settings.BaseTopic,
            ["discoveryPrefix"] = settings.DiscoveryPrefix,
            ["discoveryEnabled"] = settings.DiscoveryEnabled,
            ["commandsEnabled"] = settings.CommandsEnabled,
            ["artworkEnabled"] = settings.ArtworkEnabled,
            ["artworkMaxWidth"] = settings.ArtworkMaxWidth,
            ["statsEnabled"] = settings.StatsEnabled,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        WriteAtomically(path, root.ToJsonString(WriteOptions));

        var back = Load(path, settings.NodeName);
        if (back.Enabled != enabled || back.Settings != settings with { Password = null, Port = settings.EffectivePort } || back.PlaintextPassword is not null)
        {
            throw new IOException($"{path}: the settings did not read back as written.");
        }
    }

    /// <summary>Writes a disabled starter file for the user to fill in. Never overwrites.</summary>
    public static bool WriteTemplateIfMissing(string path, string defaultNodeName)
    {
        if (File.Exists(path))
        {
            return false;
        }

        var template = new JsonObject
        {
            ["$comment"] = "launchbox-mqtt settings. Set enabled to true after filling in host. A password written here is moved to LaunchBox's secret store on the next start and removed from this file. Reference: https://github.com/dewab74/launchbox-mqtt#configuration",
            ["enabled"] = false,
            ["host"] = string.Empty,
            ["port"] = BridgeSettings.DefaultPort,
            ["tls"] = false,
            ["verifyCertificate"] = true,
            ["caCertificatePath"] = null,
            ["username"] = null,
            ["password"] = null,
            ["nodeName"] = defaultNodeName,
            ["baseTopic"] = Topics.TopicLayout.DefaultBaseTopic,
            ["discoveryPrefix"] = Topics.TopicLayout.DefaultDiscoveryPrefix,
            ["discoveryEnabled"] = true,
            ["commandsEnabled"] = false,
            ["artworkEnabled"] = false,
            ["artworkMaxWidth"] = BridgeSettings.DefaultArtworkMaxWidth,
            ["statsEnabled"] = false,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        WriteAtomically(path, template.ToJsonString(WriteOptions));
        return true;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }

    private static string? String(JsonObject root, string key, string path) =>
        root[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => throw new InvalidDataException($"{path}: '{key}' must be a string."),
        };

    private static bool? Bool(JsonObject root, string key, string path) =>
        root[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw new InvalidDataException($"{path}: '{key}' must be true or false."),
        };

    private static int? Int(JsonObject root, string key, string path) =>
        root[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<int>(out var i) => i,
            JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidDataException($"{path}: '{key}' must be a whole number."),
        };
}
