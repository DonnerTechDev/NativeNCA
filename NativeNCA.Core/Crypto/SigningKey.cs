using System.Security.Cryptography;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using NativeNCA.Core.Localization;

namespace NativeNCA.Core.Crypto;

public enum KeyAlgorithmKind
{
    Gost2015,
    Rsa
}

/// <summary>
/// An unlocked private key together with its certificate. Algorithm-specific details
/// (digest, signature encoding, OIDs, XMLDSig URIs) live behind this abstraction.
/// </summary>
public abstract class SigningKey
{
    protected SigningKey(X509Certificate certificate, IReadOnlyList<X509Certificate> allCertificates)
    {
        Certificate = certificate;
        AllCertificates = allCertificates;
    }

    public X509Certificate Certificate { get; }
    public IReadOnlyList<X509Certificate> AllCertificates { get; }

    public abstract KeyAlgorithmKind Kind { get; }
    public abstract AlgorithmIdentifier DigestAlgorithm { get; }
    public abstract AlgorithmIdentifier SignatureAlgorithm { get; }
    public abstract string XmlSignatureMethod { get; }
    public abstract string XmlDigestMethod { get; }

    public abstract byte[] Digest(byte[] data);
    public abstract byte[] SignDigest(byte[] digest);
    public abstract bool VerifyDigest(byte[] digest, byte[] signature);

    public byte[] Sign(byte[] data) => SignDigest(Digest(data));

    /// <summary>Opens a PKCS#12 container and returns a key ready for signing.</summary>
    /// <exception cref="WrongPasswordException">The password is wrong.</exception>
    public static SigningKey Load(byte[] pfx, string password)
    {
        var contents = Pkcs12Reader.Read(pfx, password);
        string keyAlgorithm = contents.Certificate.CertificateStructure.SubjectPublicKeyInfo.Algorithm.Algorithm.Id;

        return keyAlgorithm switch
        {
            KzOids.Gost2015PublicKey => new Gost2015SigningKey(contents),
            KzOids.RsaEncryption => new RsaSigningKey(contents),
            KzOids.Gost2004PublicKey => throw new NotSupportedException(
                Strings.Crypto_LegacyGost),
            _ => throw new NotSupportedException(Loc.Format(Strings.Crypto_UnsupportedAlgorithm, keyAlgorithm))
        };
    }
}

public sealed class Gost2015SigningKey : SigningKey
{
    private readonly ECPrivateKeyParameters _privateKey;
    private readonly ECPublicKeyParameters _publicKey;

    internal Gost2015SigningKey(Pkcs12Contents contents) : base(contents.Certificate, contents.AllCertificates)
    {
        _publicKey = Gost2015.GetPublicKey(contents.Certificate);
        _privateKey = Gost2015.GetPrivateKey(contents.PrivateKey, _publicKey);
    }

    public override KeyAlgorithmKind Kind => KeyAlgorithmKind.Gost2015;
    public override AlgorithmIdentifier DigestAlgorithm => Gost2015.DigestAlgorithm;
    public override AlgorithmIdentifier SignatureAlgorithm => Gost2015.SignatureAlgorithm;
    public override string XmlSignatureMethod => KzOids.XmlGost2015Signature;
    public override string XmlDigestMethod => KzOids.XmlGost2015Digest;

    public override byte[] Digest(byte[] data) => Gost2015.Hash(data);
    public override byte[] SignDigest(byte[] digest) => Gost2015.SignDigest(_privateKey, digest);
    public override bool VerifyDigest(byte[] digest, byte[] signature) => Gost2015.VerifyDigest(_publicKey, digest, signature);
}

public sealed class RsaSigningKey : SigningKey
{
    private readonly RsaKeyParameters _privateKey;
    private readonly RsaKeyParameters _publicKey;

    internal RsaSigningKey(Pkcs12Contents contents) : base(contents.Certificate, contents.AllCertificates)
    {
        _privateKey = (RsaKeyParameters)PrivateKeyFactory.CreateKey(contents.PrivateKey);
        _publicKey = (RsaKeyParameters)contents.Certificate.GetPublicKey();

        if (!_privateKey.Modulus.Equals(_publicKey.Modulus))
            throw new InvalidDataException(Strings.Crypto_RsaKeyMismatch);
    }

    public override KeyAlgorithmKind Kind => KeyAlgorithmKind.Rsa;
    public override AlgorithmIdentifier DigestAlgorithm => new(new DerObjectIdentifier(KzOids.Sha256), DerNull.Instance);
    public override AlgorithmIdentifier SignatureAlgorithm => new(new DerObjectIdentifier(KzOids.Sha256WithRsa), DerNull.Instance);
    public override string XmlSignatureMethod => KzOids.XmlRsaSha256Signature;
    public override string XmlDigestMethod => KzOids.XmlSha256Digest;

    public override byte[] Digest(byte[] data) => SHA256.HashData(data);

    /// <summary>Native .NET RSA instance (used by SignedXml, which only accepts built-in RSA for rsa-sha256).</summary>
    public RSA ToDotNetRsa()
    {
        var p = (RsaPrivateCrtKeyParameters)_privateKey;
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = p.Modulus.ToByteArrayUnsigned(),
            Exponent = p.PublicExponent.ToByteArrayUnsigned(),
            D = Pad(p.Exponent, p.Modulus),
            P = Pad(p.P, p.P),
            Q = Pad(p.Q, p.P),
            DP = Pad(p.DP, p.P),
            DQ = Pad(p.DQ, p.P),
            InverseQ = Pad(p.QInv, p.P)
        });
        return rsa;

        static byte[] Pad(Org.BouncyCastle.Math.BigInteger value, Org.BouncyCastle.Math.BigInteger sizeOf)
        {
            int len = sizeOf.ToByteArrayUnsigned().Length;
            byte[] raw = value.ToByteArrayUnsigned();
            if (raw.Length == len) return raw;
            var padded = new byte[len];
            Buffer.BlockCopy(raw, 0, padded, len - raw.Length, raw.Length);
            return padded;
        }
    }

    public override byte[] SignDigest(byte[] digest)
    {
        var signer = new RsaDigestSigner(new NullDigestWithOid(), new DerObjectIdentifier(KzOids.Sha256));
        signer.Init(true, _privateKey);
        signer.BlockUpdate(digest, 0, digest.Length);
        return signer.GenerateSignature();
    }

    public override bool VerifyDigest(byte[] digest, byte[] signature)
    {
        var signer = new RsaDigestSigner(new NullDigestWithOid(), new DerObjectIdentifier(KzOids.Sha256));
        signer.Init(false, _publicKey);
        signer.BlockUpdate(digest, 0, digest.Length);
        return signer.VerifySignature(signature);
    }

    /// <summary>Passes a precomputed SHA-256 digest through RsaDigestSigner (PKCS#1 v1.5 DigestInfo).</summary>
    private sealed class NullDigestWithOid : IDigest
    {
        private readonly NullDigest _inner = new();
        public string AlgorithmName => "SHA-256";
        public int GetDigestSize() => 32;
        public int GetByteLength() => 64;
        public void Update(byte input) => _inner.Update(input);
        public void BlockUpdate(byte[] input, int inOff, int inLen) => _inner.BlockUpdate(input, inOff, inLen);
        public void BlockUpdate(ReadOnlySpan<byte> input) => _inner.BlockUpdate(input);
        public int DoFinal(byte[] output, int outOff) => _inner.DoFinal(output, outOff);
        public int DoFinal(Span<byte> output) => _inner.DoFinal(output);
        public void Reset() => _inner.Reset();
    }
}
