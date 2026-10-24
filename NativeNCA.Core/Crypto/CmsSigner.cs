using System.Security.Cryptography;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509;
using Attribute = Org.BouncyCastle.Asn1.Cms.Attribute;
using ContentInfo = Org.BouncyCastle.Asn1.Cms.ContentInfo;
using SignedData = Org.BouncyCastle.Asn1.Cms.SignedData;
using SignerInfo = Org.BouncyCastle.Asn1.Cms.SignerInfo;
using IssuerAndSerialNumber = Org.BouncyCastle.Asn1.Cms.IssuerAndSerialNumber;
using Time = Org.BouncyCastle.Asn1.Cms.Time;

namespace NativeNCA.Core.Crypto;

/// <summary>
/// Builds CMS SignedData in the same shape as Kalkan/NCALayer: one signer identified by
/// issuer+serial, signed attributes contentType, signingTime, signingCertificateV2, messageDigest,
/// and the signer certificate embedded.
/// </summary>
public static class CmsSigner
{
    public static byte[] Sign(SigningKey key, byte[] data, bool attached)
    {
        byte[] digest = key.Digest(data);
        return Build(key, digest, attached ? data : null);
    }

    /// <summary>Detached signature over an already computed digest (createCAdESFromBase64Hash, basics digested=true).</summary>
    public static byte[] SignDigest(SigningKey key, byte[] digest) => Build(key, digest, null);

    /// <summary>
    /// Turns a CAdES-BES signature into CAdES-T: requests a time-stamp over the signature value and
    /// stores it as the unsigned attribute id-aa-signatureTimeStampToken.
    /// </summary>
    public static async Task<byte[]> AddTimestampAsync(byte[] cms, ITimestampProvider tsa, CancellationToken ct = default)
    {
        var contentInfo = ContentInfo.GetInstance(Asn1Object.FromByteArray(cms));
        var signedData = SignedData.GetInstance(contentInfo.Content);
        var signerInfo = SignerInfo.GetInstance(signedData.SignerInfos[0]);

        byte[] token = await tsa.GetTokenAsync(signerInfo.Signature.GetOctets(), ct);

        var unsignedAttrs = new DerSet(new Attribute(
            PkcsObjectIdentifiers.IdAASignatureTimeStampToken,
            new DerSet(Asn1Object.FromByteArray(token))));

        var stamped = new SignerInfo(signerInfo.SignerID, signerInfo.DigestAlgorithm, signerInfo.SignedAttrs,
            signerInfo.SignatureAlgorithm, signerInfo.Signature, unsignedAttrs);

        var newSignedData = new SignedData(signedData.DigestAlgorithms, signedData.EncapContentInfo,
            signedData.Certificates, signedData.CRLs, new DerSet(stamped));

        return new ContentInfo(PkcsObjectIdentifiers.SignedData, newSignedData).GetEncoded(Asn1Encodable.Der);
    }

    private static byte[] Build(SigningKey key, byte[] messageDigest, byte[]? encapsulatedContent)
    {
        var cert = key.Certificate;

        var signedAttrs = new DerSet(new Asn1EncodableVector
        {
            new Attribute(CmsAttributes.ContentType, new DerSet(PkcsObjectIdentifiers.Data)),
            new Attribute(CmsAttributes.SigningTime, new DerSet(new Time(DateTime.UtcNow))),
            new Attribute(PkcsObjectIdentifiers.IdAASigningCertificateV2,
                new DerSet(new SigningCertificateV2(new[] { new EssCertIDv2(SHA256.HashData(cert.GetEncoded())) }))),
            new Attribute(CmsAttributes.MessageDigest, new DerSet(new DerOctetString(messageDigest)))
        });

        byte[] signature = key.Sign(signedAttrs.GetEncoded(Asn1Encodable.Der));

        var signerInfo = new SignerInfo(
            new SignerIdentifier(new IssuerAndSerialNumber(cert.IssuerDN, new DerInteger(cert.SerialNumber))),
            key.DigestAlgorithm,
            signedAttrs,
            key.SignatureAlgorithm,
            new DerOctetString(signature),
            null);

        var signedData = new SignedData(
            new DerSet(key.DigestAlgorithm),
            new ContentInfo(PkcsObjectIdentifiers.Data, encapsulatedContent != null ? new DerOctetString(encapsulatedContent) : null),
            new DerSet(cert.CertificateStructure),
            null,
            new DerSet(signerInfo));

        return new ContentInfo(PkcsObjectIdentifiers.SignedData, signedData).GetEncoded(Asn1Encodable.Der);
    }
}

/// <summary>Result of verifying a CMS signature.</summary>
public sealed record CmsVerificationResult(bool IsValid, X509Certificate? Signer, string? Error, DateTime? TimestampTime = null);

/// <summary>
/// CMS verification for GOST 2015 and RSA. Used for self-checks after signing and for tests.
/// Does not validate the certificate chain or revocation.
/// </summary>
public static class CmsVerifier
{
    public static CmsVerificationResult Verify(byte[] cms, byte[]? detachedContent = null)
    {
        try
        {
            var contentInfo = ContentInfo.GetInstance(Asn1Object.FromByteArray(cms));
            var signedData = SignedData.GetInstance(contentInfo.Content);

            byte[]? content = (signedData.EncapContentInfo.Content as Asn1OctetString)?.GetOctets() ?? detachedContent;

            var parser = new X509CertificateParser();
            var certs = signedData.Certificates?.Cast<Asn1Encodable>()
                .Select(c => parser.ReadCertificate(c.GetEncoded())).ToList() ?? new List<X509Certificate>();

            var signerInfo = SignerInfo.GetInstance(signedData.SignerInfos[0]);
            if (signerInfo.SignerID.ID is not IssuerAndSerialNumber isn)
                return new(false, null, "Unsupported signer identifier");

            var signer = certs.FirstOrDefault(c => c.SerialNumber.Equals(isn.SerialNumber.Value) && c.IssuerDN.Equivalent(isn.Issuer));
            if (signer == null) return new(false, null, "Signer certificate not found");

            string sigAlg = signerInfo.SignatureAlgorithm.Algorithm.Id;
            Func<byte[], byte[]> hash = sigAlg == KzOids.Gost2015Signature512 ? Gost2015.Hash : SHA256.HashData;
            byte[] signature = signerInfo.Signature.GetOctets();

            byte[] signedBytes;
            if (signerInfo.SignedAttrs != null)
            {
                byte[]? md = null;
                foreach (var a in signerInfo.SignedAttrs)
                {
                    var attr = Attribute.GetInstance(a);
                    if (attr.AttrType.Equals(CmsAttributes.MessageDigest))
                        md = Asn1OctetString.GetInstance(attr.AttrValues[0]).GetOctets();
                }

                if (md == null) return new(false, signer, "No messageDigest attribute");
                if (content != null && !md.AsSpan().SequenceEqual(hash(content)))
                    return new(false, signer, "Content hash does not match");

                signedBytes = signerInfo.SignedAttrs.GetEncoded(Asn1Encodable.Der);
            }
            else
            {
                if (content == null) return new(false, signer, "No signed content");
                signedBytes = content;
            }

            bool ok = sigAlg switch
            {
                KzOids.Gost2015Signature512 => Gost2015.VerifyDigest(Gost2015.GetPublicKey(signer), Gost2015.Hash(signedBytes), signature),
                _ => VerifyRsa(signer, signedBytes, signature)
            };

            if (!ok) return new(false, signer, "Signature is not valid");

            DateTime? timestamp = null;
            if (signerInfo.UnsignedAttrs != null)
            {
                foreach (var a in signerInfo.UnsignedAttrs)
                {
                    var attr = Attribute.GetInstance(a);
                    if (!attr.AttrType.Equals(PkcsObjectIdentifiers.IdAASignatureTimeStampToken)) continue;

                    if (VerifyTimestamp(attr.AttrValues[0], signature) is not { } time)
                        return new(false, signer, "Time stamp is not valid");
                    timestamp = time;
                }
            }

            return new(true, signer, null, timestamp);
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }

    /// <summary>Checks the token's signature and that it covers this signature value.</summary>
    private static DateTime? VerifyTimestamp(Asn1Encodable tokenContentInfo, byte[] signatureValue)
    {
        try
        {
            var token = new Org.BouncyCastle.Tsp.TimeStampToken(
                new Org.BouncyCastle.Cms.CmsSignedData(tokenContentInfo.GetEncoded(Asn1Encodable.Der)));

            var info = token.TimeStampInfo;
            byte[] expected = info.MessageImprintAlgOid == KzOids.Sha256
                ? SHA256.HashData(signatureValue)
                : Gost2015.Hash(signatureValue);
            if (!info.GetMessageImprintDigest().AsSpan().SequenceEqual(expected)) return null;

            var tsaCert = token.GetCertificates().EnumerateMatches(token.SignerID).FirstOrDefault();
            if (tsaCert == null) return null;
            token.Validate(tsaCert);
            return info.GenTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyRsa(X509Certificate signer, byte[] signedBytes, byte[] signature)
    {
        var verifier = Org.BouncyCastle.Security.SignerUtilities.GetSigner("SHA256WITHRSA");
        verifier.Init(false, signer.GetPublicKey());
        verifier.BlockUpdate(signedBytes, 0, signedBytes.Length);
        return verifier.VerifySignature(signature);
    }
}
