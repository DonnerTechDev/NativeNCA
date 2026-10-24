using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace NativeNCA.Core.Crypto;

/// <summary>
/// Writes PKCS#12 in the layout НУЦ РК uses (PBE-SHA1-3DES key bag, PBE-SHA1-RC2-40 cert bag,
/// HMAC-SHA1 MAC). Works for any key algorithm because the private key is kept as an opaque PrivateKeyInfo.
/// </summary>
public static class Pkcs12Writer
{
    private const int Iterations = 2048;
    private static readonly SecureRandom Random = new();

    /// <summary>Re-encrypts a container with a new password. Throws <see cref="WrongPasswordException"/>.</summary>
    public static byte[] ChangePassword(byte[] pfx, string currentPassword, string newPassword)
    {
        var contents = Pkcs12Reader.Read(pfx, currentPassword);
        return Write(contents, newPassword);
    }

    public static byte[] Write(Pkcs12Contents contents, string password)
    {
        char[] pass = password.ToCharArray();
        byte[] keyId = Org.BouncyCastle.Security.DigestUtilities.CalculateDigest("SHA1", contents.Certificate.GetEncoded());
        var localKeyIdAttrs = new DerSet(new DerSequence(PkcsObjectIdentifiers.Pkcs9AtLocalKeyID, new DerSet(new DerOctetString(keyId))));

        // Private key bag
        var epki = EncryptedPrivateKeyInfoFactory.CreateEncryptedPrivateKeyInfo(
            PkcsObjectIdentifiers.PbeWithShaAnd3KeyTripleDesCbc.Id, pass, Salt(), Iterations, contents.PrivateKey);
        var keyBag = new SafeBag(PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag, epki.ToAsn1Object(), localKeyIdAttrs);
        var keyContents = new ContentInfo(PkcsObjectIdentifiers.Data, new DerOctetString(new DerSequence(keyBag).GetEncoded()));

        // Certificate bags (end-entity bound to the key via localKeyId)
        var certBags = new Asn1EncodableVector();
        foreach (var cert in contents.AllCertificates)
        {
            var bag = new CertBag(PkcsObjectIdentifiers.X509Certificate, new DerOctetString(cert.GetEncoded()));
            certBags.Add(new SafeBag(PkcsObjectIdentifiers.CertBag, bag.ToAsn1Object(),
                cert.Equals(contents.Certificate) ? localKeyIdAttrs : null));
        }

        var rc2 = new AlgorithmIdentifier(PkcsObjectIdentifiers.PbewithShaAnd40BitRC2Cbc, new Pkcs12PbeParams(Salt(), Iterations));
        var cipher = (IBufferedCipher)PbeUtilities.CreateEngine(rc2.Algorithm);
        cipher.Init(true, PbeUtilities.GenerateCipherParameters(rc2.Algorithm, pass, false, rc2.Parameters));
        byte[] encryptedCerts = cipher.DoFinal(new DerSequence(certBags).GetEncoded());
        var certContents = new ContentInfo(PkcsObjectIdentifiers.EncryptedData,
            new EncryptedData(PkcsObjectIdentifiers.Data, rc2, new BerOctetString(encryptedCerts)).ToAsn1Object());

        byte[] authSafe = new AuthenticatedSafe(new[] { keyContents, certContents }).GetEncoded(Asn1Encodable.Der);

        // MAC
        byte[] macSalt = Salt();
        var kdf = new Pkcs12ParametersGenerator(new Sha1Digest());
        kdf.Init(PbeParametersGenerator.Pkcs12PasswordToBytes(pass), macSalt, Iterations);
        var hmac = new HMac(new Sha1Digest());
        hmac.Init(kdf.GenerateDerivedMacParameters(160));
        hmac.BlockUpdate(authSafe, 0, authSafe.Length);
        byte[] mac = new byte[hmac.GetMacSize()];
        hmac.DoFinal(mac, 0);

        var macData = new MacData(
            new DigestInfo(new AlgorithmIdentifier(new DerObjectIdentifier("1.3.14.3.2.26"), DerNull.Instance), mac),
            macSalt, Iterations);

        return new Pfx(new ContentInfo(PkcsObjectIdentifiers.Data, new DerOctetString(authSafe)), macData)
            .GetEncoded(Asn1Encodable.Der);
    }

    private static byte[] Salt() => SecureRandom.GetNextBytes(Random, 20);
}
