using LaunchBoxMqtt.Core.Config;
using LaunchBoxMqtt.Core.Mqtt;
using MQTTnet;
using MQTTnet.Formatter;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class CertificateVerificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lbmqtt-verify").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static BridgeSettings Tls => new() { Host = "broker.lan", NodeName = "Arcade PC", UseTls = true };

    private static MqttClientTlsOptions TlsOptions(BridgeSettings settings) =>
        ((MqttClientTcpOptions)new MqttNetTransport(settings).BuildOptions(MqttProtocolVersion.V500).ChannelOptions).TlsOptions;

    [Fact]
    public void Verification_is_on_by_default() => Assert.True(new BridgeSettings { Host = "h", NodeName = "n" }.VerifyCertificate);

    [Fact]
    public void Verified_tls_without_a_ca_uses_the_windows_trust_store() =>
        Assert.Null(TlsOptions(Tls).CertificateValidationHandler);

    [Fact]
    public void Verification_off_accepts_any_certificate()
    {
        var handler = TlsOptions(Tls with { VerifyCertificate = false }).CertificateValidationHandler;
        Assert.NotNull(handler);
        Assert.True(handler(null!));
    }

    [Fact]
    public void Verification_off_shows_in_the_log_line() =>
        Assert.Contains("NOT verified", (Tls with { VerifyCertificate = false }).ToString(), StringComparison.Ordinal);

    [Fact]
    public void A_ca_with_verification_off_is_a_mistake() =>
        Assert.Contains(
            (Tls with { VerifyCertificate = false, CaCertificatePath = Path.Combine(_dir, "ca.pem") }).Validate(),
            e => e.Contains("verification is off", StringComparison.Ordinal));

    [Fact]
    public void The_settings_file_round_trips_the_flag()
    {
        var path = Path.Combine(_dir, SettingsFile.FileName);
        var settings = Tls with { VerifyCertificate = false };
        SettingsFile.Save(path, true, settings);
        Assert.False(SettingsFile.Load(path, "Arcade PC").Settings.VerifyCertificate);
        Assert.Contains("\"verifyCertificate\": false", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_window_drops_the_ca_when_verification_is_off()
    {
        var values = new Dictionary<string, string>(SettingsForm.ToValues(true, Tls))
        {
            [SettingsForm.VerifyCertificate] = "false",
            [SettingsForm.CaCertificatePath] = Path.Combine(_dir, "missing.pem"),
        };
        var parsed = SettingsForm.Parse(values, "Arcade PC", hasStoredPassword: false);
        Assert.True(parsed.IsValid);
        Assert.False(parsed.Settings!.VerifyCertificate);
        Assert.Null(parsed.Settings.CaCertificatePath);
    }
}
