using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanConnect.Core.Net;

/// <summary>
/// This computer's cryptographic identity: a self-signed ECDSA certificate created on first run.
/// Other computers remember its fingerprint once paired, so nobody can impersonate it later.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private DeviceIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the certificate, lowercase hex. Also used as the device id.</summary>
    public string Fingerprint { get; }

    public static DeviceIdentity Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Lan Connect device", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false)); // server + client auth
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        // Round-trip through PKCS#12: Windows' TLS stack cannot use ephemeral in-memory keys.
        return FromPkcs12(ephemeral.Export(X509ContentType.Pkcs12));
    }

    public static DeviceIdentity FromPkcs12(byte[] pkcs12)
    {
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet;
        return new DeviceIdentity(X509CertificateLoader.LoadPkcs12(pkcs12, null, flags | X509KeyStorageFlags.Exportable));
    }

    public byte[] ExportPkcs12() => Certificate.Export(X509ContentType.Pkcs12);

    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>Short, human-friendly form for display ("3f9a-c21e").</summary>
    public static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length >= 8 ? $"{fingerprint[..4]}-{fingerprint[4..8]}" : fingerprint;

    public void Dispose() => Certificate.Dispose();
}
