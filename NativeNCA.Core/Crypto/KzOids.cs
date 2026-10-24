namespace NativeNCA.Core.Crypto;

/// <summary>
/// Object identifiers used by НУЦ РК (arc 1.2.398.3.10) and common PKIX OIDs.
/// GOST values were confirmed against a real Kalkan-produced CMS (NCALayer's ncalayer.der).
/// </summary>
public static class KzOids
{
    // СТ РК ГОСТ Р 34.10-2015 (512 bit)
    public const string Gost2015PublicKey = "1.2.398.3.10.1.1.2.2";
    public const string Gost2015ParamSetA = "1.2.398.3.10.1.1.2.2.1";
    public const string Gost2015Signature512 = "1.2.398.3.10.1.1.2.3.2";
    public const string Gost2015Digest512 = "1.2.398.3.10.1.3.3";

    // СТ РК ГОСТ 34.310-2004 (legacy, all such certificates have expired)
    public const string Gost2004PublicKey = "1.2.398.3.10.1.1.1.1";
    public const string Gost2004Signature = "1.2.398.3.10.1.1.1.2";

    // RSA
    public const string RsaEncryption = "1.2.840.113549.1.1.1";
    public const string Sha256WithRsa = "1.2.840.113549.1.1.11";
    public const string Sha256 = "2.16.840.1.101.3.4.2.1";

    // Subject attributes in KZ certificates
    public const string SerialNumber = "2.5.4.5";      // "IIN123456789012"
    public const string OrganizationalUnit = "2.5.4.11"; // "BIN123456789012"

    // Extended key usages
    public const string EkuClientAuth = "1.3.6.1.5.5.7.3.2";
    public const string EkuEmailProtection = "1.3.6.1.5.5.7.3.4";

    // XMLDSig algorithm URIs
    public const string XmlGost2015Signature = "urn:ietf:params:xml:ns:pkigovkz:xmlsec:algorithms:gostr34102015-gostr34112015-512";
    public const string XmlGost2015Digest = "urn:ietf:params:xml:ns:pkigovkz:xmlsec:algorithms:gostr34112015-512";
    public const string XmlRsaSha256Signature = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    public const string XmlSha256Digest = "http://www.w3.org/2001/04/xmlenc#sha256";
}
