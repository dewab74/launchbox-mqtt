using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace LaunchBoxMqtt.Core.Mqtt;

/// <summary>
/// Validates a broker certificate against a private CA (for example a
/// step-ca or Mosquitto self-made chain) instead of the OS trust store. The
/// host name check is kept: only chain errors are re-evaluated.
/// </summary>
public static class CertificateValidation
{
    public static X509Certificate2Collection LoadCaBundle(string pemPath)
    {
        var bundle = new X509Certificate2Collection();
        bundle.ImportFromPemFile(pemPath);
        if (bundle.Count == 0)
        {
            throw new InvalidOperationException($"No certificates found in CA file '{pemPath}'.");
        }

        return bundle;
    }

    public static bool IsTrusted(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2Collection? customRoots)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null || customRoots is null || customRoots.Count == 0)
        {
            return false;
        }

        // A name mismatch or missing certificate is never excused by a custom CA.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(customRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }
}
