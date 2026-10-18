using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Secondary.Shared.Connection;

// Each helper and device has its own self-signed certificate. Pairing records the other side's
// fingerprint, and later connections accept only that fingerprint; no certificate authority is involved.
public sealed class ConnectionIdentity : IDisposable
{
    private ConnectionIdentity(X509Certificate2 certificate, bool recreated)
    {
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
        Context = SslStreamCertificateContext.Create(certificate, null, offline: true);
        Recreated = recreated;
    }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
    public SslStreamCertificateContext Context { get; }
    // True when the stored identity was unreadable and a new one was made; existing pairings no longer match.
    public bool Recreated { get; }

    public static ConnectionIdentity LoadOrCreate(string path, string name)
    {
        var recreated = false;
        if (File.Exists(path))
        {
            try { return new(X509CertificateLoader.LoadPkcs12FromFile(path, null), false); }
            catch (CryptographicException)
            {
                // Keep the unreadable file for inspection rather than overwrite it.
                File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt", true);
                recreated = true;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=Secondary " + Sanitize(name)), key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var bytes = created.Export(X509ContentType.Pkcs12);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        // The private key must stay readable by this user only.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
        // Reload from PKCS#12: Windows TLS cannot use the ephemeral key a fresh CertificateRequest holds.
        return new(X509CertificateLoader.LoadPkcs12(bytes, null), recreated);
    }

    public static string FingerprintOf(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    // The first 16 hex digits in groups of four, for people to compare.
    public static string Short(string fingerprint) =>
        fingerprint.Length < 16 ? fingerprint : string.Join(' ', fingerprint[..4], fingerprint[4..8], fingerprint[8..12], fingerprint[12..16]);
    private static string Sanitize(string name)
    {
        var clean = new string(Array.FindAll(name.ToCharArray(), c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'));
        return clean.Length == 0 ? "device" : clean.Length > 40 ? clean[..40] : clean;
    }
    public void Dispose() => Certificate.Dispose();
}
