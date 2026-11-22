using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

public class BridgeSettingsTests
{
    private static BridgeSettings Valid => new() { Host = "broker.lan", NodeName = "Arcade PC" };

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(Valid.Validate());
        Assert.Equal(1883, Valid.EffectivePort);
        Assert.Equal(8883, (Valid with { UseTls = true }).EffectivePort);
        Assert.Equal("arcade_pc", Valid.NodeId);
        Assert.Equal("launchbox-mqtt-arcade_pc", Valid.ClientId);
    }

    [Fact]
    public void Problems_are_all_reported()
    {
        var settings = Valid with
        {
            Host = " ",
            Port = 70000,
            BaseTopic = "a/#",
            DiscoveryPrefix = "/ha",
            Password = "pw", // pragma: allowlist secret (test value)
            CaCertificatePath = "/nonexistent/ca.pem",
            ArtworkMaxWidth = 10,
        };

        var errors = settings.Validate();

        Assert.Equal(8, errors.Count);
    }

    [Fact]
    public void ToString_never_contains_the_password()
    {
        var settings = Valid with { Username = "launchbox", Password = "hunter2-not-a-real-password", UseTls = true }; // pragma: allowlist secret (test value)
        var text = settings.ToString();
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("mqtts://launchbox@broker.lan:8883", text, StringComparison.Ordinal);
    }
}
