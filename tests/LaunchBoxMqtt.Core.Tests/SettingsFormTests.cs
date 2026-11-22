using LaunchBoxMqtt.Core.Config;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class SettingsFormTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lbmqtt-form").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Dictionary<string, string> Valid() => new()
    {
        [SettingsForm.Enabled] = "true",
        [SettingsForm.Host] = " broker.lan ",
        [SettingsForm.Port] = "8883",
        [SettingsForm.Tls] = "true",
        [SettingsForm.CaCertificatePath] = "",
        [SettingsForm.Username] = "launchbox",
        [SettingsForm.Password] = "",
        [SettingsForm.NodeName] = "Arcade PC",
        [SettingsForm.BaseTopic] = "launchbox",
        [SettingsForm.DiscoveryPrefix] = "homeassistant",
        [SettingsForm.DiscoveryEnabled] = "true",
        [SettingsForm.CommandsEnabled] = "true",
        [SettingsForm.ArtworkEnabled] = "true",
        [SettingsForm.ArtworkMaxWidth] = "640",
        [SettingsForm.StatsEnabled] = "true",
    };

    [Fact]
    public void Valid_values_become_settings_and_blank_password_keeps_the_stored_one()
    {
        var result = SettingsForm.Parse(Valid(), "PC", hasStoredPassword: true);

        Assert.True(result.IsValid);
        Assert.True(result.Enabled);
        Assert.Null(result.NewPassword);
        Assert.Equal("broker.lan", result.Settings!.Host);
        Assert.Equal(8883, result.Settings.EffectivePort);
        Assert.Empty(result.Settings.Validate());
    }

    [Fact]
    public void Round_trip_through_values_is_lossless()
    {
        var original = SettingsForm.Parse(Valid(), "PC", hasStoredPassword: true).Settings!;
        var again = SettingsForm.Parse(
            new Dictionary<string, string>(SettingsForm.ToValues(true, original)), "PC", hasStoredPassword: true);
        Assert.Equal(original with { Port = original.EffectivePort }, again.Settings! with { Port = again.Settings.EffectivePort });
    }

    [Fact]
    public void Commands_default_to_off()
    {
        var values = Valid();
        values.Remove(SettingsForm.CommandsEnabled);
        Assert.False(SettingsForm.Parse(values, "PC", true).Settings!.CommandsEnabled);
        Assert.True(SettingsForm.Parse(Valid(), "PC", true).Settings!.CommandsEnabled);
    }

    [Fact]
    public void Values_never_contain_a_password()
    {
        var settings = new BridgeSettings { Host = "b", NodeName = "PC", Username = "u", Password = "never-shown" }; // pragma: allowlist secret (test value)
        var values = SettingsForm.ToValues(true, settings);
        Assert.False(values.ContainsKey(SettingsForm.Password));
        Assert.DoesNotContain("never-shown", values.Values);
    }

    [Fact]
    public void Typed_password_is_returned_untrimmed()
    {
        var values = Valid();
        values[SettingsForm.Password] = " pass word "; // pragma: allowlist secret (test value)
        Assert.Equal(" pass word ", SettingsForm.Parse(values, "PC", hasStoredPassword: false).NewPassword);
    }

    [Theory]
    [InlineData(SettingsForm.Host, "", SettingsForm.Host)]
    [InlineData(SettingsForm.Port, "70000", SettingsForm.Port)]
    [InlineData(SettingsForm.Port, "abc", SettingsForm.Port)]
    [InlineData(SettingsForm.BaseTopic, "a/#", SettingsForm.BaseTopic)]
    [InlineData(SettingsForm.DiscoveryPrefix, "/ha", SettingsForm.DiscoveryPrefix)]
    [InlineData(SettingsForm.CaCertificatePath, "/nonexistent/ca.pem", SettingsForm.CaCertificatePath)]
    [InlineData(SettingsForm.ArtworkMaxWidth, "10", SettingsForm.ArtworkMaxWidth)]
    [InlineData(SettingsForm.ArtworkMaxWidth, "4096", SettingsForm.ArtworkMaxWidth)]
    public void Bad_values_are_reported_on_their_field(string field, string value, string expectedErrorField)
    {
        var values = Valid();
        values[field] = value;
        var result = SettingsForm.Parse(values, "PC", hasStoredPassword: true);
        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains(expectedErrorField, result.FieldErrors.Keys);
    }

    [Fact]
    public void Username_without_any_password_is_an_error_when_enabled()
    {
        var result = SettingsForm.Parse(Valid(), "PC", hasStoredPassword: false);
        Assert.Contains(SettingsForm.Password, result.FieldErrors.Keys);
    }

    [Fact]
    public void Password_without_username_is_an_error()
    {
        var values = Valid();
        values[SettingsForm.Username] = "";
        values[SettingsForm.Password] = "x"; // pragma: allowlist secret (test value)
        Assert.Contains(SettingsForm.Username, SettingsForm.Parse(values, "PC", false).FieldErrors.Keys);
    }

    [Fact]
    public void Disabled_form_may_be_incomplete()
    {
        var values = Valid();
        values[SettingsForm.Enabled] = "false";
        values[SettingsForm.Host] = "";
        var result = SettingsForm.Parse(values, "PC", hasStoredPassword: false);
        Assert.True(result.IsValid);
        Assert.False(result.Enabled);
    }

    [Fact]
    public void Ca_path_is_dropped_when_tls_is_off_and_blank_fields_take_defaults()
    {
        var values = Valid();
        values[SettingsForm.Tls] = "false";
        values[SettingsForm.CaCertificatePath] = "/nonexistent/ca.pem";
        values[SettingsForm.Port] = "";
        values[SettingsForm.NodeName] = "";
        values[SettingsForm.BaseTopic] = "";
        var s = SettingsForm.Parse(values, "Arcade PC", hasStoredPassword: true).Settings!;
        Assert.Null(s.CaCertificatePath);
        Assert.Equal((1883, "Arcade PC", "launchbox"), (s.EffectivePort, s.NodeName, s.BaseTopic));
    }

    [Fact]
    public void Saved_file_loads_back_without_a_password()
    {
        var path = Path.Combine(_dir, SettingsFile.FileName);
        var settings = SettingsForm.Parse(Valid(), "PC", hasStoredPassword: true).Settings! with { Password = "not-saved" }; // pragma: allowlist secret (test value)

        SettingsFile.Save(path, enabled: true, settings);

        Assert.DoesNotContain("not-saved", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("LaunchBox's", File.ReadAllText(path), StringComparison.Ordinal);
        var loaded = SettingsFile.Load(path, "PC");
        Assert.True(loaded.Enabled);
        Assert.True(loaded.Settings.CommandsEnabled);
        Assert.True(loaded.Settings.ArtworkEnabled);
        Assert.Equal(640, loaded.Settings.ArtworkMaxWidth);
        Assert.True(loaded.Settings.StatsEnabled);
        Assert.Equal("broker.lan", loaded.Settings.Host);
        Assert.Null(loaded.PlaintextPassword);
    }
}
