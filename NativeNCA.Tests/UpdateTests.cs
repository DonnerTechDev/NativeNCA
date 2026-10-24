using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Updates;
using NativeNCA.Core.ViewModels;

namespace NativeNCA.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0")]
    [InlineData("0.2.0", "0.10.0")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.1-alpha")]
    [InlineData("0.0.0-dev", "0.1.0")]
    public void Orders_AsSemVer(string older, string newer)
    {
        var a = AppVersion.Parse(older);
        var b = AppVersion.Parse(newer);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.False(a.Equals(b));
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2.0+ce77a22f", "0.2.0")]
    [InlineData("V1.2", "1.2.0")]
    [InlineData("1.0.0-beta.1+abc", "1.0.0-beta.1")]
    public void Parses_TagsAndInformationalVersions(string text, string expected)
    {
        var v = AppVersion.Parse(text);
        Assert.Equal(expected, v.ToString());
        Assert.True(v.Equals(AppVersion.Parse(expected)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("-1.0.0")]
    public void Rejects_Garbage(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void BuildMetadata_DoesNotMakeAVersionNewer() =>
        Assert.Equal(0, AppVersion.Parse("0.2.0+aaa").CompareTo(AppVersion.Parse("0.2.0+bbb")));
}

public class ReleaseSelectionTests
{
    internal static string ReleaseJson(string tag, bool prerelease = false, bool draft = false, bool withSignatures = true,
        string host = "github.com")
    {
        string v = tag.TrimStart('v');
        var assets = new List<object>();
        foreach (string arch in new[] { "win-x64", "win-arm64" })
        {
            string zip = $"NativeNCA-{v}-{arch}.zip";
            assets.Add(new { name = zip, size = 1000, browser_download_url = $"https://{host}/DonnerTechDev/NativeNCA/releases/download/{tag}/{zip}" });
            assets.Add(new { name = zip + ".sha256", size = 90, browser_download_url = $"https://{host}/DonnerTechDev/NativeNCA/releases/download/{tag}/{zip}.sha256" });
            if (withSignatures)
                assets.Add(new { name = zip + ".sig", size = 90, browser_download_url = $"https://{host}/DonnerTechDev/NativeNCA/releases/download/{tag}/{zip}.sig" });
        }
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = $"https://github.com/DonnerTechDev/NativeNCA/releases/tag/{tag}",
            draft,
            prerelease,
            assets
        });
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void Selects_ZipAndSignature_ForArchitecture(string arch)
    {
        var release = GitHubReleaseClient.ParseRelease(ReleaseJson("v0.2.0"))!;
        var package = GitHubReleaseClient.SelectPackage(release, arch)!;

        Assert.Equal($"NativeNCA-0.2.0-{arch}.zip", package.Zip.Name);
        Assert.Equal($"NativeNCA-0.2.0-{arch}.zip.sig", package.Signature.Name);
        Assert.Equal(arch, package.Architecture);
        Assert.Equal("0.2.0", release.Version.ToString());
    }

    [Fact]
    public void NoPackage_WithoutSignature() =>
        Assert.Null(GitHubReleaseClient.SelectPackage(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.2.0", withSignatures: false))!, "win-x64"));

    [Fact]
    public void NoPackage_ForUnknownArchitecture()
    {
        var release = GitHubReleaseClient.ParseRelease(ReleaseJson("v0.2.0"))!;
        Assert.Null(GitHubReleaseClient.SelectPackage(release, null));
        Assert.Null(GitHubReleaseClient.SelectPackage(release, "win-x86"));
        Assert.Null(GitHubReleaseClient.ArchitectureName(System.Runtime.InteropServices.Architecture.X86));
    }

    [Fact]
    public void NoPackage_FromUntrustedHost() =>
        Assert.Null(GitHubReleaseClient.SelectPackage(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.2.0", host: "evil.example"))!, "win-x64"));

    [Fact]
    public void Ignores_PreReleasesDraftsAndOddTags()
    {
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.0-beta.1")));
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.0", prerelease: true)));
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson("v0.3.0", draft: true)));
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson("nightly")));
    }

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    public void TrustedHosts_AreHttpsGitHubOnly(string url, bool trusted) =>
        Assert.Equal(trusted, GitHubReleaseClient.IsTrustedDownloadUri(new Uri(url)));
}

public class ReleaseSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_sig_" + Guid.NewGuid().ToString("N"));
    private readonly string _privateKey;
    private readonly byte[] _publicKey;
    private static readonly AppVersion V020 = AppVersion.Parse("0.2.0");
    private static readonly byte[] ZipHash = System.Security.Cryptography.SHA256.HashData("zip"u8);

    public ReleaseSignatureTests()
    {
        Directory.CreateDirectory(_dir);
        var (priv, pub) = ReleaseSignature.GenerateKeyPair();
        _privateKey = priv;
        _publicKey = Convert.FromBase64String(pub);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Valid_Signature_Verifies()
    {
        string sig = ReleaseSignature.Sign(_privateKey, V020, "win-x64", ZipHash);
        Assert.True(ReleaseSignature.Verify(sig, V020, "win-x64", ZipHash, _publicKey));
        Assert.True(ReleaseSignature.Verify(sig + "\n", V020, "win-x64", ZipHash, _publicKey));
    }

    [Fact]
    public void TamperedZip_IsRejected()
    {
        string sig = ReleaseSignature.Sign(_privateKey, V020, "win-x64", ZipHash);
        var other = (byte[])ZipHash.Clone();
        other[0] ^= 1;
        Assert.False(ReleaseSignature.Verify(sig, V020, "win-x64", other, _publicKey));
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        // A genuine signature of an older release must not install as a "newer" one (downgrade).
        string sig = ReleaseSignature.Sign(_privateKey, AppVersion.Parse("0.1.0"), "win-x64", ZipHash);
        Assert.False(ReleaseSignature.Verify(sig, V020, "win-x64", ZipHash, _publicKey));
    }

    [Fact]
    public void WrongArchitecture_IsRejected()
    {
        string sig = ReleaseSignature.Sign(_privateKey, V020, "win-arm64", ZipHash);
        Assert.False(ReleaseSignature.Verify(sig, V020, "win-x64", ZipHash, _publicKey));
    }

    [Fact]
    public void OtherKeyOrGarbage_IsRejected()
    {
        var (otherKey, _) = ReleaseSignature.GenerateKeyPair();
        Assert.False(ReleaseSignature.Verify(ReleaseSignature.Sign(otherKey, V020, "win-x64", ZipHash), V020, "win-x64", ZipHash, _publicKey));
        Assert.False(ReleaseSignature.Verify("not base64!", V020, "win-x64", ZipHash, _publicKey));
        Assert.False(ReleaseSignature.Verify(Convert.ToBase64String(new byte[64]), V020, "win-x64", ZipHash, _publicKey));
        Assert.False(ReleaseSignature.Verify("", V020, "win-x64", ZipHash, _publicKey));
    }

    [Fact]
    public void EmbeddedPublicKey_IsP256()
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ReleaseSignature.PublicKeyBase64), out _);
        Assert.Equal(256, ecdsa.KeySize);
        // Nothing signed with a throwaway key passes the real check.
        Assert.False(ReleaseSignature.Verify(ReleaseSignature.Sign(_privateKey, V020, "win-x64", ZipHash), V020, "win-x64", ZipHash));
    }

    /// <summary>A zip like CI's, holding a file with a real version resource as NativeNCA.exe.</summary>
    private string MakeZip(out AppVersion exeVersion)
    {
        string exeSource = typeof(AppVersion).Assembly.Location; // NativeNCA.Core.dll has a version resource
        exeVersion = AppInstallation.ReadExeVersion(exeSource)!;
        string zip = Path.Combine(_dir, "NativeNCA-test-win-x64.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(exeSource, AppInstallation.ExeName);
        return zip;
    }

    [Fact]
    public void Extracts_SignedZip()
    {
        string zip = MakeZip(out var version);
        byte[] hash = UpdateDownloader.HashFile(zip);
        string sig = ReleaseSignature.Sign(_privateKey, version, "win-x64", hash);
        string target = Path.Combine(_dir, AppInstallation.NewExeName);

        UpdateDownloader.VerifyAndExtract(zip, hash, sig, version, "win-x64", target, _publicKey);

        Assert.Equal(File.ReadAllBytes(typeof(AppVersion).Assembly.Location), File.ReadAllBytes(target));
    }

    [Fact]
    public void DoesNotExtract_WithBadSignature()
    {
        string zip = MakeZip(out var version);
        byte[] hash = UpdateDownloader.HashFile(zip);
        string sig = ReleaseSignature.Sign(_privateKey, version, "win-arm64", hash);
        string target = Path.Combine(_dir, AppInstallation.NewExeName);

        Assert.Throws<UpdateRejectedException>(() =>
            UpdateDownloader.VerifyAndExtract(zip, hash, sig, version, "win-x64", target, _publicKey));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void DoesNotKeep_ExeWhoseVersionDiffers()
    {
        // Correctly signed for 99.0.0, but the exe inside says otherwise.
        string zip = MakeZip(out _);
        byte[] hash = UpdateDownloader.HashFile(zip);
        var claimed = AppVersion.Parse("99.0.0");
        string sig = ReleaseSignature.Sign(_privateKey, claimed, "win-x64", hash);
        string target = Path.Combine(_dir, AppInstallation.NewExeName);

        Assert.Throws<UpdateRejectedException>(() =>
            UpdateDownloader.VerifyAndExtract(zip, hash, sig, claimed, "win-x64", target, _publicKey));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".download"));
    }

    [Fact]
    public async Task Downloader_RefusesNotNewerVersion()
    {
        var release = GitHubReleaseClient.ParseRelease(ReleaseSelectionTests.ReleaseJson("v0.2.0"))!;
        var package = GitHubReleaseClient.SelectPackage(release, "win-x64")!;
        using var http = new HttpClient(new FakeGitHub(_ => throw new InvalidOperationException("no network in tests")));
        var downloader = new UpdateDownloader(http, _dir, _publicKey);

        await Assert.ThrowsAsync<UpdateRejectedException>(() =>
            downloader.DownloadAndStageAsync(package, AppVersion.Parse("0.2.0"), Path.Combine(_dir, "x.exe"), null, CancellationToken.None));
    }
}

public class InstallationSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_install_" + Guid.NewGuid().ToString("N"));
    private readonly AppInstallation _install;

    public InstallationSwapTests()
    {
        Directory.CreateDirectory(_dir);
        _install = new AppInstallation(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Read(string path) => File.ReadAllText(path);

    [Fact]
    public void Update_KeepsExactlyOnePreviousVersion()
    {
        File.WriteAllText(_install.ExePath, "v1");

        File.WriteAllText(_install.NewExePath, "v2");
        _install.SwapInNewVersion(AppVersion.Parse("0.1.0"));
        Assert.Equal("v2", Read(_install.ExePath));
        Assert.Equal("v1", Read(_install.PreviousExePath));
        Assert.False(File.Exists(_install.NewExePath));
        Assert.Equal("0.1.0", _install.GetPreviousVersion()!.ToString());

        File.WriteAllText(_install.NewExePath, "v3");
        _install.SwapInNewVersion(AppVersion.Parse("0.2.0"));
        Assert.Equal("v3", Read(_install.ExePath));
        Assert.Equal("v2", Read(_install.PreviousExePath));
        Assert.Equal("0.2.0", _install.GetPreviousVersion()!.ToString());
        Assert.Equal(new[] { AppInstallation.ExeName, AppInstallation.PreviousExeName, AppInstallation.PreviousInfoName }.Order(),
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void FailedSwap_PutsTheCurrentExeBack()
    {
        File.WriteAllText(_install.ExePath, "v1");
        File.WriteAllText(_install.NewExePath, "v2");

        // An open handle without delete sharing makes the second rename fail.
        using (new FileStream(_install.NewExePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => _install.SwapInNewVersion(AppVersion.Parse("0.1.0")));

        Assert.Equal("v1", Read(_install.ExePath));
        Assert.False(_install.HasPreviousVersion);
    }

    [Fact]
    public void UndoSwap_RestoresTheOldExe()
    {
        File.WriteAllText(_install.ExePath, "v1");
        File.WriteAllText(_install.NewExePath, "v2");
        _install.SwapInNewVersion(AppVersion.Parse("0.1.0"));

        _install.UndoSwap();
        _install.CleanUp();

        Assert.Equal("v1", Read(_install.ExePath));
        Assert.False(_install.HasPreviousVersion);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Rollback_SwapsBackAndCleansUpOnNextStart()
    {
        File.WriteAllText(_install.ExePath, "v1");
        File.WriteAllText(_install.NewExePath, "v2");
        _install.SwapInNewVersion(AppVersion.Parse("0.1.0"));

        _install.SwapToPreviousVersion();
        Assert.Equal("v1", Read(_install.ExePath));
        Assert.Equal("v2", Read(_install.DiscardedExePath)); // was running: deleted later
        Assert.False(_install.HasPreviousVersion);
        Assert.Null(_install.GetPreviousVersion());

        _install.CleanUp();
        Assert.Equal(new[] { AppInstallation.ExeName }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void UndoRollback_RestoresBothVersions()
    {
        File.WriteAllText(_install.ExePath, "v1");
        File.WriteAllText(_install.NewExePath, "v2");
        _install.SwapInNewVersion(AppVersion.Parse("0.1.0"));
        _install.SwapToPreviousVersion();

        _install.UndoRollback(AppVersion.Parse("0.1.0"));

        Assert.Equal("v2", Read(_install.ExePath));
        Assert.Equal("v1", Read(_install.PreviousExePath));
        Assert.Equal("0.1.0", _install.GetPreviousVersion()!.ToString());
    }

    [Fact]
    public void Rollback_WithoutPreviousVersion_ChangesNothing()
    {
        File.WriteAllText(_install.ExePath, "v1");
        Assert.Throws<FileNotFoundException>(_install.SwapToPreviousVersion);
        Assert.Equal("v1", Read(_install.ExePath));
    }

    [Fact]
    public void PreviousVersion_FallsBackToTheExeVersion()
    {
        File.WriteAllText(_install.ExePath, "v2");
        File.Copy(typeof(AppVersion).Assembly.Location, _install.PreviousExePath);
        Assert.Equal(AppInstallation.ReadExeVersion(typeof(AppVersion).Assembly.Location), _install.GetPreviousVersion());
    }

    [Fact]
    public void RecognisesTheInstalledCopy()
    {
        Assert.True(_install.IsInstalledCopy(Path.Combine(_dir, "NATIVENCA.EXE")));
        Assert.True(_install.IsInstalledCopy(Path.Combine(_dir, ".", "NativeNCA.exe")));
        Assert.False(_install.IsInstalledCopy(Path.Combine(_dir, AppInstallation.PreviousExeName)));
        Assert.False(_install.IsInstalledCopy(@"C:\Users\someone\Downloads\NativeNCA.exe"));
        Assert.False(_install.IsInstalledCopy(null));
    }
}

public class VaultFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_format_" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "vault.json");

    public VaultFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void MissingFormat_IsVersionOne_AndIsWritten()
    {
        File.WriteAllText(DbPath, """{"Keys":[],"SessionCacheMinutes":15}""");
        var vault = new SecureVaultService(_dir, new FakeHelloProtector());

        Assert.False(vault.IsReadOnly);
        Assert.Equal(1, vault.FormatVersion);
        vault.SaveSettings(vault.GetSettings() with { SessionCacheMinutes = 5 });

        using var doc = JsonDocument.Parse(File.ReadAllText(DbPath));
        Assert.Equal(1, doc.RootElement.GetProperty("FormatVersion").GetInt32());
    }

    [Fact]
    public void NewVault_IsCurrentFormat()
    {
        var vault = new SecureVaultService(_dir, new FakeHelloProtector());
        Assert.Equal(SecureVaultService.SupportedFormatVersion, vault.FormatVersion);
        Assert.False(vault.IsReadOnly);
    }

    [Fact]
    public void NewerFormat_IsReadOnly_AndNeverWritten()
    {
        var db = new VaultDatabase
        {
            FormatVersion = SecureVaultService.SupportedFormatVersion + 1,
            Keys = { new VaultKeyItem { Name = "Ключ", EncryptedFileName = "x.enc", CertificateSerial = "01" } }
        };
        string json = JsonSerializer.Serialize(db) + "\n";
        File.WriteAllText(DbPath, json);
        var vault = new SecureVaultService(_dir, new FakeHelloProtector());

        Assert.True(vault.IsReadOnly);
        Assert.Single(vault.GetKeys()); // still readable
        var key = vault.GetKeys()[0];

        Assert.Throws<VaultFormatTooNewException>(() => vault.SaveSettings(vault.GetSettings() with { UseTimestamp = false }));
        Assert.Throws<VaultFormatTooNewException>(() => vault.UpdateKey(key.Id, "Другое имя", false));
        Assert.Throws<VaultFormatTooNewException>(() => vault.RemoveKey(key.Id));
        Assert.Throws<VaultFormatTooNewException>(() => vault.RemoveAvatar(key.Id));
        vault.MarkKeyUsed(key.Id); // signing still works, it just does not record the time

        Assert.Equal(json, File.ReadAllText(DbPath));
        Assert.Equal(new[] { "vault.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void NewerFormat_ThatDoesNotParse_IsLeftAlone()
    {
        string json = """{"FormatVersion":7,"Keys":"stored differently now"}""";
        File.WriteAllText(DbPath, json);
        var vault = new SecureVaultService(_dir, new FakeHelloProtector());

        Assert.True(vault.IsReadOnly);
        Assert.Empty(vault.GetKeys());
        Assert.Throws<VaultFormatTooNewException>(() => vault.SaveSettings(vault.GetSettings()));
        Assert.Equal(json, File.ReadAllText(DbPath));
        Assert.Equal(new[] { "vault.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void SettingsViewModel_ReportsReadOnlyVault()
    {
        File.WriteAllText(DbPath, """{"FormatVersion":2,"Keys":[]}""");
        var vm = new SettingsViewModel(new SecureVaultService(_dir, new FakeHelloProtector()));

        Assert.NotNull(vm.VaultReadOnlyMessage);
        vm.UseTimestamp = !vm.UseTimestamp; // must not throw
        Assert.StartsWith("Ошибка", vm.StatusMessage);
    }
}

/// <summary>HttpMessageHandler answering from a function, counting requests.</summary>
internal sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(respond(request));
    }
}

public class UpdateCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_check_" + Guid.NewGuid().ToString("N"));

    public UpdateCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static HttpResponseMessage Json(string json, string etag = "\"e1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private UpdateViewModel MakeViewModel(string current, FakeGitHub handler, UpdateStateStore? store = null) =>
        new(AppVersion.Parse(current), new AppInstallation(Path.Combine(_dir, "install")), isInstalledCopy: true,
            store ?? new UpdateStateStore(Path.Combine(_dir, "update.json")),
            new GitHubReleaseClient(new HttpClient(handler)), downloader: null);

    [Fact]
    public async Task OffersNewerVersion_AndNotifiesOnce()
    {
        var handler = new FakeGitHub(_ => Json(ReleaseSelectionTests.ReleaseJson("v0.2.0")));
        var store = new UpdateStateStore(Path.Combine(_dir, "update.json"));
        int notifications = 0;

        var vm = MakeViewModel("0.1.0", handler, store);
        vm.NotificationRequested += (_, _) => notifications++;
        await vm.CheckAsync(manual: false);
        await vm.CheckAsync(manual: false);

        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal("0.2.0", vm.AvailableVersionText);
        Assert.Equal(1, notifications);

        // A restart does not notify again for the same version.
        var restarted = MakeViewModel("0.1.0", handler, store);
        restarted.NotificationRequested += (_, _) => notifications++;
        await restarted.CheckAsync(manual: false);
        Assert.Equal(1, notifications);
        Assert.True(restarted.IsUpdateAvailable);
    }

    [Fact]
    public async Task SameOrOlderVersion_IsNotOffered()
    {
        var vm = MakeViewModel("0.2.0", new FakeGitHub(_ => Json(ReleaseSelectionTests.ReleaseJson("v0.2.0"))));
        await vm.CheckAsync(manual: true);
        Assert.False(vm.IsUpdateAvailable);
        Assert.StartsWith("Установлена последняя версия", vm.CheckStatusText);
    }

    [Fact]
    public async Task UnsignedRelease_IsNotOffered()
    {
        var vm = MakeViewModel("0.1.0", new FakeGitHub(_ => Json(ReleaseSelectionTests.ReleaseJson("v0.2.0", withSignatures: false))));
        await vm.CheckAsync(manual: true);
        Assert.False(vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task RolledBackVersion_IsOnlyShownOnManualCheck()
    {
        var store = new UpdateStateStore(Path.Combine(_dir, "update.json"));
        store.Update(s => s.SkippedVersion = "0.2.0");
        int notifications = 0;
        var vm = MakeViewModel("0.1.0", new FakeGitHub(_ => Json(ReleaseSelectionTests.ReleaseJson("v0.2.0"))), store);
        vm.NotificationRequested += (_, _) => notifications++;

        await vm.CheckAsync(manual: false);
        Assert.False(vm.IsUpdateAvailable);

        await vm.CheckAsync(manual: true);
        Assert.True(vm.IsUpdateAvailable);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Offline_RateLimited_AndNotModified_AreQuiet()
    {
        int call = 0;
        var handler = new FakeGitHub(request =>
        {
            call++;
            if (call == 1) return Json(ReleaseSelectionTests.ReleaseJson("v0.2.0"));
            if (call == 2)
            {
                Assert.Equal("\"e1\"", request.Headers.IfNoneMatch.Single().ToString());
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            if (call == 3)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                limited.Headers.Add("x-ratelimit-remaining", "0");
                limited.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString());
                return limited;
            }
            throw new HttpRequestException("offline");
        });
        var client = new GitHubReleaseClient(new HttpClient(handler));

        Assert.Equal("0.2.0", (await client.GetLatestAsync())!.Version.ToString());
        Assert.Equal("0.2.0", (await client.GetLatestAsync())!.Version.ToString()); // 304 → cached
        Assert.Equal("0.2.0", (await client.GetLatestAsync())!.Version.ToString()); // 403 → cached, limited
        Assert.NotNull(client.RateLimitedUntil);
        Assert.Equal("0.2.0", (await client.GetLatestAsync())!.Version.ToString()); // no request while limited
        Assert.Equal(3, handler.Requests);

        var offline = new GitHubReleaseClient(new HttpClient(new FakeGitHub(_ => throw new HttpRequestException("offline"))));
        Assert.Null(await offline.GetLatestAsync());
    }

    [Fact]
    public void HandOffArguments_AreRecognised()
    {
        string[] args = { "--updated-from", "0.1.0", "--background" };
        Assert.True(UpdateViewModel.IsHandOffStart(args));
        Assert.Equal("0.1.0", UpdateViewModel.ArgumentValue(args, UpdateViewModel.UpdatedFromArgument));
        Assert.False(UpdateViewModel.IsHandOffStart(new[] { "--background" }));

        var vm = MakeViewModel("0.2.0", new FakeGitHub(_ => throw new HttpRequestException()));
        vm.ApplyStartupArguments(args);
        Assert.Contains("0.2.0", vm.RestartBannerText);
    }
}

public class SetupServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_setup_" + Guid.NewGuid().ToString("N"));

    public SetupServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void CreatesShellLink()
    {
        string link = Path.Combine(_dir, "NativeNCA.lnk");
        SetupService.CreateShortcut(link, typeof(AppVersion).Assembly.Location, "test");

        byte[] header = File.ReadAllBytes(link)[..4];
        Assert.Equal(new byte[] { 0x4C, 0, 0, 0 }, header);
    }

    [Fact]
    public async Task RemovesInstallFolder_InTheBackground()
    {
        string install = Path.Combine(_dir, "Programs", "NativeNCA");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, AppInstallation.ExeName), "x");

        new SetupService(new AppInstallation(install)).ScheduleDirectoryRemoval();

        for (int i = 0; i < 100 && Directory.Exists(install); i++) await Task.Delay(100);
        Assert.False(Directory.Exists(install));
        Assert.True(Directory.Exists(Path.Combine(_dir, "Programs")));
    }
}
