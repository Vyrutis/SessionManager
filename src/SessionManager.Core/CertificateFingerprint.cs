using System.Security.Cryptography;

namespace SessionManager;

/// <summary>SHA-256 fingerprints of web certificates, for remembering a self-signed certificate the user chose to trust.</summary>
public static class CertificateFingerprint
{
    /// <summary>Upper-case hex without separators, e.g. "3FA2…".</summary>
    public static string Of(byte[] rawCertificate) => Convert.ToHexString(SHA256.HashData(rawCertificate));

    /// <summary>"3F:A2:…" in two lines, the way browsers show it.</summary>
    public static string Format(string hex)
    {
        var pairs = Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)).ToList();
        if (pairs.Count <= 16) return string.Join(":", pairs);
        return string.Join(":", pairs.Take(16)) + "\n" + string.Join(":", pairs.Skip(16));
    }

    public static bool Matches(string? trusted, string fingerprint) =>
        !string.IsNullOrEmpty(trusted) && string.Equals(trusted.Replace(":", "").Trim(), fingerprint, StringComparison.OrdinalIgnoreCase);
}
