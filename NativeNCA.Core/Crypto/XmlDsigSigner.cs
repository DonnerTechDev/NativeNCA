using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using X509Certificate = Org.BouncyCastle.X509.X509Certificate;
using X509CertificateParser = Org.BouncyCastle.X509.X509CertificateParser;

namespace NativeNCA.Core.Crypto;

/// <summary>
/// Enveloped XMLDSig compatible with NCALayer's signXml: Reference URI="" with
/// enveloped-signature + C14N WithComments transforms, SignedInfo canonicalized with C14N,
/// KeyInfo/X509Data/X509Certificate. Canonicalization is done by .NET's SignedXml; GOST
/// algorithms are plugged in through CryptoConfig.
/// </summary>
public static class XmlDsigSigner
{
    static XmlDsigSigner()
    {
        CryptoConfig.AddAlgorithm(typeof(Gost2015SignatureDescription), KzOids.XmlGost2015Signature);
        CryptoConfig.AddAlgorithm(typeof(Streebog512HashAlgorithm), KzOids.XmlGost2015Digest);
    }

    public static string Sign(SigningKey key, string xml, string? tbsElementXPath = null, string? signatureParentXPath = null)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xml);

        var parent = SelectElement(doc, signatureParentXPath) ?? doc.DocumentElement
            ?? throw new InvalidDataException("Пустой XML-документ");

        string referenceUri = "";
        if (!string.IsNullOrWhiteSpace(tbsElementXPath))
        {
            var tbs = SelectElement(doc, tbsElementXPath) ?? throw new InvalidDataException($"Элемент {tbsElementXPath} не найден");
            string id = tbs.GetAttribute("Id") is { Length: > 0 } a ? a : tbs.GetAttribute("id");
            if (string.IsNullOrEmpty(id))
                throw new NotSupportedException("Подписываемый элемент должен иметь атрибут Id");
            referenceUri = "#" + id;
        }

        // Context = the parent element, so namespaces inherited by the Signature are canonicalized correctly.
        var signedXml = new SignedXml(parent)
        {
            SigningKey = key is RsaSigningKey rsa ? rsa.ToDotNetRsa() : new SigningKeyAlgorithm(key)
        };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = key.XmlSignatureMethod;

        var reference = new Reference(referenceUri) { DigestMethod = key.XmlDigestMethod };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NWithCommentsTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        var x509Data = new KeyInfoX509Data();
        x509Data.AddCertificate(X509CertificateLoader.LoadCertificate(key.Certificate.GetEncoded()));
        keyInfo.AddClause(x509Data);
        signedXml.KeyInfo = keyInfo;

        signedXml.ComputeSignature();
        parent.AppendChild(doc.ImportNode(signedXml.GetXml(), true));

        return doc.OuterXml;
    }

    /// <summary>Verifies the first ds:Signature in the document using the embedded certificate.</summary>
    public static bool Verify(string signedXml, out X509Certificate? signer)
    {
        signer = null;
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(signedXml);

        var sigElement = (XmlElement?)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Item(0);
        if (sigElement == null) return false;

        var certNode = sigElement.GetElementsByTagName("X509Certificate", SignedXml.XmlDsigNamespaceUrl).Item(0);
        if (certNode == null) return false;
        signer = new X509CertificateParser().ReadCertificate(Convert.FromBase64String(certNode.InnerText.Trim()));

        var parent = (XmlElement)sigElement.ParentNode!;
        var sx = new SignedXml(parent);
        sx.LoadXml(sigElement);

        AsymmetricAlgorithm verifyKey = Gost2015.IsGost2015Certificate(signer)
            ? new VerifyOnlyGostAlgorithm(signer)
            : X509CertificateLoader.LoadCertificate(signer.GetEncoded()).GetRSAPublicKey()!;

        return sx.CheckSignature(verifyKey);
    }

    private static XmlElement? SelectElement(XmlDocument doc, string? xpath) =>
        string.IsNullOrWhiteSpace(xpath) ? null : doc.SelectSingleNode(xpath) as XmlElement;

    // ---------- CryptoConfig plumbing for GOST ----------

    /// <summary>Wraps a GOST SigningKey so SignedXml can use it through Gost2015SignatureDescription.</summary>
    /// <summary>
    /// Common base so SignedXml's key-type check (SignatureDescription.KeyAlgorithm) accepts
    /// both the signing wrapper and the verify-only wrapper.
    /// </summary>
    public abstract class GostAlgorithmBase : AsymmetricAlgorithm;

    internal sealed class SigningKeyAlgorithm : GostAlgorithmBase
    {
        public SigningKeyAlgorithm(SigningKey key) => Key = key;
        public SigningKey Key { get; }
        public override string? SignatureAlgorithm => Key.XmlSignatureMethod;
    }

    internal sealed class VerifyOnlyGostAlgorithm : GostAlgorithmBase
    {
        public VerifyOnlyGostAlgorithm(X509Certificate cert) => PublicKey = Gost2015.GetPublicKey(cert);
        public Org.BouncyCastle.Crypto.Parameters.ECPublicKeyParameters PublicKey { get; }
    }

    public sealed class Gost2015SignatureDescription : SignatureDescription
    {
        public Gost2015SignatureDescription()
        {
            KeyAlgorithm = typeof(GostAlgorithmBase).AssemblyQualifiedName;
            DigestAlgorithm = typeof(Streebog512HashAlgorithm).AssemblyQualifiedName;
        }

        public override HashAlgorithm CreateDigest() => new Streebog512HashAlgorithm();
        public override AsymmetricSignatureFormatter CreateFormatter(AsymmetricAlgorithm key) => new Formatter(key);
        public override AsymmetricSignatureDeformatter CreateDeformatter(AsymmetricAlgorithm key) => new Deformatter(key);

        private sealed class Formatter(AsymmetricAlgorithm key) : AsymmetricSignatureFormatter
        {
            private AsymmetricAlgorithm _key = key;
            public override void SetKey(AsymmetricAlgorithm key) => _key = key;
            public override void SetHashAlgorithm(string strName) { }
            public override byte[] CreateSignature(byte[] rgbHash) =>
                ((SigningKeyAlgorithm)_key).Key.SignDigest(rgbHash);
        }

        private sealed class Deformatter(AsymmetricAlgorithm key) : AsymmetricSignatureDeformatter
        {
            private AsymmetricAlgorithm _key = key;
            public override void SetKey(AsymmetricAlgorithm key) => _key = key;
            public override void SetHashAlgorithm(string strName) { }
            public override bool VerifySignature(byte[] rgbHash, byte[] rgbSignature) => _key switch
            {
                VerifyOnlyGostAlgorithm v => Gost2015.VerifyDigest(v.PublicKey, rgbHash, rgbSignature),
                SigningKeyAlgorithm s => s.Key.VerifyDigest(rgbHash, rgbSignature),
                _ => false
            };
        }
    }
}

/// <summary>ГОСТ Р 34.11-2015 (Streebog-512) exposed as a .NET HashAlgorithm for SignedXml.</summary>
public sealed class Streebog512HashAlgorithm : HashAlgorithm
{
    private readonly Org.BouncyCastle.Crypto.Digests.Gost3411_2012_512Digest _digest = new();

    public Streebog512HashAlgorithm() => HashSizeValue = 512;

    public override void Initialize() => _digest.Reset();

    protected override void HashCore(byte[] array, int ibStart, int cbSize) => _digest.BlockUpdate(array, ibStart, cbSize);

    protected override byte[] HashFinal()
    {
        var result = new byte[_digest.GetDigestSize()];
        _digest.DoFinal(result, 0);
        return result;
    }
}
