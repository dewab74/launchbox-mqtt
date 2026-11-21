using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class CertificateValidationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lbmqtt-certs").FullName;
    private readonly X509Certificate2 _ca = TestCertificates.CreateCa("Test Root CA");
    private readonly X509Certificate2 _otherCa = TestCertificates.CreateCa("Some Other CA");

    public void Dispose()
    {
        _ca.Dispose();
        _otherCa.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Chain_to_the_custom_ca_is_trusted()
    {
        using var server = TestCertificates.CreateServer(_ca, "broker.test");
        var roots = CertificateValidation.LoadCaBundle(TestCertificates.WritePem(_dir, _ca));
        Assert.True(CertificateValidation.IsTrusted(server, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void Chain_to_another_ca_is_rejected()
    {
        using var server = TestCertificates.CreateServer(_otherCa, "broker.test");
        var roots = CertificateValidation.LoadCaBundle(TestCertificates.WritePem(_dir, _ca));
        Assert.False(CertificateValidation.IsTrusted(server, SslPolicyErrors.RemoteCertificateChainErrors, roots));
    }

    [Fact]
    public void Name_mismatch_is_never_excused()
    {
        using var server = TestCertificates.CreateServer(_ca, "broker.test");
        var roots = CertificateValidation.LoadCaBundle(TestCertificates.WritePem(_dir, _ca));
        var errors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
        Assert.False(CertificateValidation.IsTrusted(server, errors, roots));
    }

    [Fact]
    public void Chain_errors_without_a_custom_ca_are_rejected()
    {
        using var server = TestCertificates.CreateServer(_ca, "broker.test");
        Assert.False(CertificateValidation.IsTrusted(server, SslPolicyErrors.RemoteCertificateChainErrors, null));
    }

    [Fact]
    public void Clean_os_validation_passes_through() =>
        Assert.True(CertificateValidation.IsTrusted(null, SslPolicyErrors.None, null));

    [Fact]
    public void Bundle_with_several_certificates_loads_all()
    {
        var bundle = CertificateValidation.LoadCaBundle(TestCertificates.WritePem(_dir, _ca, _otherCa));
        Assert.Equal(2, bundle.Count);
    }

    [Fact]
    public void Empty_bundle_fails_loudly()
    {
        var path = Path.Combine(_dir, "empty.pem");
        File.WriteAllText(path, "not a certificate\n");
        Assert.Throws<InvalidOperationException>(() => CertificateValidation.LoadCaBundle(path));
    }
}
