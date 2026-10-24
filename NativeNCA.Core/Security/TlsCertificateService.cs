using System.IO.Compression;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace NativeNCA.Core.Security;

/// <summary>
/// Provides the TLS certificate for wss://127.0.0.1:13579.
///
/// Browsers already trust NCALayer's localhost certificate (issued by НУЦ РК RSA 2022, whose root
/// NCALayer installs into the Windows store). NCALayer ships it as keystore.jks inside its websocket
/// bundle; we read that keystore directly with BouncyCastle (no Java/keytool needed) and keep a cached
/// copy so NativeNCA keeps working if NCALayer is removed later.
/// </summary>
public class TlsCertificateService
{
    // Public, identical in every NCALayer installation.
    private const string NcaKeystorePassword = "nb1B3dau";
    private const string NcaKeyAlias = "ncalayer";

    private readonly string _cachePath;
    private readonly string _ncaLayerDir;

    public TlsCertificateService(string? cacheDir = null, string? ncaLayerDir = null)
    {
        string dir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeNCA");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "tls_127_0_0_1.pfx");
        _ncaLayerDir = ncaLayerDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCALayer");
    }

    public SslStreamCertificateContext GetServerCertificateContext()
    {
        // Prefer a fresh copy from NCALayer: it rotates the certificate with its updates.
        byte[]? pfx = ExtractFromNcaLayer();
        if (pfx != null)
        {
            File.WriteAllBytes(_cachePath, pfx);
        }
        else if (File.Exists(_cachePath))
        {
            pfx = File.ReadAllBytes(_cachePath);
        }
        else
        {
            throw new InvalidOperationException(
                "Не найден TLS-сертификат для 127.0.0.1. Установите оригинальный NCALayer хотя бы один раз — " +
                "NativeNCA использует его доверенный сертификат, чтобы браузеры принимали соединение.");
        }

        var collection = X509CertificateLoader.LoadPkcs12Collection(pfx, NcaKeystorePassword, X509KeyStorageFlags.UserKeySet);
        var leaf = collection.First(c => c.HasPrivateKey);

        if (leaf.NotAfter < DateTime.Now)
            throw new InvalidOperationException(
                $"TLS-сертификат NCALayer истёк {leaf.NotAfter:dd.MM.yyyy}. Обновите NCALayer, чтобы получить новый.");

        var intermediates = new X509Certificate2Collection(collection.Where(c => !c.HasPrivateKey).ToArray());
        return SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
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
