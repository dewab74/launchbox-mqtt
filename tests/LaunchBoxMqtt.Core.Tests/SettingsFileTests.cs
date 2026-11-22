using System.Text.Json.Nodes;
using LaunchBoxMqtt.Core.Config;
using LaunchBoxMqtt.Core.State;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lbmqtt-settings").FullName;

    private string FilePath => Path.Combine(_dir, SettingsFile.FileName);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Template_is_written_once_and_loads_as_disabled()
    {
        Assert.True(SettingsFile.WriteTemplateIfMissing(FilePath, "Arcade PC"));
        Assert.False(SettingsFile.WriteTemplateIfMissing(FilePath, "OTHER"));

        var content = SettingsFile.Load(FilePath, "Arcade PC");

        Assert.False(content.Enabled);
        Assert.Null(content.PlaintextPassword);
        Assert.Equal("Arcade PC", content.Settings.NodeName);
        Assert.Contains(content.Settings.Validate(), e => e.Contains("host", StringComparison.OrdinalIgnoreCase));
        Assert.False(content.Settings.CommandsEnabled);
    }

    [Fact]
    public void Full_file_maps_every_setting()
    {
        File.WriteAllText(FilePath, """
            {
              // comments and trailing commas are allowed
              "enabled": true,
              "host": "broker.lan",
              "port": "8884",
              "tls": true,
              "username": "launchbox",
              "nodeName": "Den PC",
              "baseTopic": "games",
              "discoveryPrefix": "ha",
              "discoveryEnabled": false,
            }
            """);

        var content = SettingsFile.Load(FilePath, "IGNORED");
        var s = content.Settings;

        Assert.True(content.Enabled);
        Assert.Equal(("broker.lan", 8884, true, "launchbox"), (s.Host, s.EffectivePort, s.UseTls, s.Username));
        Assert.Equal(("Den PC", "den_pc", "games", "ha", false), (s.NodeName, s.NodeId, s.BaseTopic, s.DiscoveryPrefix, s.DiscoveryEnabled));
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Password_is_reported_then_removed_from_the_file()
    {
        File.WriteAllText(FilePath, """{ "enabled": true, "host": "b", "username": "u", "password": "s3cret-test-value" }"""); // pragma: allowlist secret (test value)

        var content = SettingsFile.Load(FilePath, "PC");
        Assert.Equal("s3cret-test-value", content.PlaintextPassword); // pragma: allowlist secret (test value)

        SettingsFile.RemovePassword(FilePath);

        Assert.DoesNotContain("s3cret", File.ReadAllText(FilePath), StringComparison.Ordinal);
        var reloaded = SettingsFile.Load(FilePath, "PC");
        Assert.Null(reloaded.PlaintextPassword);
        Assert.Equal("u", reloaded.Settings.Username);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("""{ "Host": "b" }""", "unknown setting")]
    [InlineData("""{ "port": "eighty" }""", "'port'")]
    [InlineData("""{ "tls": "yes" }""", "'tls'")]
    [InlineData("""[1, 2]""", "JSON object")]
    [InlineData("""{ "host": """, "not valid JSON")]
    public void Mistakes_fail_loudly_with_the_reason(string json, string expected)
    {
        File.WriteAllText(FilePath, json);
        var ex = Assert.Throws<InvalidDataException>(() => SettingsFile.Load(FilePath, "PC"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_node_name_falls_back_to_the_machine_name()
    {
        File.WriteAllText(FilePath, """{ "host": "b", "nodeName": " " }""");
        Assert.Equal("Arcade PC", SettingsFile.Load(FilePath, "Arcade PC").Settings.NodeName);
    }

    [Fact]
    public void Template_is_valid_json_with_only_known_keys()
    {
        SettingsFile.WriteTemplateIfMissing(FilePath, "PC");
        var keys = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject().Select(p => p.Key);
        Assert.Contains("password", keys);
        _ = SettingsFile.Load(FilePath, "PC");
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("Shooter", new[] { "Shooter" })]
    [InlineData(" Shooter ;Arcade; ;shooter", new[] { "Shooter", "Arcade" })]
    public void Genres_are_split(string? input, string[] expected) =>
        Assert.Equal(expected, GameInfo.SplitGenres(input));
}
