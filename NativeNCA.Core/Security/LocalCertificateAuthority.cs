using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NativeNCA.Core.Security;

/// <summary>Result of <see cref="LocalCertificateAuthority.Generate"/>: the root has no private key.</summary>
public sealed record LocalTlsCertificate(X509Certificate2 Root, X509Certificate2 Leaf);

/// <summary>
/// Creates the per-machine TLS certificate for wss://127.0.0.1:13579.
///
/// A fresh root CA signs one leaf for localhost / 127.0.0.1 / ::1 and its private key is thrown away
/// right after that, so nothing can ever be issued under this root again. The root also carries a
/// critical nameConstraints extension: even a stolen copy of the root could only vouch for loopback
/// addresses. Lifetimes are long because locally installed roots are exempt from the public limits,
/// and renewing means a new Windows prompt.
/// </summary>
public static class LocalCertificateAuthority
{
    public const string RootOrganization = "NativeNCA";
    public static readonly TimeSpan RootLifetime = TimeSpan.FromDays(365 * 25);
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(365 * 20);

    private const string NameConstraintsOid = "2.5.29.30";

    public static LocalTlsCertificate Generate(DateTimeOffset now)
    {
        using var rootWithKey = CreateRoot(now);
        var leaf = CreateLeaf(rootWithKey, now);

        // Keep only the public part of the root; disposing the original destroys the ephemeral key.
        var root = X509CertificateLoader.LoadCertificate(rootWithKey.RawData);
        return new LocalTlsCertificate(root, leaf);
    }

    /// <summary>Root CA with its (ephemeral) private key. Callers must dispose it.</summary>
    internal static X509Certificate2 CreateRoot(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName($"CN=NativeNCA Localhost Root ({Environment.MachineName}), O={RootOrganization}");
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, EncodeLoopbackNameConstraints(), true));

        return request.CreateSelfSigned(now.AddDays(-1), now + RootLifetime);
    }

    /// <summary>Server certificate for the loopback names, with its private key.</summary>
    internal static X509Certificate2 CreateLeaf(X509Certificate2 root, DateTimeOffset now, string dnsName = "localhost")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={dnsName}"), key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        request.CertificateExtensions.Add(san.Build(true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

        var notAfter = now + LeafLifetime;
        if (notAfter > root.NotAfter) notAfter = root.NotAfter;

        using var cert = request.Create(root, now.AddDays(-1), notAfter, RandomSerial());
        return cert.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// NameConstraints ::= SEQUENCE { permittedSubtrees [0] GeneralSubtrees }, permitting only
    /// dNSName localhost, iPAddress 127.0.0.1/32 and ::1/128.
    /// </summary>
    internal static byte[] EncodeLoopbackNameConstraints()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            using (writer.PushSequence())
                writer.WriteCharacterString(UniversalTagNumber.IA5String, "localhost", new Asn1Tag(TagClass.ContextSpecific, 2));

            using (writer.PushSequence())
                writer.WriteOctetString([127, 0, 0, 1, 255, 255, 255, 255], new Asn1Tag(TagClass.ContextSpecific, 7));

            byte[] ipv6 = new byte[32];
            ipv6[15] = 1;
            Array.Fill(ipv6, (byte)0xFF, 16, 16);
            using (writer.PushSequence())
                writer.WriteOctetString(ipv6, new Asn1Tag(TagClass.ContextSpecific, 7));
        }
        return writer.Encode();
    }

    private static byte[] RandomSerial()
    {
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive INTEGER
        serial[0] |= 0x01; // no leading zero byte
        return serial;
    }
}
