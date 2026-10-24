using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509;

namespace NativeNCA.Core.Crypto;

/// <summary>Human-oriented data extracted from a НУЦ РК certificate.</summary>
public sealed class CertificateInfo
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public string? CommonName { get; init; }
    public string? FullName { get; init; }
    public string? Iin { get; init; }
    public string? Bin { get; init; }
    public string? Organization { get; init; }
    public string? IssuerCommonName { get; init; }
    public required string SerialNumberHex { get; init; }
    public DateTime NotBefore { get; init; }
    public DateTime NotAfter { get; init; }
    /// <summary>Stored in the vault (VaultKeyItem.KeyAlgorithm); shown as Strings.Key_Algorithm_Gost.</summary>
    public const string GostAlgorithm = "ГОСТ"; // NoLoc: stored value, not UI text

    public required string Algorithm { get; init; } // GostAlgorithm or "RSA"
    public bool CanAuthenticate { get; init; }
    public bool CanSign { get; init; }

    /// <summary>"AUTH", "SIGN" or "BOTH" — the format stored in the vault.</summary>
    public string Usage => (CanAuthenticate, CanSign) switch
    {
        (true, true) => "BOTH",
        (true, false) => "AUTH",
        _ => "SIGN"
    };

    public static CertificateInfo From(X509Certificate cert)
    {
        var subject = cert.SubjectDN;
        string? cn = First(subject, X509Name.CN);
        string? patronymic = First(subject, X509Name.GivenName);
        string? serial = First(subject, X509Name.SerialNumber);
        string? ou = First(subject, X509Name.OU);

        var eku = cert.GetExtendedKeyUsage()?.Select(o => o.Id).ToHashSet() ?? new HashSet<string>();
        bool[]? keyUsage = cert.GetKeyUsage();

        string keyAlg = cert.CertificateStructure.SubjectPublicKeyInfo.Algorithm.Algorithm.Id;

        bool canAuth = eku.Contains(KzOids.EkuClientAuth);
        bool canSign = eku.Contains(KzOids.EkuEmailProtection);

        // Since 2024 НУЦ issues a single GOST 2015 key per person (GOST512_*.p12) that is used both
        // for login and for signing, even though its EKU lists only emailProtection. Only the legacy
        // RSA pair (AUTH_RSA256_* / RSA256_*) is split by purpose.
        bool digitalSignature = keyUsage != null && keyUsage.Length > 0 && keyUsage[0];
        if (keyAlg == KzOids.Gost2015PublicKey && digitalSignature)
        {
            canAuth = true;
            canSign = true;
        }

        if (!canAuth && !canSign)
        {
            // No EKU: fall back to keyUsage (nonRepudiation => signing key).
            canSign = keyUsage != null && keyUsage.Length > 1 && keyUsage[1];
            canAuth = !canSign;
        }

        return new CertificateInfo
        {
            Subject = subject.ToString(),
            Issuer = cert.IssuerDN.ToString(),
            CommonName = cn,
            FullName = cn == null ? null : patronymic == null ? cn : $"{cn} {patronymic}",
            Iin = StripPrefix(serial, "IIN"),
            Bin = StripPrefix(ou, "BIN"),
            Organization = First(subject, X509Name.O),
            IssuerCommonName = First(cert.IssuerDN, X509Name.CN),
            SerialNumberHex = cert.SerialNumber.ToString(16),
            NotBefore = cert.NotBefore.ToUniversalTime(),
            NotAfter = cert.NotAfter.ToUniversalTime(),
            Algorithm = keyAlg == KzOids.RsaEncryption ? "RSA" : GostAlgorithm,
            CanAuthenticate = canAuth,
            CanSign = canSign
        };
    }

    public static string ToPem(X509Certificate cert)
    {
        var sb = new StringBuilder("-----BEGIN CERTIFICATE-----\n");
        string b64 = Convert.ToBase64String(cert.GetEncoded());
        for (int i = 0; i < b64.Length; i += 64)
            sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        return sb.Append("-----END CERTIFICATE-----").ToString();
    }

    private static string? First(X509Name name, DerObjectIdentifier oid)
    {
        var values = name.GetValueList(oid);
        return values.Count > 0 ? values[0] : null;
    }

    private static string? StripPrefix(string? value, string prefix)
    {
        if (value == null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = value[prefix.Length..];
        return rest.Length == 12 && rest.All(char.IsDigit) ? rest : null;
    }
}
