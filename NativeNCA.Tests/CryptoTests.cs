using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using NativeNCA.Core.Crypto;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;

namespace NativeNCA.Tests;

public class GostOracleTests
{
    // CMS signed by НУЦ РК with Kalkan (NCALayer update manifest). This is the ground truth
    // that pins down curve, hash and byte-order conventions of СТ РК ГОСТ Р 34.10-2015.
    private static byte[] Manifest => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "ncalayer_manifest.p7s"));

    [Fact]
    public void Real_NCA_Gost2015_signature_verifies()
    {
        var result = CmsVerifier.Verify(Manifest);
        Assert.True(result.IsValid, result.Error);
        Assert.Contains("NCALayer", result.Signer!.SubjectDN.ToString());
    }

    [Fact]
    public void Tampered_NCA_signature_is_rejected()
    {
        var cms = Manifest;
        // Flip one byte of the embedded content (JSON text near the start)
        int idx = Encoding.ASCII.GetString(cms).IndexOf("ncalayer", StringComparison.Ordinal);
        cms[idx] ^= 0x20;
        Assert.False(CmsVerifier.Verify(cms).IsValid);
    }

    [Fact]
    public void Certificate_info_reads_NCA_certificate()
    {
        var sd = SignedData.GetInstance(Org.BouncyCastle.Asn1.Cms.ContentInfo.GetInstance(Asn1Object.FromByteArray(Manifest)).Content);
        var cert = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(sd.Certificates[0].GetEncoded());
        var info = CertificateInfo.From(cert);

        Assert.Equal("ГОСТ", info.Algorithm);
        Assert.Equal("000740000728", info.Bin);
        Assert.Equal("NCALayer", info.CommonName);
    }
}

public class GostKeyTests
{
    [Fact]
    public void Loads_nca_style_gost_pfx_and_parses_subject()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        Assert.Equal(KeyAlgorithmKind.Gost2015, key.Kind);

        var info = CertificateInfo.From(key.Certificate);
        Assert.Equal("900101300123", info.Iin);
        Assert.Equal("180340012345", info.Bin);
        Assert.Equal("ТЕСТОВ ТЕСТ ТЕСТОВИЧ", info.FullName);
        Assert.Equal("BOTH", info.Usage);
    }

    [Fact]
    public void Wrong_password_is_reported()
    {
        Assert.Throws<WrongPasswordException>(() => SigningKey.Load(TestKeys.CreateGostPfx(), "wrong"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cms_roundtrip(bool attached)
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        byte[] data = Encoding.UTF8.GetBytes("Документ для подписи");

        byte[] cms = CmsSigner.Sign(key, data, attached);

        var result = CmsVerifier.Verify(cms, attached ? null : data);
        Assert.True(result.IsValid, result.Error);
        Assert.False(CmsVerifier.Verify(cms, Encoding.UTF8.GetBytes("другой документ")).IsValid && !attached);
    }

    [Fact]
    public void Cms_over_precomputed_digest()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        byte[] data = Encoding.UTF8.GetBytes("data");

        byte[] cms = CmsSigner.SignDigest(key, Gost2015.Hash(data));

        Assert.True(CmsVerifier.Verify(cms, data).IsValid);
    }

    [Fact]
    public void XmlDsig_roundtrip_and_tamper_detection()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        const string xml = "<root xmlns:a=\"urn:a\"><a:item id=\"1\">Сумма 100</a:item><!-- comment --></root>";

        string signed = XmlDsigSigner.Sign(key, xml);

        Assert.Contains(KzOids.XmlGost2015Signature, signed);
        Assert.True(XmlDsigSigner.Verify(signed, out _));
        Assert.False(XmlDsigSigner.Verify(signed.Replace("Сумма 100", "Сумма 900"), out _));
    }

    [Fact]
    public void XmlDsig_into_nested_parent()
    {
        var key = SigningKey.Load(TestKeys.CreateGostPfx(), TestKeys.Password);
        const string xml = "<root xmlns=\"urn:default\"><body xmlns:x=\"urn:x\"><x:data>1</x:data></body><sig/></root>";

        string signed = XmlDsigSigner.Sign(key, xml, signatureParentXPath: "/*[local-name()='root']/*[local-name()='sig']");

        Assert.True(XmlDsigSigner.Verify(signed, out _));
    }
}

public class RsaKeyTests
{
    [Fact]
    public void Cms_roundtrip()
    {
        var key = SigningKey.Load(TestKeys.CreateRsaPfx(), TestKeys.Password);
        Assert.Equal(KeyAlgorithmKind.Rsa, key.Kind);

        byte[] data = Encoding.UTF8.GetBytes("hello");
        Assert.True(CmsVerifier.Verify(CmsSigner.Sign(key, data, attached: true)).IsValid);

        // Independent check with .NET's own CMS implementation
        var cms = new System.Security.Cryptography.Pkcs.SignedCms();
        cms.Decode(CmsSigner.Sign(key, data, attached: true));
        cms.CheckSignature(verifySignatureOnly: true);
    }

    [Fact]
    public void XmlDsig_verifies_with_dotnet_SignedXml()
    {
        var key = SigningKey.Load(TestKeys.CreateRsaPfx(), TestKeys.Password);
        string signed = XmlDsigSigner.Sign(key, "<doc><v>42</v></doc>");

        // Independent verifier: stock .NET SignedXml with the embedded certificate
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(signed);
        var sx = new SignedXml(doc);
        sx.LoadXml((XmlElement)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        var cert = X509CertificateLoader.LoadCertificate(key.Certificate.GetEncoded());
        Assert.True(sx.CheckSignature(cert, verifySignatureOnly: true));
    }
}
