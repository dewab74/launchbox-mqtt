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

    /// <summary>
    /// Whether to trust the broker's certificate. <paramref name="presented"/> is
    /// the chain the TLS stack built from what the broker sent; its certificates
    /// supply the intermediates, since the custom root alone cannot reach a leaf
    /// issued by an intermediate.
    /// </summary>
    public static bool IsTrusted(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors, X509Certificate2Collection? customRoots) =>
        Check(certificate, presented, errors, customRoots) is null;

    /// <summary>Null when trusted, otherwise why not, for the log.</summary>
    public static string? Check(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors, X509Certificate2Collection? customRoots)
    {
        if (errors == SslPolicyErrors.None)
        {
            return null;
        }

        if (certificate is null || customRoots is null || customRoots.Count == 0)
        {
            return $"TLS validation failed ({errors}) and no CA certificate file is configured.";
        }

        // A name mismatch or missing certificate is never excused by a custom CA.
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return $"TLS validation failed: {errors}.";
        }

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(customRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (presented is not null)
        {
            chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
            foreach (var element in presented.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        if (chain.Build(leaf))
        {
            return null;
        }

        var status = string.Join(", ", chain.ChainStatus.Select(s => s.Status).Distinct());
        return $"Broker certificate '{leaf.Subject}' (issuer '{leaf.Issuer}') does not chain to the configured CA: {status}.";
    }
}
