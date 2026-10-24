using System.IO.Compression;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace NativeNCA.Core.Security;

/// <summary>Which certificate the wss server presents to browsers.</summary>
public enum TlsCertificateSource
{
    /// <summary>No usable certificate: browsers cannot connect.</summary>
    None,

    /// <summary>NativeNCA's own per-machine certificate (see <see cref="LocalCertificateAuthority"/>).</summary>
    Own,

    /// <summary>NCALayer's shared 127.0.0.1 certificate, used until the own one is set up.</summary>
    NcaLayer
}

/// <summary>Neither the own certificate nor NCALayer's is available.</summary>
public sealed class TlsCertificateMissingException(string message) : InvalidOperationException(message);

/// <summary>
/// Provides the TLS certificate for wss://127.0.0.1:13579, in this order:
///
/// 1. NativeNCA's own certificate, if its root is in CurrentUser\Root and nothing has expired. The leaf
///    and its key are stored DPAPI-protected in %APPDATA%\NativeNCA\Tls; the root's key no longer exists.
/// 2. NCALayer's shared localhost certificate (issued by НУЦ РК RSA 2022, whose root NCALayer installs).
///    NCALayer ships it as keystore.jks inside its websocket bundle; we read that keystore with
///    BouncyCastle and keep a cached copy. Its private key is the same in every NCALayer installation,
///    which is why the own certificate is preferred.
/// 3. Otherwise <see cref="TlsCertificateMissingException"/>.
/// </summary>
public class TlsCertificateService
{
    // Public, identical in every NCALayer installation.
    private const string NcaKeystorePassword = "nb1B3dau";
    private const string NcaKeyAlias = "ncalayer";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NativeNCA.Tls.Entropy.v1");

    private readonly string _cachePath;
    private readonly string _ncaLayerDir;
    private readonly string _ownDir;
    private readonly string _rootPath;
    private readonly string _leafPath;
    private readonly string _declinedPath;
    private readonly IRootCertificateStore _rootStore;

    public TlsCertificateService(string? dataDir = null, string? ncaLayerDir = null, IRootCertificateStore? rootStore = null)
    {
        string dir = dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeNCA");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "tls_127_0_0_1.pfx");
        _ownDir = Path.Combine(dir, "Tls");
        _rootPath = Path.Combine(_ownDir, "root.cer");
        _leafPath = Path.Combine(_ownDir, "localhost.p12.dpapi");
        _declinedPath = Path.Combine(dir, "tls_setup_declined");
        _ncaLayerDir = ncaLayerDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCALayer");
        _rootStore = rootStore ?? new CurrentUserRootStore();
    }

    /// <summary>Certificate chosen by the last <see cref="GetServerCertificateContext"/> call.</summary>
    public TlsCertificateSource ActiveSource { get; private set; }

    /// <summary>The user chose not to set up the own certificate: do not offer it unprompted again.</summary>
    public bool IsSetupDeclined
    {
        get => File.Exists(_declinedPath);
        set
        {
            if (value) File.WriteAllBytes(_declinedPath, []);
            else File.Delete(_declinedPath);
        }
    }

    /// <summary>What <see cref="GetServerCertificateContext"/> would pick right now.</summary>
    public TlsCertificateSource GetAvailableSource()
    {
        using (var own = TryLoadOwn(X509KeyStorageFlags.EphemeralKeySet))
            if (own != null) return TlsCertificateSource.Own;

        var nca = TryLoadNcaLayer(cache: false);
        if (nca == null) return TlsCertificateSource.None;
        foreach (var c in nca) c.Dispose();
        return TlsCertificateSource.NcaLayer;
    }

    public SslStreamCertificateContext GetServerCertificateContext()
    {
        // SChannel cannot use ephemeral keys, so the server copy goes to the user key store
        // for the lifetime of the certificate object.
        var own = TryLoadOwn(X509KeyStorageFlags.UserKeySet);
        if (own != null)
        {
            ActiveSource = TlsCertificateSource.Own;
            return SslStreamCertificateContext.Create(own, new X509Certificate2Collection(), offline: true);
        }

        var collection = TryLoadNcaLayer(cache: true);
        if (collection != null)
        {
            ActiveSource = TlsCertificateSource.NcaLayer;
            var leaf = collection.First(c => c.HasPrivateKey);
            var intermediates = new X509Certificate2Collection(collection.Where(c => !c.HasPrivateKey).ToArray());
            return SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        }

        ActiveSource = TlsCertificateSource.None;
        throw new TlsCertificateMissingException(
            "Нет сертификата, которому доверяют браузеры. Нажмите «Настроить сертификат» — NativeNCA создаст " +
            "собственный сертификат для этого компьютера.");
    }

    // ===================== Own certificate =====================

    /// <summary>
    /// Generates the per-machine root and leaf and adds the root to CurrentUser\Root.
    /// Windows asks the user to confirm; declining throws <see cref="OperationCanceledException"/>
    /// and leaves nothing behind. Blocks until the prompt is answered: call off the UI thread.
    /// </summary>
    public void InstallOwnCertificate(IntPtr ownerWindow = default)
    {
        using (var existing = TryLoadOwn(X509KeyStorageFlags.EphemeralKeySet))
            if (existing != null) return;

        var generated = LocalCertificateAuthority.Generate(DateTimeOffset.UtcNow);
        using var root = generated.Root;
        using var leaf = generated.Leaf;

        _rootStore.Add(root, ownerWindow);
        try
        {
            SaveOwn(root, leaf);
        }
        catch
        {
            try { _rootStore.Remove(root, ownerWindow); } catch { /* the user may decline this prompt too */ }
            throw;
        }

        IsSetupDeclined = false;
    }

    /// <summary>
    /// Removes every NativeNCA root from CurrentUser\Root (Windows asks to confirm each) and deletes the files.
    /// Declining keeps everything as it was and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public void RemoveOwnCertificate(IntPtr ownerWindow = default)
    {
        var roots = _rootStore.FindByOrganization(LocalCertificateAuthority.RootOrganization)
            .Where(c => c.Subject.Contains("NativeNCA Localhost Root", StringComparison.Ordinal))
            .ToList();
        if (File.Exists(_rootPath))
            roots.Add(X509CertificateLoader.LoadCertificateFromFile(_rootPath));

        foreach (var root in roots.DistinctBy(c => c.Thumbprint))
        {
            if (_rootStore.Contains(root)) _rootStore.Remove(root, ownerWindow);
        }
        foreach (var root in roots) root.Dispose();

        if (Directory.Exists(_ownDir)) Directory.Delete(_ownDir, recursive: true);
        IsSetupDeclined = true;
    }

    internal void SaveOwn(X509Certificate2 root, X509Certificate2 leaf)
    {
        Directory.CreateDirectory(_ownDir);
        byte[] pfx = leaf.Export(X509ContentType.Pkcs12);
        try
        {
            WriteAtomically(_leafPath, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
        WriteAtomically(_rootPath, root.RawData);
    }

    /// <summary>The stored leaf with its key, or null if anything is missing, expired or untrusted.</summary>
    internal X509Certificate2? TryLoadOwn(X509KeyStorageFlags flags)
    {
        if (!File.Exists(_rootPath) || !File.Exists(_leafPath)) return null;

        try
        {
            using var root = X509CertificateLoader.LoadCertificateFromFile(_rootPath);
            if (!IsCurrent(root) || !_rootStore.Contains(root)) return null;

            byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(_leafPath), Entropy, DataProtectionScope.CurrentUser);
            X509Certificate2 leaf;
            try
            {
                leaf = X509CertificateLoader.LoadPkcs12(pfx, null, flags);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }

            if (leaf.HasPrivateKey && IsCurrent(leaf) && ChainsTo(leaf, root)) return leaf;
            leaf.Dispose();
            return null;
        }
        catch
        {
            // Corrupt files or a different Windows user: treat as not set up.
            return null;
        }
    }

    /// <summary>The leaf chains to exactly this root, with nameConstraints and usages checked by Windows.</summary>
    internal static bool ChainsTo(X509Certificate2 leaf, X509Certificate2 root)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }

    private static bool IsCurrent(X509Certificate2 cert) => cert.NotBefore <= DateTime.Now && DateTime.Now < cert.NotAfter;

    private static void WriteAtomically(string path, byte[] data)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    // ===================== NCALayer fallback =====================

    private X509Certificate2Collection? TryLoadNcaLayer(bool cache)
    {
        // Prefer a fresh copy from NCALayer: it rotates the certificate with its updates.
        byte[]? pfx = ExtractFromNcaLayer();
        if (pfx != null)
        {
            if (cache) File.WriteAllBytes(_cachePath, pfx);
        }
        else if (File.Exists(_cachePath))
        {
            pfx = File.ReadAllBytes(_cachePath);
        }
        else
        {
            return null;
        }

        try
        {
            var collection = X509CertificateLoader.LoadPkcs12Collection(pfx, NcaKeystorePassword,
                cache ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
            var leaf = collection.FirstOrDefault(c => c.HasPrivateKey);
            if (leaf != null && IsCurrent(leaf)) return collection;

            foreach (var c in collection) c.Dispose();
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private byte[]? ExtractFromNcaLayer()
    {
        if (!Directory.Exists(_ncaLayerDir)) return null;

        var jars = Directory.EnumerateFiles(_ncaLayerDir, "*.jar", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc);

        foreach (var jar in jars)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jar.FullName);
                var entry = zip.GetEntry("keystore.jks");
                if (entry == null) continue;

                using var jksStream = entry.Open();
                using var buffered = new MemoryStream();
                jksStream.CopyTo(buffered);
                buffered.Position = 0;

                return ConvertJksToPkcs12(buffered);
            }
            catch
            {
                // Unreadable bundle: try the next one.
            }
        }

        return null;
    }

    private static byte[] ConvertJksToPkcs12(Stream jks)
    {
        char[] password = NcaKeystorePassword.ToCharArray();
        var store = new JksStore();
        store.Load(jks, password);

        string alias = store.IsKeyEntry(NcaKeyAlias) ? NcaKeyAlias : store.Aliases.First(store.IsKeyEntry);
        var key = store.GetKey(alias, password);
        var chain = store.GetCertificateChain(alias);

        var pkcs12 = new Pkcs12StoreBuilder().Build();
        pkcs12.SetKeyEntry(NcaKeyAlias, new AsymmetricKeyEntry(key), chain.Select(c => new X509CertificateEntry(c)).ToArray());

        using var output = new MemoryStream();
        pkcs12.Save(output, password, new SecureRandom());
        return output.ToArray();
    }
}
