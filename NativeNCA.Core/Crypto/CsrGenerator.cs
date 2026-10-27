using System.Security.Cryptography;
using NativeNCA.Core.Localization;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace NativeNCA.Core.Crypto;

public sealed record GeneratedCsr
{
    public required string CsrPem { get; init; }
    public required string KeyId { get; init; } // 40 lowercase hex chars (SHA-1 of public key bytes)
    public required string Prefix { get; init; } // "GOST512" or "RSA"
    public required KeyAlgorithmKind Kind { get; init; }
    public required PrivateKeyInfo PrivateKeyInfo { get; init; }
    public required SubjectPublicKeyInfo PublicKeyInfo { get; init; }
}

/// <summary>
/// Generates cryptographic key pairs and PKCS#10 Certificate Signing Requests (CSR)
/// conforming to НУЦ РК (National Certification Authority of Kazakhstan) standards:
///  - СТ РК ГОСТ Р 34.10-2015 (512-bit, curve TC26-512 paramSetA, Streebog-512);
///  - RSA (2048-bit, SHA256withRSA).
/// </summary>
public static class CsrGenerator
{
    private static readonly SecureRandom Random = new();

    public static GeneratedCsr Generate(
        IDictionary<string, string> dn,
        string? email = null,
        IEnumerable<string>? subjectAltNames = null,
        string algorithm = "GOST512",
        int keyLength = 512)
    {
        var subject = BuildSubject(dn);
        var attributes = BuildAttributes(email, subjectAltNames);

        bool isRsa = algorithm.Contains("RSA", StringComparison.OrdinalIgnoreCase);
        return isRsa
            ? GenerateRsa(subject, attributes, keyLength > 0 ? keyLength : 2048)
            : GenerateGost2015(subject, attributes);
    }

    public static X509Name BuildSubject(IDictionary<string, string> dn)
    {
        var oids = new List<DerObjectIdentifier>();
        var values = new List<string>();

        foreach (var (oidStr, valStr) in dn)
        {
            if (string.IsNullOrWhiteSpace(oidStr) || string.IsNullOrWhiteSpace(valStr))
                continue;

            oids.Add(new DerObjectIdentifier(oidStr.Trim()));
            values.Add(valStr.Trim());
        }

        return new X509Name(oids, values);
    }

    private static Asn1Set BuildAttributes(string? email, IEnumerable<string>? subjectAltNames)
    {
        var sanList = new List<GeneralName>();

        if (!string.IsNullOrWhiteSpace(email))
        {
            sanList.Add(new GeneralName(GeneralName.Rfc822Name, email.Trim()));
        }

        if (subjectAltNames != null)
        {
            foreach (var san in subjectAltNames)
            {
                if (string.IsNullOrWhiteSpace(san)) continue;
                int tag = san.Contains('@') ? GeneralName.Rfc822Name : GeneralName.DnsName;
                sanList.Add(new GeneralName(tag, san.Trim()));
            }
        }

        if (sanList.Count == 0)
            return new DerSet();

        var extGen = new X509ExtensionsGenerator();
        extGen.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(sanList.ToArray()));

        var attrVector = new Asn1EncodableVector
        {
            new DerSequence(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(extGen.Generate()))
        };

        return new DerSet(attrVector);
    }

    private static GeneratedCsr GenerateGost2015(X509Name subject, Asn1Set attributes)
    {
        var domain = Gost2015.Domain;
        BigInteger d;
        do
        {
            d = new BigInteger(domain.N.BitLength, Random);
        }
        while (d.SignValue <= 0 || d.CompareTo(domain.N) >= 0);

        var q = domain.G.Multiply(d).Normalize();

        // Public key: OCTET STRING(X‖Y little-endian, each 64 bytes)
        byte[] pub = new byte[Gost2015.CoordinateSize * 2];
        WriteLittleEndian(q.AffineXCoord.ToBigInteger(), pub, 0);
        WriteLittleEndian(q.AffineYCoord.ToBigInteger(), pub, Gost2015.CoordinateSize);

        var algId = new AlgorithmIdentifier(new DerObjectIdentifier(KzOids.Gost2015PublicKey),
            new DerSequence(new DerObjectIdentifier(KzOids.Gost2015ParamSetA), new DerObjectIdentifier(KzOids.Gost2015Digest512)));
        var spki = new SubjectPublicKeyInfo(algId, new DerOctetString(pub).GetEncoded());

        // Private key: little-endian scalar in OCTET STRING
        byte[] priv = new byte[Gost2015.CoordinateSize];
        WriteLittleEndian(d, priv, 0);
        var pki = new PrivateKeyInfo(algId, new DerOctetString(priv));

        var reqInfo = new CertificationRequestInfo(subject, spki, attributes);
        byte[] tbs = reqInfo.GetDerEncoded();
        byte[] digest = Gost2015.Hash(tbs);
        byte[] sig = Gost2015.SignDigest(new ECPrivateKeyParameters(d, domain), digest);

        var sigAlg = new AlgorithmIdentifier(new DerObjectIdentifier(KzOids.Gost2015Signature512), DerNull.Instance);
        var csr = new CertificationRequest(reqInfo, sigAlg, new DerBitString(sig));

        byte[] pubKeyBytes = spki.PublicKey.GetBytes();
        byte[] keyIdBytes = SHA1.HashData(pubKeyBytes);
        string keyIdHex = Convert.ToHexString(keyIdBytes).ToLowerInvariant();

        return new GeneratedCsr
        {
            CsrPem = FormatPem(csr),
            KeyId = keyIdHex,
            Prefix = "GOST512",
            Kind = KeyAlgorithmKind.Gost2015,
            PrivateKeyInfo = pki,
            PublicKeyInfo = spki
        };
    }

    private static GeneratedCsr GenerateRsa(X509Name subject, Asn1Set attributes, int keyLength)
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new KeyGenerationParameters(Random, keyLength));
        var kp = gen.GenerateKeyPair();

        var spki = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(kp.Public);
        var pki = PrivateKeyInfoFactory.CreatePrivateKeyInfo(kp.Private);

        var reqInfo = new CertificationRequestInfo(subject, spki, attributes);
        byte[] tbs = reqInfo.GetDerEncoded();

        var signer = SignerUtilities.GetSigner("SHA256WITHRSA");
        signer.Init(true, kp.Private);
        signer.BlockUpdate(tbs, 0, tbs.Length);
        byte[] sig = signer.GenerateSignature();

        var sigAlg = new AlgorithmIdentifier(new DerObjectIdentifier(KzOids.Sha256WithRsa), DerNull.Instance);
        var csr = new CertificationRequest(reqInfo, sigAlg, new DerBitString(sig));

        byte[] pubKeyBytes = spki.PublicKey.GetBytes();
        byte[] keyIdBytes = SHA1.HashData(pubKeyBytes);
        string keyIdHex = Convert.ToHexString(keyIdBytes).ToLowerInvariant();

        return new GeneratedCsr
        {
            CsrPem = FormatPem(csr),
            KeyId = keyIdHex,
            Prefix = "RSA",
            Kind = KeyAlgorithmKind.Rsa,
            PrivateKeyInfo = pki,
            PublicKeyInfo = spki
        };
    }

    public static string FormatPem(CertificationRequest csr)
    {
        string base64 = Convert.ToBase64String(csr.GetDerEncoded(), Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN CERTIFICATE REQUEST-----\r\n{base64}\r\n-----END CERTIFICATE REQUEST-----\r\n";
    }

    private static void WriteLittleEndian(BigInteger value, byte[] target, int offset)
    {
        byte[] be = value.ToByteArrayUnsigned();
        for (int i = 0; i < be.Length; i++)
            target[offset + i] = be[be.Length - 1 - i];
    }
}
