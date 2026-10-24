using NativeNCA.Core.Crypto;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace NativeNCA.Tests;

/// <summary>
/// Generates НУЦ-like test keys: a GOST 2015 / RSA key, a certificate with a KZ subject
/// (SERIALNUMBER=IIN…, OU=BIN…) and a PKCS#12 laid out the way НУЦ does it
/// (PBE-SHA1-3DES key bag, PBE-SHA1-RC2-40 cert bag, HMAC-SHA1 MAC).
/// </summary>
internal static class TestKeys
{
    public const string Password = "Qwerty12";
    private static readonly SecureRandom Random = new();

    public static X509Name KzSubject(string bin = "180340012345") => new(
        $"CN=ТЕСТОВ ТЕСТ, SURNAME=ТЕСТОВ, SERIALNUMBER=IIN900101300123, C=KZ, O=ТОО «ТЕСТ», OU=BIN{bin}, GIVENNAME=ТЕСТОВИЧ");

    public static byte[] CreateGostPfx(bool authAndSign = true, string password = Password)
    {
        var domain = Gost2015.Domain;
        BigInteger d;
        do { d = new BigInteger(domain.N.BitLength, Random); } while (d.SignValue == 0 || d.CompareTo(domain.N) >= 0);
        var q = domain.G.Multiply(d).Normalize();

        // Public key: OCTET STRING(X‖Y little-endian)
        byte[] pub = new byte[128];
        WriteLe(q.AffineXCoord.ToBigInteger(), pub, 0);
        WriteLe(q.AffineYCoord.ToBigInteger(), pub, 64);
        var algId = new AlgorithmIdentifier(new DerObjectIdentifier(KzOids.Gost2015PublicKey),
            new DerSequence(new DerObjectIdentifier(KzOids.Gost2015ParamSetA), new DerObjectIdentifier(KzOids.Gost2015Digest512)));
        var spki = new SubjectPublicKeyInfo(algId, new DerOctetString(pub).GetEncoded());

        var tbs = BuildTbs(spki, Gost2015.SignatureAlgorithm, authAndSign);
        byte[] sig = Gost2015.SignDigest(new ECPrivateKeyParameters(d, domain), Gost2015.Hash(tbs.GetEncoded()));
        var cert = new X509Certificate(new X509CertificateStructure(tbs, Gost2015.SignatureAlgorithm, new DerBitString(sig)));

        // Private key: little-endian scalar in an OCTET STRING, as observed in НУЦ containers
        byte[] priv = new byte[64];
        WriteLe(d, priv, 0);
        var pki = new PrivateKeyInfo(algId, new DerOctetString(priv));

        return BuildPfx(pki, cert, password);
    }

    public static byte[] CreateRsaPfx(string password = Password)
    {
        var gen = new RsaKeyPairGenerator();
        gen.Init(new KeyGenerationParameters(Random, 2048));
        var kp = gen.GenerateKeyPair();

        var sigAlg = new AlgorithmIdentifier(new DerObjectIdentifier(KzOids.Sha256WithRsa), DerNull.Instance);
        var tbs = BuildTbs(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(kp.Public), sigAlg, authAndSign: false);
        var signer = SignerUtilities.GetSigner("SHA256WITHRSA");
        signer.Init(true, kp.Private);
        byte[] tbsBytes = tbs.GetEncoded();
        signer.BlockUpdate(tbsBytes, 0, tbsBytes.Length);
        var cert = new X509Certificate(new X509CertificateStructure(tbs, sigAlg, new DerBitString(signer.GenerateSignature())));

        return BuildPfx(PrivateKeyInfoFactory.CreatePrivateKeyInfo(kp.Private), cert, password);
    }

    private static TbsCertificateStructure BuildTbs(SubjectPublicKeyInfo spki, AlgorithmIdentifier sigAlg, bool authAndSign)
    {
        var gen = new V3TbsCertificateGenerator();
        gen.SetSerialNumber(new DerInteger(new BigInteger(64, Random).Add(BigInteger.One)));
        gen.SetIssuer(new X509Name("CN=ҰЛТТЫҚ КУӘЛАНДЫРУШЫ ОРТАЛЫҚ (TEST), C=KZ"));
        gen.SetSubject(KzSubject());
        gen.SetStartDate(new Time(DateTime.UtcNow.AddDays(-1)));
        gen.SetEndDate(new Time(DateTime.UtcNow.AddYears(1)));
        gen.SetSubjectPublicKeyInfo(spki);
        gen.SetSignature(sigAlg);

        var ekus = authAndSign
            ? new[] { KeyPurposeID.id_kp_clientAuth, KeyPurposeID.id_kp_emailProtection }
            : new[] { KeyPurposeID.id_kp_emailProtection };
        var extGen = new X509ExtensionsGenerator();
        extGen.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(ekus));
        extGen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.NonRepudiation));
        gen.SetExtensions(extGen.Generate());
        return gen.GenerateTbsCertificate();
    }

    private static byte[] BuildPfx(PrivateKeyInfo pki, X509Certificate cert, string password)
    {
        char[] pass = password.ToCharArray();
        var localKeyId = new DerSet(new DerSequence(PkcsObjectIdentifiers.Pkcs9AtLocalKeyID,
            new DerSet(new DerOctetString(new byte[] { 1, 2, 3, 4 }))));

        // Key bag: PBE-SHA1-3DES
        byte[] salt = SecureRandom.GetNextBytes(Random, 20);
        var epki = EncryptedPrivateKeyInfoFactory.CreateEncryptedPrivateKeyInfo(
            PkcsObjectIdentifiers.PbeWithShaAnd3KeyTripleDesCbc.Id, pass, salt, 2048, pki);
        var keyBag = new SafeBag(PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag, epki.ToAsn1Object(), localKeyId);
        var keyContents = new ContentInfo(PkcsObjectIdentifiers.Data, new DerOctetString(new DerSequence(keyBag).GetEncoded()));

        // Cert bag inside encryptedData: PBE-SHA1-RC2-40
        var certBag = new SafeBag(PkcsObjectIdentifiers.CertBag,
            new CertBag(PkcsObjectIdentifiers.X509Certificate, new DerOctetString(cert.GetEncoded())).ToAsn1Object(), localKeyId);
        byte[] certSalt = SecureRandom.GetNextBytes(Random, 20);
        var rc2Alg = new AlgorithmIdentifier(PkcsObjectIdentifiers.PbewithShaAnd40BitRC2Cbc, new Pkcs12PbeParams(certSalt, 2048));
        var cipher = (IBufferedCipher)PbeUtilities.CreateEngine(rc2Alg.Algorithm);
        cipher.Init(true, PbeUtilities.GenerateCipherParameters(rc2Alg.Algorithm, pass, false, rc2Alg.Parameters));
        byte[] encCerts = cipher.DoFinal(new DerSequence(certBag).GetEncoded());
        var certContents = new ContentInfo(PkcsObjectIdentifiers.EncryptedData,
            new EncryptedData(PkcsObjectIdentifiers.Data, rc2Alg, new BerOctetString(encCerts)).ToAsn1Object());

        byte[] authSafe = new AuthenticatedSafe(new[] { keyContents, certContents }).GetEncoded();

        // MAC: HMAC-SHA1 with PKCS#12 KDF
        byte[] macSalt = SecureRandom.GetNextBytes(Random, 20);
        var kdf = new Pkcs12ParametersGenerator(new Sha1Digest());
        kdf.Init(PbeParametersGenerator.Pkcs12PasswordToBytes(pass), macSalt, 2048);
        var hmac = new HMac(new Sha1Digest());
        hmac.Init(kdf.GenerateDerivedMacParameters(160));
        hmac.BlockUpdate(authSafe, 0, authSafe.Length);
        byte[] mac = new byte[hmac.GetMacSize()];
        hmac.DoFinal(mac, 0);
        var macData = new MacData(new DigestInfo(new AlgorithmIdentifier(new DerObjectIdentifier("1.3.14.3.2.26"), DerNull.Instance), mac), macSalt, 2048);

        return new Pfx(new ContentInfo(PkcsObjectIdentifiers.Data, new DerOctetString(authSafe)), macData).GetEncoded();
    }

    private static void WriteLe(BigInteger value, byte[] target, int offset)
    {
        byte[] be = value.ToByteArrayUnsigned();
        for (int i = 0; i < be.Length; i++) target[offset + i] = be[be.Length - 1 - i];
    }
}
