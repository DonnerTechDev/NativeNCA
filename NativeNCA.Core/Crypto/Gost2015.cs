using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.Rosstandart;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace NativeNCA.Core.Crypto;

/// <summary>
/// СТ РК ГОСТ Р 34.10-2015 (512 bit) as used by НУЦ РК.
///
/// Conventions (verified against a real НУЦ signature, the only valid combination out of 24):
///  - curve: TC26 512 paramSetA, hash: ГОСТ Р 34.11-2015 (Streebog-512);
///  - public key: X‖Y, each 64 bytes little-endian, wrapped in an OCTET STRING;
///  - signature: r‖s, each 64 bytes little-endian;
///  - the digest is interpreted as a little-endian integer (what ECGost3410Signer does internally);
///  - CMS messageDigest is the raw Streebog output.
/// </summary>
public static class Gost2015
{
    public const int CoordinateSize = 64;

    private static readonly Lazy<ECDomainParameters> LazyDomain = new(() =>
    {
        var x9 = Org.BouncyCastle.Asn1.CryptoPro.ECGost3410NamedCurves.GetByOid(
            RosstandartObjectIdentifiers.id_tc26_gost_3410_12_512_paramSetA);
        return new ECDomainParameters(x9.Curve, x9.G, x9.N, x9.H, x9.GetSeed());
    });

    public static ECDomainParameters Domain => LazyDomain.Value;

    public static byte[] Hash(byte[] data) => Hash(data, 0, data.Length);

    public static byte[] Hash(byte[] data, int offset, int length)
    {
        var digest = new Gost3411_2012_512Digest();
        digest.BlockUpdate(data, offset, length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }

    public static bool IsGost2015Certificate(X509Certificate cert) =>
        cert.CertificateStructure.SubjectPublicKeyInfo.Algorithm.Algorithm.Id == KzOids.Gost2015PublicKey;

    public static ECPublicKeyParameters GetPublicKey(X509Certificate cert)
    {
        var spki = cert.CertificateStructure.SubjectPublicKeyInfo;
        byte[] raw = Asn1OctetString.GetInstance(Asn1Object.FromByteArray(spki.PublicKey.GetBytes())).GetOctets();
        if (raw.Length != CoordinateSize * 2)
            throw new NotSupportedException($"Поддерживаются только ключи ГОСТ 2015 512 бит (длина ключа {raw.Length * 4} бит)");

        var x = FromLittleEndian(raw, 0, CoordinateSize);
        var y = FromLittleEndian(raw, CoordinateSize, CoordinateSize);
        var q = Domain.Curve.ValidatePoint(x, y);
        return new ECPublicKeyParameters(q, Domain);
    }

    /// <summary>
    /// Extracts the private scalar from a НУЦ PrivateKeyInfo. The exact wrapping is not documented,
    /// so we look for a 64-byte value and accept it only if d·G equals the certificate's public key
    /// (trying both byte orders). This makes a wrong guess impossible to go unnoticed.
    /// </summary>
    public static ECPrivateKeyParameters GetPrivateKey(PrivateKeyInfo pki, ECPublicKeyParameters expectedPublic)
    {
        foreach (var candidate in EnumerateCandidates(pki))
        {
            foreach (var d in new[] { new BigInteger(1, candidate), FromLittleEndian(candidate, 0, candidate.Length) })
            {
                if (d.SignValue <= 0 || d.CompareTo(Domain.N) >= 0) continue;
                if (Domain.G.Multiply(d).Normalize().Equals(expectedPublic.Q.Normalize()))
                    return new ECPrivateKeyParameters(d, Domain);
            }
        }

        throw new InvalidDataException("Закрытый ключ ГОСТ не соответствует сертификату или имеет неизвестный формат");
    }

    public static byte[] SignDigest(ECPrivateKeyParameters key, byte[] digest)
    {
        var signer = new ECGost3410Signer();
        signer.Init(true, new ParametersWithRandom(key, new SecureRandom()));
        var rs = signer.GenerateSignature(digest);

        var result = new byte[CoordinateSize * 2];
        WriteLittleEndian(rs[0], result, 0);
        WriteLittleEndian(rs[1], result, CoordinateSize);
        return result;
    }

    public static bool VerifyDigest(ECPublicKeyParameters key, byte[] digest, byte[] signature)
    {
        if (signature.Length != CoordinateSize * 2) return false;
        var r = FromLittleEndian(signature, 0, CoordinateSize);
        var s = FromLittleEndian(signature, CoordinateSize, CoordinateSize);

        var signer = new ECGost3410Signer();
        signer.Init(false, key);
        return signer.VerifySignature(digest, r, s);
    }

    public static AlgorithmIdentifier DigestAlgorithm =>
        new(new DerObjectIdentifier(KzOids.Gost2015Digest512), DerNull.Instance);

    public static AlgorithmIdentifier SignatureAlgorithm =>
        new(new DerObjectIdentifier(KzOids.Gost2015Signature512), DerNull.Instance);

    private static IEnumerable<byte[]> EnumerateCandidates(PrivateKeyInfo pki)
    {
        var queue = new Queue<Asn1Encodable>();
        queue.Enqueue(pki.PrivateKey);

        while (queue.Count > 0)
        {
            var item = queue.Dequeue().ToAsn1Object();
            switch (item)
            {
                case Asn1OctetString octets:
                    var bytes = octets.GetOctets();
                    if (bytes.Length == CoordinateSize) yield return bytes;
                    else if (TryParse(bytes) is { } nested) queue.Enqueue(nested);
                    break;
                case DerInteger integer:
                    var value = integer.Value.ToByteArrayUnsigned();
                    if (value.Length <= CoordinateSize) yield return Pad(value);
                    break;
                case Asn1Sequence seq:
                    foreach (var child in seq) queue.Enqueue(child);
                    break;
                case Asn1TaggedObject tagged:
                    queue.Enqueue(tagged.GetBaseObject());
                    break;
            }
        }
    }

    private static Asn1Object? TryParse(byte[] bytes)
    {
        try { return Asn1Object.FromByteArray(bytes); }
        catch { return null; }
    }

    private static byte[] Pad(byte[] value)
    {
        if (value.Length == CoordinateSize) return value;
        var padded = new byte[CoordinateSize];
        Buffer.BlockCopy(value, 0, padded, CoordinateSize - value.Length, value.Length);
        return padded;
    }

    private static BigInteger FromLittleEndian(byte[] data, int offset, int length)
    {
        var be = new byte[length];
        for (int i = 0; i < length; i++) be[i] = data[offset + length - 1 - i];
        return new BigInteger(1, be);
    }

    private static void WriteLittleEndian(BigInteger value, byte[] target, int offset)
    {
        byte[] be = value.ToByteArrayUnsigned();
        for (int i = 0; i < be.Length; i++) target[offset + i] = be[be.Length - 1 - i];
    }
}
