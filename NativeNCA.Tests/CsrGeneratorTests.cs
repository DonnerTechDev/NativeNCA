using System.Security.Cryptography;
using System.Text;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Security;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Xunit;

namespace NativeNCA.Tests;

public class CsrGeneratorTests
{
    private static readonly Dictionary<string, string> TestDn = new()
    {
        ["2.5.4.3"] = "ТЕСТОВ ТЕСТ ТЕСТОВИЧ",
        ["2.5.4.5"] = "IIN900101300123",
        ["2.5.4.6"] = "KZ",
        ["2.5.4.7"] = "АСТАНА",
        ["2.5.4.10"] = "ТОО ТЕСТ",
        ["2.5.4.11"] = "BIN180340012345"
    };

    [Fact]
    public void GenerateGost2015_creates_valid_csr_structure()
    {
        var result = CsrGenerator.Generate(TestDn, algorithm: "GOST512", keyLength: 512);

        Assert.NotNull(result.CsrPem);
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", result.CsrPem.Trim());
        Assert.EndsWith("-----END CERTIFICATE REQUEST-----", result.CsrPem.Trim());
        Assert.Equal("GOST512", result.Prefix);
        Assert.Equal(KeyAlgorithmKind.Gost2015, result.Kind);
        Assert.Equal(40, result.KeyId.Length);

        // Parse PEM back into PKCS#10 CertificationRequest
        var reader = new PemReader(new StringReader(result.CsrPem));
        var pkcs10 = Assert.IsType<Pkcs10CertificationRequest>(reader.ReadObject());

        // Validate Subject
        var reqInfo = pkcs10.GetCertificationRequestInfo();
        var subjectStr = reqInfo.Subject.ToString();
        Assert.Contains("ТЕСТОВ ТЕСТ ТЕСТОВИЧ", subjectStr);
        Assert.Contains("900101300123", subjectStr);

        // Validate Algorithm Identifiers
        Assert.Equal(KzOids.Gost2015Signature512, pkcs10.SignatureAlgorithm.Algorithm.Id);
        Assert.Equal(KzOids.Gost2015PublicKey, reqInfo.SubjectPublicKeyInfo.Algorithm.Algorithm.Id);

        // Verify Signature
        byte[] tbs = reqInfo.GetDerEncoded();
        byte[] digest = Gost2015.Hash(tbs);
        byte[] sig = pkcs10.GetSignatureOctets();

        // Extract public key from SubjectPublicKeyInfo
        byte[] pubOctets = Asn1OctetString.GetInstance(
            Asn1Object.FromByteArray(reqInfo.SubjectPublicKeyInfo.PublicKey.GetBytes())).GetOctets();
        Assert.Equal(128, pubOctets.Length);

        var x = FromLe(pubOctets, 0, 64);
        var y = FromLe(pubOctets, 64, 64);
        var q = Gost2015.Domain.Curve.ValidatePoint(x, y);
        var pubKey = new ECPublicKeyParameters(q, Gost2015.Domain);

        bool isValid = Gost2015.VerifyDigest(pubKey, digest, sig);
        Assert.True(isValid, "GOST 2015 signature on CSR must verify");

        // Verify KeyId matches SHA-1 of public key bitstring bytes
        byte[] expectedKeyId = SHA1.HashData(reqInfo.SubjectPublicKeyInfo.PublicKey.GetBytes());
        Assert.Equal(Convert.ToHexString(expectedKeyId).ToLowerInvariant(), result.KeyId);
    }

    [Fact]
    public void GenerateRsa_creates_valid_csr_structure()
    {
        var result = CsrGenerator.Generate(TestDn, algorithm: "RSA", keyLength: 2048);

        Assert.NotNull(result.CsrPem);
        Assert.Equal("RSA", result.Prefix);
        Assert.Equal(KeyAlgorithmKind.Rsa, result.Kind);
        Assert.Equal(40, result.KeyId.Length);

        var reader = new PemReader(new StringReader(result.CsrPem));
        var pkcs10 = Assert.IsType<Pkcs10CertificationRequest>(reader.ReadObject());

        Assert.Equal(KzOids.Sha256WithRsa, pkcs10.SignatureAlgorithm.Algorithm.Id);

        var reqInfo = pkcs10.GetCertificationRequestInfo();
        byte[] tbs = reqInfo.GetDerEncoded();
        byte[] sig = pkcs10.GetSignatureOctets();

        var pubKey = PublicKeyFactory.CreateKey(reqInfo.SubjectPublicKeyInfo);
        var signer = SignerUtilities.GetSigner("SHA256WITHRSA");
        signer.Init(false, pubKey);
        signer.BlockUpdate(tbs, 0, tbs.Length);
        Assert.True(signer.VerifySignature(sig), "RSA signature on CSR must verify");
    }

    [Fact]
    public void Generate_with_email_and_sans_adds_extension_request()
    {
        var sans = new[] { "test.domain.kz", "alt@test.kz" };
        var result = CsrGenerator.Generate(TestDn, email: "user@test.kz", subjectAltNames: sans, algorithm: "GOST512");

        var reader = new PemReader(new StringReader(result.CsrPem));
        var pkcs10 = Assert.IsType<Pkcs10CertificationRequest>(reader.ReadObject());
        var attrs = pkcs10.GetCertificationRequestInfo().Attributes;

        Assert.NotNull(attrs);
        Assert.NotEmpty(attrs);

        // Find extensionRequest attribute
        bool foundSan = false;
        foreach (Asn1Encodable attr in attrs)
        {
            var seq = Asn1Sequence.GetInstance(attr);
            if (PkcsObjectIdentifiers.Pkcs9AtExtensionRequest.Equals(seq[0]))
            {
                var extSet = Asn1Set.GetInstance(seq[1]);
                var extensions = X509Extensions.GetInstance(extSet[0]);
                var sanExt = extensions.GetExtension(X509Extensions.SubjectAlternativeName);
                if (sanExt != null)
                {
                    foundSan = true;
                    var gn = GeneralNames.GetInstance(sanExt.GetParsedValue());
                    var names = gn.GetNames().Select(n => n.Name.ToString()).ToList();
                    Assert.Contains("user@test.kz", names);
                    Assert.Contains("test.domain.kz", names);
                    Assert.Contains("alt@test.kz", names);
                }
            }
        }

        Assert.True(foundSan, "CSR must contain SubjectAlternativeName in extensionRequest");
    }

    [Fact]
    public void Subject_country_encoded_as_printable_string()
    {
        var subject = CsrGenerator.BuildSubject(TestDn);
        var seq = (Asn1Sequence)subject.ToAsn1Object();

        foreach (Asn1Encodable rdnSet in seq)
        {
            var rdn = (Asn1Set)rdnSet;
            var pair = (Asn1Sequence)rdn[0];
            var oid = (DerObjectIdentifier)pair[0];
            var val = pair[1];

            if (oid.Id == "2.5.4.6") // C=KZ
            {
                Assert.IsType<DerPrintableString>(val);
                Assert.Equal("KZ", ((DerPrintableString)val).GetString());
            }
            else
            {
                Assert.IsAssignableFrom<IAsn1String>(val);
            }
        }
    }

    private static Org.BouncyCastle.Math.BigInteger FromLe(byte[] data, int offset, int length)
    {
        var be = new byte[length];
        for (int i = 0; i < length; i++) be[i] = data[offset + length - 1 - i];
        return new Org.BouncyCastle.Math.BigInteger(1, be);
    }
}
