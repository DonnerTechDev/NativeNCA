using System.Security.Cryptography;
using System.Text;

namespace NativeNCA.Core.Updates;

/// <summary>
/// Signatures of release zips. CI signs every zip with the release key (ECDSA P-256, SHA-256) and
/// uploads <c>&lt;zip&gt;.sig</c>: the base64 IEEE P1363 signature (r‖s) of a short text that names the
/// version, the architecture and the SHA-256 of the zip. The updater rebuilds that text from the
/// version and architecture it expects, so a valid signature for another version (a downgrade) or
/// another architecture does not verify. The private key lives only in the RELEASE_SIGNING_KEY secret.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>SubjectPublicKeyInfo (DER, base64) of the release signing key. See tools/ReleaseSigning.</summary>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4cfEuz3imTlan3yMFrM0oR+4/mjHwRroCm8SRQp7CaUfBRrpA1jgbzmJTCwzA4b06aAf6C2bKCKh7Cr8RNvS4Q==";

    public const string SignatureExtension = ".sig";

    /// <summary>The exact bytes that are signed.</summary>
    public static byte[] BuildMessage(AppVersion version, string architecture, ReadOnlySpan<byte> zipSha256)
    {
        if (zipSha256.Length != 32) throw new ArgumentException("SHA-256 expected", nameof(zipSha256));
        string text =
            "NativeNCA release signature v1\n" +
            $"version: {version}\n" +
            $"arch: {architecture}\n" +
            $"sha256: {Convert.ToHexStringLower(zipSha256)}\n";
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Signs with a PKCS#8 private key in PEM. Used by tools/ReleaseSigning and tests.</summary>
    public static string Sign(string privateKeyPem, AppVersion version, string architecture, ReadOnlySpan<byte> zipSha256)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        if (ecdsa.KeySize != 256) throw new CryptographicException("The release signing key must be ECDSA P-256");
        byte[] signature = ecdsa.SignData(BuildMessage(version, architecture, zipSha256), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    /// <summary>Checks a <c>.sig</c> file's text against the embedded release key.</summary>
    public static bool Verify(string signatureText, AppVersion version, string architecture, ReadOnlySpan<byte> zipSha256) =>
        Verify(signatureText, version, architecture, zipSha256, Convert.FromBase64String(PublicKeyBase64));

    public static bool Verify(string signatureText, AppVersion version, string architecture, ReadOnlySpan<byte> zipSha256,
        byte[] publicKeySpki)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64) return false;

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
        if (ecdsa.KeySize != 256) return false;
        return ecdsa.VerifyData(BuildMessage(version, architecture, zipSha256), signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Generates a new key pair: (PKCS#8 PEM private key, SPKI base64 public key).</summary>
    public static (string PrivateKeyPem, string PublicKeyBase64) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>SPKI base64 of the public half of a PEM private key.</summary>
    public static string GetPublicKey(string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }
}
