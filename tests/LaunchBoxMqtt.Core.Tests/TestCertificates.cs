using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LaunchBoxMqtt.Core.Tests;

/// <summary>Throwaway CAs and server certificates, generated per test run.</summary>
internal static class TestCertificates
{
    public static X509Certificate2 CreateCa(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>An intermediate CA signed by <paramref name="root"/>, with its private key.</summary>
    public static X509Certificate2 CreateIntermediate(X509Certificate2 root, string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var publicOnly = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(20), RandomNumberGenerator.GetBytes(16));
        using var withKey = publicOnly.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>A server certificate for <paramref name="dnsName"/> signed by <paramref name="ca"/>, with its private key.</summary>
    public static X509Certificate2 CreateServer(X509Certificate2 ca, string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var serial = RandomNumberGenerator.GetBytes(16);
        using var publicOnly = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
        using var withKey = publicOnly.CopyWithPrivateKey(key);
        // Round-trip through PKCS#12 so SslStream can use the key on every platform.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    public static string WritePem(string directory, params X509Certificate2[] certificates)
    {
        var path = Path.Combine(directory, $"ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, string.Concat(certificates.Select(c => c.ExportCertificatePem() + "\n")));
        return path;
    }
}
