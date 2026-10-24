using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NativeNCA.Core.Security;

namespace NativeNCA.Tests;

/// <summary>In-memory CurrentUser\Root: tests must never install anything into the real Windows store.</summary>
internal sealed class FakeRootStore : IRootCertificateStore
{
    public List<X509Certificate2> Roots { get; } = new();
    public bool DeclineNext { get; set; }

    public bool Contains(X509Certificate2 certificate) => Roots.Any(r => r.Thumbprint == certificate.Thumbprint);

    public void Add(X509Certificate2 certificate, IntPtr ownerWindow)
    {
        if (DeclineNext) { DeclineNext = false; throw new OperationCanceledException(); }
        Roots.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
    }

    public void Remove(X509Certificate2 certificate, IntPtr ownerWindow)
    {
        if (DeclineNext) { DeclineNext = false; throw new OperationCanceledException(); }
        Roots.RemoveAll(r => r.Thumbprint == certificate.Thumbprint);
    }

    public IReadOnlyList<X509Certificate2> FindByOrganization(string organization) =>
        Roots.Where(r => r.Subject.Contains($"O={organization}", StringComparison.Ordinal))
            .Select(r => X509CertificateLoader.LoadCertificate(r.RawData)).ToList();
}

public sealed class TlsCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_tls_" + Guid.NewGuid().ToString("N"));
    private readonly FakeRootStore _store = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // No NCALayer directory, so the fallback never kicks in unless a test wants it.
    private TlsCertificateService CreateService() => new(_dir, Path.Combine(_dir, "no-ncalayer"), _store);

    [Fact]
    public void Leaf_HasLoopbackNamesAndServerAuth()
    {
        var generated = LocalCertificateAuthority.Generate(DateTimeOffset.UtcNow);
        var leaf = generated.Leaf;

        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.True(san.Critical);
        Assert.Equal(new[] { "localhost" }, san.EnumerateDnsNames());
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, san.EnumerateIPAddresses());

        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Equal(new[] { "1.3.6.1.5.5.7.3.1" }, eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value));

        var ku = leaf.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.True(ku.Critical);
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, ku.KeyUsages);

        var bc = leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(bc.CertificateAuthority);

        Assert.True(leaf.HasPrivateKey);
        Assert.Equal(generated.Root.SubjectName.RawData, leaf.IssuerName.RawData);
        Assert.True(leaf.NotAfter <= generated.Root.NotAfter);
        Assert.True(leaf.NotAfter > DateTime.Now.AddYears(15));
    }

    [Fact]
    public void Root_IsConstrainedToLoopback()
    {
        var root = LocalCertificateAuthority.Generate(DateTimeOffset.UtcNow).Root;

        var bc = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(bc.Critical);
        Assert.True(bc.CertificateAuthority);
        Assert.True(bc.HasPathLengthConstraint);
        Assert.Equal(0, bc.PathLengthConstraint);

        var nc = root.Extensions["2.5.29.30"];
        Assert.NotNull(nc);
        Assert.True(nc!.Critical);

        // permittedSubtrees [0] { dNSName localhost, iPAddress 127.0.0.1/32, iPAddress ::1/128 }; no excludedSubtrees.
        var reader = new AsnReader(nc.RawData, AsnEncodingRules.DER).ReadSequence();
        var permitted = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        Assert.False(reader.HasData);

        Assert.Equal("localhost", permitted.ReadSequence()
            .ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
        Assert.Equal(new byte[] { 127, 0, 0, 1, 255, 255, 255, 255 }, permitted.ReadSequence()
            .ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7)));
        byte[] ipv6 = permitted.ReadSequence().ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
        Assert.Equal(IPAddress.IPv6Loopback.GetAddressBytes(), ipv6[..16]);
        Assert.All(ipv6[16..], b => Assert.Equal(0xFF, b));
        Assert.False(permitted.HasData);
    }

    [Fact]
    public void RootPrivateKey_IsNotRetained()
    {
        var generated = LocalCertificateAuthority.Generate(DateTimeOffset.UtcNow);
        Assert.False(generated.Root.HasPrivateKey);

        var service = CreateService();
        service.SaveOwn(generated.Root, generated.Leaf);

        // root.cer is just the public certificate.
        string tlsDir = Path.Combine(_dir, "Tls");
        var storedRoot = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(tlsDir, "root.cer"));
        Assert.False(storedRoot.HasPrivateKey);
        Assert.Equal(generated.Root.RawData, storedRoot.RawData);

        // The protected container holds exactly one key, and it belongs to the leaf.
        byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(tlsDir, "localhost.p12.dpapi")),
            "NativeNCA.Tls.Entropy.v1"u8.ToArray(), DataProtectionScope.CurrentUser);
        var collection = X509CertificateLoader.LoadPkcs12Collection(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        var withKeys = collection.Where(c => c.HasPrivateKey).ToList();
        Assert.Single(withKeys);
        Assert.Equal(generated.Leaf.Thumbprint, withKeys[0].Thumbprint);
        Assert.DoesNotContain(collection.Cast<X509Certificate2>(), c => c.Thumbprint == generated.Root.Thumbprint);

        // Nothing else is written next to them.
        Assert.Equal(new[] { "localhost.p12.dpapi", "root.cer" },
            Directory.GetFiles(tlsDir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Chain_BuildsAgainstCustomTrustStore()
    {
        var generated = LocalCertificateAuthority.Generate(DateTimeOffset.UtcNow);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(generated.Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

        bool ok = chain.Build(generated.Leaf);
        Assert.True(ok, string.Join(", ", chain.ChainStatus.Select(s => s.Status)));
        Assert.Equal(2, chain.ChainElements.Count);
        Assert.Equal(generated.Root.Thumbprint, chain.ChainElements[1].Certificate.Thumbprint);
    }

    [Fact]
    public void NameConstraints_RejectNonLoopbackLeaf()
    {
        // Only possible here because the test holds the root key; the real flow discards it.
        using var rootWithKey = LocalCertificateAuthority.CreateRoot(DateTimeOffset.UtcNow);
        using var evil = LocalCertificateAuthority.CreateLeaf(rootWithKey, DateTimeOffset.UtcNow, "egov.kz");
        using var root = X509CertificateLoader.LoadCertificate(rootWithKey.RawData);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.False(chain.Build(evil));
        Assert.Contains(chain.ChainStatus, s => s.Status == X509ChainStatusFlags.HasNotPermittedNameConstraint);
    }

    [Fact]
    public void Storage_RoundTripsThroughDpapi()
    {
        var service = CreateService();
        Assert.Equal(TlsCertificateSource.None, service.GetAvailableSource());

        service.InstallOwnCertificate();
        Assert.Single(_store.Roots);
        Assert.False(_store.Roots[0].HasPrivateKey);
        Assert.Equal(TlsCertificateSource.Own, service.GetAvailableSource());

        // A second service instance (next app start) reads the same files.
        var reloaded = CreateService();
        using var leaf = reloaded.TryLoadOwn(X509KeyStorageFlags.EphemeralKeySet);
        Assert.NotNull(leaf);
        Assert.True(leaf!.HasPrivateKey);
        Assert.Contains("localhost", leaf.Subject);

        // Installing again is a no-op while everything is valid: no second Windows prompt.
        reloaded.InstallOwnCertificate();
        Assert.Single(_store.Roots);

        // The container is useless without DPAPI: it is not a plain PFX.
        byte[] raw = File.ReadAllBytes(Path.Combine(_dir, "Tls", "localhost.p12.dpapi"));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(raw, null));
    }

    [Fact]
    public void Install_DeclinedWindowsPrompt_LeavesNothingBehind()
    {
        var service = CreateService();
        _store.DeclineNext = true;

        Assert.Throws<OperationCanceledException>(() => service.InstallOwnCertificate());
        Assert.Empty(_store.Roots);
        Assert.False(Directory.Exists(Path.Combine(_dir, "Tls")));
        Assert.Equal(TlsCertificateSource.None, service.GetAvailableSource());
    }

    [Fact]
    public void RootRemovedFromStore_OwnCertificateIsNotUsed()
    {
        var service = CreateService();
        service.InstallOwnCertificate();
        _store.Roots.Clear(); // e.g. removed in certmgr.msc

        Assert.Equal(TlsCertificateSource.None, service.GetAvailableSource());
        Assert.Throws<TlsCertificateMissingException>(() => service.GetServerCertificateContext());
    }

    [Fact]
    public void Remove_DeletesRootAndFiles()
    {
        var service = CreateService();
        service.InstallOwnCertificate();

        _store.DeclineNext = true;
        Assert.Throws<OperationCanceledException>(() => service.RemoveOwnCertificate());
        Assert.Equal(TlsCertificateSource.Own, service.GetAvailableSource());

        service.RemoveOwnCertificate();
        Assert.Empty(_store.Roots);
        Assert.False(Directory.Exists(Path.Combine(_dir, "Tls")));
        Assert.True(service.IsSetupDeclined);
        Assert.Equal(TlsCertificateSource.None, service.GetAvailableSource());
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task LoopbackHandshake_ValidatesAgainstGeneratedRoot(string host)
    {
        var service = CreateService();
        service.InstallOwnCertificate();
        var serverContext = service.GetServerCertificateContext();
        Assert.Equal(TlsCertificateSource.Own, service.ActiveSource);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = serverContext,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
                await ssl.WriteAsync("ok"u8.ToArray());
            });

            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            policy.CustomTrustStore.Add(_store.Roots.Single());

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var clientSsl = new SslStream(tcp.GetStream());
            await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                CertificateChainPolicy = policy,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            });

            var buffer = new byte[2];
            await clientSsl.ReadExactlyAsync(buffer);
            Assert.Equal("ok"u8.ToArray(), buffer);
            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task LoopbackHandshake_FailsWithoutTrustedRoot()
    {
        var service = CreateService();
        service.InstallOwnCertificate();
        var serverContext = service.GetServerCertificateContext();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = serverContext }); }
                catch { /* the client aborts */ }
            });

            // Trust some other NativeNCA root: the chain must not build.
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(LocalCertificateAuthority.Generate(DateTimeOffset.UtcNow).Root);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var clientSsl = new SslStream(tcp.GetStream());
            await Assert.ThrowsAsync<AuthenticationException>(() => clientSsl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", CertificateChainPolicy = policy }));
            await serverTask;
        }
        finally
        {
            listener.Stop();
        }
    }
}
