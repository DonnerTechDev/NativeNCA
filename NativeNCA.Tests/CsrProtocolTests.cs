using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Server;
using NativeNCA.Core.Services;
using NativeNCA.Core.ViewModels;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Xunit;

namespace NativeNCA.Tests;

public sealed class CsrProtocolTests : IDisposable
{
    private const string Origin = "https://nca.pki.gov.kz";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_csr_tests_" + Guid.NewGuid().ToString("N"));
    private readonly SecureVaultService _vault;
    private readonly PendingCsrStore _pendingStore;

    public CsrProtocolTests()
    {
        Directory.CreateDirectory(_dir);
        _vault = new SecureVaultService(_dir);
        _pendingStore = new PendingCsrStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static JsonObject BasicsRequest(string method, JsonObject args) => new()
    {
        ["module"] = NcaProtocolHandler.BasicsModule,
        ["method"] = method,
        ["args"] = args
    };

    private static JsonObject StandardDn() => new()
    {
        ["2.5.4.3"] = "ТЕСТОВ ТЕСТ ТЕСТОВИЧ",
        ["2.5.4.5"] = "IIN900101300123",
        ["2.5.4.6"] = "KZ",
        ["2.5.4.7"] = "АСТАНА",
        ["2.5.4.10"] = "ТОО ТЕСТ",
        ["2.5.4.11"] = "BIN180340012345"
    };

    [Fact]
    public async Task ObtainAllStorages_returns_PKCS12_storage()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        var req = BasicsRequest("obtainAllStorages", new JsonObject());

        var response = JsonNode.Parse(await handler.HandleAsync(req.ToJsonString(), Origin, default))!;

        Assert.True(response["status"]?.GetValue<bool>());
        var storages = response["body"]?.AsArray();
        Assert.NotNull(storages);
        Assert.Single(storages);

        var storage = storages[0]?.AsObject();
        Assert.NotNull(storage);
        Assert.Equal("PKCS12", storage["storage"]?.GetValue<string>());
    }

    [Fact]
    public async Task GenerateCsr_gost512_success_stores_pending_key()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        var args = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["algorithm"] = "GOST512",
            ["dn"] = StandardDn()
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("generateCsr", args).ToJsonString(), Origin, default))!;

        Assert.True(response["status"]?.GetValue<bool>());
        var pems = response["body"]?["pems"]?.AsArray();
        Assert.NotNull(pems);
        Assert.Single(pems);
        string? csrPem = pems[0]?.GetValue<string>();
        Assert.NotNull(csrPem);
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", csrPem);

        // Verify that pending CSR store has exactly 1 key
        Assert.Equal(1, _pendingStore.Count);

        // Parse CSR to check KeyId
        var reader = new PemReader(new StringReader(csrPem));
        var pkcs10 = Assert.IsType<Pkcs10CertificationRequest>(reader.ReadObject());
        var spki = pkcs10.GetCertificationRequestInfo().SubjectPublicKeyInfo;
        string expectedKeyId = Convert.ToHexString(SHA1.HashData(spki.PublicKey.GetBytes())).ToLowerInvariant();

        var pendingKey = _pendingStore.Find(expectedKeyId);
        Assert.NotNull(pendingKey);
        Assert.Equal(expectedKeyId, pendingKey.KeyId);
        Assert.Equal("GOST512", pendingKey.Prefix);
    }

    [Fact]
    public async Task GenerateCsr_rsa_success_stores_pending_key()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        var args = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["algorithm"] = "RSA",
            ["keyLength"] = 2048,
            ["dn"] = StandardDn()
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("generateCsr", args).ToJsonString(), Origin, default))!;

        Assert.True(response["status"]?.GetValue<bool>());
        var pems = response["body"]?["pems"]?.AsArray();
        Assert.NotNull(pems);
        Assert.Single(pems);
        string? csrPem = pems[0]?.GetValue<string>();
        Assert.NotNull(csrPem);
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", csrPem);

        Assert.Equal(1, _pendingStore.Count);
    }

    [Fact]
    public async Task GenerateCsr_when_cancelled_returns_empty_body_and_no_pending_key()
    {
        var prompt = new FakePrompt(_vault, approve: false);
        var handler = new NcaProtocolHandler(_vault, prompt, tsa: null, pendingCsrStore: _pendingStore);
        var args = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["algorithm"] = "GOST512",
            ["dn"] = StandardDn()
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("generateCsr", args).ToJsonString(), Origin, default))!;

        Assert.True(response["status"]?.GetValue<bool>());
        var body = response["body"]?.AsObject();
        Assert.NotNull(body);
        Assert.Empty(body); // NCALayer Basics cancel contract: {"status": true, "body": {}}

        Assert.Equal(0, _pendingStore.Count);
    }

    [Fact]
    public async Task GenerateCsr_unsupported_storage_returns_error()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        var args = new JsonObject
        {
            ["storageType"] = "AKKaztokenStore", // token storage not supported yet
            ["algorithm"] = "GOST512",
            ["dn"] = StandardDn()
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("generateCsr", args).ToJsonString(), Origin, default))!;

        Assert.False(response["status"]?.GetValue<bool>());
        Assert.Equal("CSR_GENERATING_FAILURE", response["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task GenerateCsr_missing_dn_returns_error()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        var args = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["algorithm"] = "GOST512"
            // no "dn"
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("generateCsr", args).ToJsonString(), Origin, default))!;

        Assert.False(response["status"]?.GetValue<bool>());
        Assert.Equal("CSR_GENERATING_FAILURE", response["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task ImportCertificate_empty_list_returns_error()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        var args = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["certificates"] = new JsonArray()
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("importCertificate", args).ToJsonString(), Origin, default))!;

        Assert.False(response["status"]?.GetValue<bool>());
        Assert.Equal("CERTIFICATE_IMPORTING_FAILURE", response["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task ImportCertificate_unknown_certificate_returns_key_not_found()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault), tsa: null, pendingCsrStore: _pendingStore);
        byte[] pfx = TestKeys.CreateGostPfx();
        var contents = Pkcs12Reader.Read(pfx, TestKeys.Password);

        var args = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["certificates"] = new JsonArray(Convert.ToBase64String(contents.Certificate.GetEncoded()))
        };

        var response = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("importCertificate", args).ToJsonString(), Origin, default))!;

        Assert.False(response["status"]?.GetValue<bool>());
        Assert.Equal("CERTIFICATE_IMPORTING_FAILURE", response["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task Full_reissue_lifecycle_generateCsr_import_and_sign()
    {
        var backupDir = Path.Combine(_dir, "backup");
        Directory.CreateDirectory(backupDir);

        var prompt = new FakePrompt(_vault);
        var handler = new NcaProtocolHandler(_vault, prompt, tsa: null, pendingCsrStore: _pendingStore);

        // 1. Generate CSR
        var genArgs = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["storagePath"] = backupDir,
            ["algorithm"] = "GOST512",
            ["dn"] = StandardDn()
        };

        var genResp = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("generateCsr", genArgs).ToJsonString(), Origin, default))!;
        Assert.True(genResp["status"]?.GetValue<bool>());
        var pems = genResp["body"]!["pems"]!.AsArray();
        Assert.Single(pems);
        string csrPem = pems[0]!.GetValue<string>();

        // 2. Extract public key from CSR and issue certificate (simulating CA response)
        var reader = new PemReader(new StringReader(csrPem));
        var pkcs10 = Assert.IsType<Pkcs10CertificationRequest>(reader.ReadObject());
        var spki = pkcs10.GetCertificationRequestInfo().SubjectPublicKeyInfo;
        var issuedCert = TestKeys.CreateCertFromSpki(spki, isGost: true);
        string certBase64 = Convert.ToBase64String(issuedCert.GetEncoded());

        // 3. Import Certificate
        var importArgs = new JsonObject
        {
            ["storageType"] = "PKCS12",
            ["keystorePath"] = backupDir,
            ["defaultPassword"] = TestKeys.Password,
            ["certificates"] = new JsonArray(certBase64)
        };

        var importResp = JsonNode.Parse(await handler.HandleAsync(BasicsRequest("importCertificate", importArgs).ToJsonString(), Origin, default))!;
        Assert.True(importResp["status"]?.GetValue<bool>());
        Assert.True(importResp["body"]?["result"]?.GetValue<bool>());

        // Verify key was imported into vault
        var vaultKeys = _vault.GetKeys();
        Assert.Single(vaultKeys);
        var importedKey = vaultKeys[0];
        Assert.Equal(CertificateInfo.GostAlgorithm, importedKey.KeyAlgorithm);

        // Verify pending store is now empty
        Assert.Equal(0, _pendingStore.Count);

        // Verify backup .p12 was written to backupDir
        var files = Directory.GetFiles(backupDir, "*.p12");
        Assert.Single(files);
        Assert.Contains("GOST512_", Path.GetFileName(files[0]));

        // Verify backup .p12 can be decrypted and contains matching certificate
        var backupContents = Pkcs12Reader.Read(File.ReadAllBytes(files[0]), TestKeys.Password);
        Assert.Equal(importedKey.CertificateSerial, backupContents.Certificate.SerialNumber.ToString(16));

        // 4. Test signing with the newly imported key via signXml
        var signReq = new JsonObject
        {
            ["module"] = NcaProtocolHandler.CommonUtilsModule,
            ["method"] = "signXml",
            ["args"] = new JsonArray("PKCS12", "SIGNATURE", "<reissued><id>123</id></reissued>", "", "")
        };

        var signResp = JsonNode.Parse(await handler.HandleAsync(signReq.ToJsonString(), Origin, default))!;
        Assert.Equal("200", signResp["code"]?.GetValue<string>());
        string signedXml = signResp["responseObject"]!.GetValue<string>();
        Assert.True(XmlDsigSigner.Verify(signedXml, out _));
    }
}
