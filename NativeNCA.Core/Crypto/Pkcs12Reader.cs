using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using Org.BouncyCastle.X509;
using NativeNCA.Core.Localization;
using Strings = NativeNCA.Core.Localization.Strings;

namespace NativeNCA.Core.Crypto;

public class WrongPasswordException : Exception
{
    public WrongPasswordException() : base(Strings.Error_WrongPassword) { }
}

/// <summary>
/// Decrypted contents of a PKCS#12 container: the private key (not interpreted) and certificates.
/// </summary>
public sealed class Pkcs12Contents
{
    public required PrivateKeyInfo PrivateKey { get; init; }
    public required X509Certificate Certificate { get; init; }
    public required IReadOnlyList<X509Certificate> AllCertificates { get; init; }
}

/// <summary>
/// Minimal PKCS#12 reader that, unlike BouncyCastle's Pkcs12Store, does not try to interpret the
/// private key algorithm. This is required for НУЦ РК GOST keys whose OIDs BouncyCastle does not know.
/// Supports the layout НУЦ uses: HMAC-SHA* MAC, PBE-SHA1-3DES key bag, PBE-SHA1-RC2-40 cert bag.
/// </summary>
public static class Pkcs12Reader
{
    public static Pkcs12Contents Read(byte[] pfxBytes, string password)
    {
        var pfx = Pfx.GetInstance(Asn1Object.FromByteArray(pfxBytes));
        var authSafeContent = pfx.AuthSafe;
        if (!PkcsObjectIdentifiers.Data.Equals(authSafeContent.ContentType))
            throw new InvalidDataException(Strings.Crypto_Pkcs12Format);

        byte[] authSafeData = Asn1OctetString.GetInstance(authSafeContent.Content).GetOctets();
        char[] pass = password.ToCharArray();

        if (pfx.MacData != null && !VerifyMac(pfx.MacData, authSafeData, pass))
            throw new WrongPasswordException();

        var keys = new List<(PrivateKeyInfo Key, Asn1OctetString? LocalKeyId)>();
        var certs = new List<(X509Certificate Cert, Asn1OctetString? LocalKeyId)>();
        var certParser = new X509CertificateParser();

        foreach (var ci in AuthenticatedSafe.GetInstance(Asn1Object.FromByteArray(authSafeData)).GetContentInfo())
        {
            byte[] safeContentsBytes;
            if (PkcsObjectIdentifiers.Data.Equals(ci.ContentType))
            {
                safeContentsBytes = Asn1OctetString.GetInstance(ci.Content).GetOctets();
            }
            else if (PkcsObjectIdentifiers.EncryptedData.Equals(ci.ContentType))
            {
                var encData = EncryptedData.GetInstance(ci.Content);
                safeContentsBytes = PbeDecrypt(encData.EncryptionAlgorithm, pass, encData.Content.GetOctets());
            }
            else
            {
                continue;
            }

            foreach (var bagObj in Asn1Sequence.GetInstance(Asn1Object.FromByteArray(safeContentsBytes)))
            {
                var bag = SafeBag.GetInstance(bagObj);
                var localKeyId = GetLocalKeyId(bag);

                if (PkcsObjectIdentifiers.Pkcs8ShroudedKeyBag.Equals(bag.BagID))
                {
                    var epki = EncryptedPrivateKeyInfo.GetInstance(bag.BagValue);
                    var pki = PbeDecryptPrivateKey(epki, pass);
                    keys.Add((pki, localKeyId));
                }
                else if (PkcsObjectIdentifiers.KeyBag.Equals(bag.BagID))
                {
                    keys.Add((PrivateKeyInfo.GetInstance(bag.BagValue), localKeyId));
                }
                else if (PkcsObjectIdentifiers.CertBag.Equals(bag.BagID))
                {
                    var certBag = CertBag.GetInstance(bag.BagValue);
                    if (!PkcsObjectIdentifiers.X509Certificate.Equals(certBag.CertID)) continue;
                    byte[] certDer = Asn1OctetString.GetInstance(certBag.CertValue).GetOctets();
                    certs.Add((certParser.ReadCertificate(certDer), localKeyId));
                }
            }
        }

        if (keys.Count == 0)
            throw new InvalidDataException(Strings.Crypto_Pkcs12NoKey);
        if (certs.Count == 0)
            throw new InvalidDataException(Strings.Crypto_Pkcs12NoCertificate);

        var (key, keyId) = keys[0];

        // Prefer the certificate bound to the key via localKeyId, otherwise the first end-entity certificate.
        var endEntity =
            certs.FirstOrDefault(c => keyId != null && keyId.Equals(c.LocalKeyId)).Cert
            ?? certs.FirstOrDefault(c => c.Cert.GetBasicConstraints() < 0).Cert
            ?? certs[0].Cert;

        return new Pkcs12Contents
        {
            PrivateKey = key,
            Certificate = endEntity,
            AllCertificates = certs.Select(c => c.Cert).ToList()
        };
    }

    private static Asn1OctetString? GetLocalKeyId(SafeBag bag)
    {
        if (bag.BagAttributes == null) return null;
        foreach (var attrObj in bag.BagAttributes)
        {
            var attr = Asn1Sequence.GetInstance(attrObj);
            if (PkcsObjectIdentifiers.Pkcs9AtLocalKeyID.Equals(attr[0]))
                return Asn1OctetString.GetInstance(Asn1Set.GetInstance(attr[1])[0]);
        }
        return null;
    }

    private static bool VerifyMac(MacData macData, byte[] data, char[] password)
    {
        var algorithm = macData.Mac.DigestAlgorithm.Algorithm;
        IDigest digest = DigestUtilities.GetDigest(algorithm);

        var generator = new Pkcs12ParametersGenerator(digest);
        generator.Init(PbeParametersGenerator.Pkcs12PasswordToBytes(password), macData.GetSalt(), macData.IterationCount.IntValueExact);
        var keyParam = (KeyParameter)generator.GenerateDerivedMacParameters(digest.GetDigestSize() * 8);

        var hmac = new HMac(DigestUtilities.GetDigest(algorithm));
        hmac.Init(keyParam);
        hmac.BlockUpdate(data, 0, data.Length);
        byte[] result = new byte[hmac.GetMacSize()];
        hmac.DoFinal(result, 0);

        return Arrays.FixedTimeEquals(result, macData.Mac.Digest.GetOctets());
    }

    private static byte[] PbeDecrypt(Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier algId, char[] password, byte[] data)
    {
        var cipherParams = PbeUtilities.GenerateCipherParameters(algId.Algorithm, password, false, algId.Parameters);
        var cipher = (IBufferedCipher)PbeUtilities.CreateEngine(algId.Algorithm);
        cipher.Init(false, cipherParams);
        try
        {
            return cipher.DoFinal(data);
        }
        catch (InvalidCipherTextException)
        {
            throw new WrongPasswordException();
        }
    }

    private static PrivateKeyInfo PbeDecryptPrivateKey(EncryptedPrivateKeyInfo epki, char[] password)
    {
        try
        {
            return PrivateKeyInfoFactory.CreatePrivateKeyInfo(password, epki);
        }
        catch (InvalidCipherTextException)
        {
            throw new WrongPasswordException();
        }
    }
}
