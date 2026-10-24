using System.Text;
using System.Text.Json.Nodes;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Server;
using NativeNCA.Core.Services;
using NativeNCA.Core.ViewModels;

namespace NativeNCA.Tests;

/// <summary>
/// Stands in for the overlay: builds the real SignOverlayViewModel (so key filtering is the
/// production logic), picks the first offered key and unlocks it with the test password.
/// </summary>
internal sealed class FakePrompt(SecureVaultService vault, bool approve = true) : IUserPromptService
{
    private int _active;

    public List<SigningPromptRequest> Requests { get; } = new();
    public List<int> OfferedKeyCounts { get; } = new();
    public int MaxConcurrent { get; private set; }

    public async Task<SigningPromptResult> PromptForSigningAsync(SigningPromptRequest request, CancellationToken ct = default)
    {
        int now = Interlocked.Increment(ref _active);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            lock (Requests) Requests.Add(request);
            await Task.Delay(30, ct);

            var vm = new SignOverlayViewModel(vault, new ConsentCache(), request);
            lock (OfferedKeyCounts) OfferedKeyCounts.Add(vm.MatchingKeys.Count);

            if (!approve || vm.SelectedKey == null) return SigningPromptResult.Cancelled;

            var item = vm.SelectedKey.Model;
            return new SigningPromptResult
            {
                Approved = true,
                SelectedKey = item,
                Key = request.RequiresPrivateKey ? vault.OpenKey(item, TestKeys.Password) : null
            };
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public Task ShowNotificationAsync(string title, string message) => Task.CompletedTask;
}

public sealed class ProtocolTests : IDisposable
{
    private const string Origin = "https://online.bank.kz";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nnca_tests_" + Guid.NewGuid().ToString("N"));
    private readonly SecureVaultService _vault;

    public ProtocolTests()
    {
        _vault = new SecureVaultService(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private VaultKeyItem AddKey(byte[] pfx)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".p12");
        File.WriteAllBytes(path, pfx);
        return _vault.AddKey(path, TestKeys.Password);
    }

    private static string CommonUtils(string method, params object?[] args) => new JsonObject
    {
        ["module"] = NcaProtocolHandler.CommonUtilsModule,
        ["method"] = method,
        ["args"] = new JsonArray(args.Select(a => a switch
        {
            null => null,
            bool b => JsonValue.Create(b),
            string[] arr => (JsonNode)new JsonArray(arr.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            _ => (JsonNode?)JsonValue.Create(a.ToString())
        }).ToArray())
    }.ToJsonString();

    [Fact]
    public async Task GetActiveTokens_returns_empty_list()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault));
        var response = JsonNode.Parse(await handler.HandleAsync(CommonUtils("getActiveTokens"), Origin, default))!;

        Assert.Equal("200", response["code"]!.GetValue<string>());
        Assert.Empty(response["responseObject"]!.AsArray());
    }

    [Fact]
    public async Task SignXml_returns_valid_signed_document()
    {
        AddKey(TestKeys.CreateGostPfx());
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault));

        var response = JsonNode.Parse(await handler.HandleAsync(
            CommonUtils("signXml", "PKCS12", "SIGNATURE", "<login><nonce>42</nonce></login>", "", ""), Origin, default))!;

        Assert.Equal("200", response["code"]!.GetValue<string>());
        Assert.True(XmlDsigSigner.Verify(response["responseObject"]!.GetValue<string>(), out _));
    }

    [Fact]
    public async Task CreateCAdESFromBase64_returns_valid_cms()
    {
        AddKey(TestKeys.CreateGostPfx());
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault));
        byte[] data = Encoding.UTF8.GetBytes("payment order");

        var response = JsonNode.Parse(await handler.HandleAsync(
            CommonUtils("createCAdESFromBase64", "PKCS12", "SIGNATURE", Convert.ToBase64String(data), true), Origin, default))!;

        var cms = Convert.FromBase64String(response["responseObject"]!.GetValue<string>());
        Assert.True(CmsVerifier.Verify(cms).IsValid);
    }

    [Fact]
    public async Task Gost_key_with_only_emailProtection_is_offered_for_login()
    {
        // Real НУЦ GOST512 keys (including legal-entity keys) list only emailProtection in EKU,
        // yet are used to log in to banks. Regression test for "Нет подходящих ключей".
        AddKey(TestKeys.CreateGostPfx(authAndSign: false));
        var prompt = new FakePrompt(_vault);
        var handler = new NcaProtocolHandler(_vault, prompt);

        var request = new JsonObject
        {
            ["module"] = NcaProtocolHandler.BasicsModule,
            ["method"] = "sign",
            ["args"] = new JsonObject
            {
                ["allowedStorages"] = new JsonArray("PKCS12"),
                ["format"] = "cms",
                ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("auth-challenge")),
                ["signingParams"] = new JsonObject { ["decode"] = true, ["encapsulate"] = true },
                ["signerParams"] = new JsonObject { ["extKeyUsageOids"] = new JsonArray(KzOids.EkuClientAuth) },
                ["locale"] = "ru"
            }
        }.ToJsonString();

        var response = JsonNode.Parse(await handler.HandleAsync(request, Origin, default))!;

        Assert.Equal(1, prompt.OfferedKeyCounts.Single());
        Assert.True(response["status"]!.GetValue<bool>());
        var cms = Convert.FromBase64String(response["body"]!["result"]!.AsArray().Single()!.GetValue<string>());
        Assert.True(CmsVerifier.Verify(cms).IsValid);
    }

    [Fact]
    public async Task Basics_sign_single_string_returns_array_like_egov_expects()
    {
        AddKey(TestKeys.CreateGostPfx());
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault));

        // The request egov.kz sends for login; it then posts body.result[0] to its server.
        var request = new JsonObject
        {
            ["module"] = NcaProtocolHandler.BasicsModule,
            ["method"] = "sign",
            ["args"] = new JsonObject
            {
                ["data"] = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><login><client-type>PORTAL</client-type></login>",
                ["allowedStorages"] = new JsonArray("PKCS12", "AKKaztokenStore", "AKKZIDCardStore"),
                ["format"] = "xml",
                ["signingParams"] = new JsonObject(),
                ["signerParams"] = new JsonObject { ["extKeyUsageOids"] = new JsonArray(KzOids.EkuClientAuth) },
                ["locale"] = "ru"
            }
        }.ToJsonString();

        var response = JsonNode.Parse(await handler.HandleAsync(request, Origin, default))!;

        Assert.True(response["status"]!.GetValue<bool>());
        string xml = response["body"]!["result"]!.AsArray().Single()!.GetValue<string>();
        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<client-type>PORTAL</client-type>", xml);
        Assert.True(XmlDsigSigner.Verify(xml, out _));
    }

    [Fact]
    public async Task Basics_sign_with_array_returns_array()
    {
        AddKey(TestKeys.CreateGostPfx());
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault));

        var request = new JsonObject
        {
            ["module"] = NcaProtocolHandler.BasicsModule,
            ["method"] = "sign",
            ["args"] = new JsonObject
            {
                ["format"] = "xml",
                ["data"] = new JsonArray("<a>1</a>", "<b>2</b>"),
                ["signingParams"] = new JsonObject(),
                ["signerParams"] = new JsonObject()
            }
        }.ToJsonString();

        var result = JsonNode.Parse(await handler.HandleAsync(request, Origin, default))!["body"]!["result"]!.AsArray();

        Assert.Equal(2, result.Count);
        Assert.All(result, x => Assert.True(XmlDsigSigner.Verify(x!.GetValue<string>(), out _)));
    }

    [Fact]
    public async Task Cancel_uses_NCALayer_response_shapes()
    {
        AddKey(TestKeys.CreateGostPfx());
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault, approve: false));

        var common = JsonNode.Parse(await handler.HandleAsync(CommonUtils("signXml", "PKCS12", "SIGNATURE", "<a/>", "", ""), Origin, default))!;
        Assert.Equal("500", common["code"]!.GetValue<string>());
        Assert.Equal("action.canceled", common["message"]!.GetValue<string>());

        string basicsReq = new JsonObject
        {
            ["module"] = NcaProtocolHandler.BasicsModule, ["method"] = "sign",
            ["args"] = new JsonObject { ["format"] = "cms", ["data"] = "AAAA", ["signingParams"] = new JsonObject { ["decode"] = true } }
        }.ToJsonString();
        var basics = JsonNode.Parse(await handler.HandleAsync(basicsReq, Origin, default))!;
        Assert.True(basics["status"]!.GetValue<bool>());
        Assert.Null(basics["body"]!["result"]);
    }

    [Fact]
    public async Task GetKeyInfo_returns_certificate_without_unlocking_key()
    {
        var item = AddKey(TestKeys.CreateGostPfx());
        var prompt = new FakePrompt(_vault);
        var handler = new NcaProtocolHandler(_vault, prompt);

        var info = JsonNode.Parse(await handler.HandleAsync(CommonUtils("getKeyInfo", "PKCS12"), Origin, default))!["responseObject"]!;

        Assert.False(prompt.Requests.Single().RequiresPrivateKey);
        Assert.Equal(item.CertificateSerial, info["serialNumber"]!.GetValue<string>());
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", info["pem"]!.GetValue<string>());
        Assert.Equal("ECGOST34310", info["algorithm"]!.GetValue<string>());
    }

    [Fact]
    public async Task Concurrent_requests_show_one_prompt_at_a_time()
    {
        AddKey(TestKeys.CreateGostPfx());
        var prompt = new FakePrompt(_vault);
        var handler = new NcaProtocolHandler(_vault, prompt);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            handler.HandleAsync(CommonUtils("signXml", "PKCS12", "SIGNATURE", $"<n>{i}</n>", "", ""), Origin, default)));

        Assert.Equal(4, prompt.Requests.Count);
        Assert.Equal(1, prompt.MaxConcurrent);
    }

    [Fact]
    public async Task Unknown_module_and_malformed_json_return_errors()
    {
        var handler = new NcaProtocolHandler(_vault, new FakePrompt(_vault));

        Assert.Equal("500", JsonNode.Parse(await handler.HandleAsync("{not json", Origin, default))!["code"]!.GetValue<string>());
        var km = JsonNode.Parse(await handler.HandleAsync("{\"module\":\"kz.digiflow.mobile.extensions\",\"method\":\"getVersion\"}", Origin, default))!;
        Assert.Equal("500", km["code"]!.GetValue<string>());
    }

    [Fact]
    public void Change_password_reencrypts_stored_container()
    {
        var item = AddKey(TestKeys.CreateGostPfx());

        _vault.ChangeKeyPassword(item.Id, TestKeys.Password, "НовыйПароль1");

        Assert.False(_vault.VerifyKeyPassword(item.Id, TestKeys.Password));
        Assert.True(_vault.VerifyKeyPassword(item.Id, "НовыйПароль1"));
        var key = _vault.OpenKey(_vault.GetKey(item.Id)!, "НовыйПароль1");
        Assert.True(CmsVerifier.Verify(CmsSigner.Sign(key, new byte[] { 1, 2, 3 }, true)).IsValid);
    }

    private static byte[] TinyPng(byte shade)
    {
        // 1x1 PNG; changing a CRC byte keeps the PNG signature valid and makes the files differ.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        png[^5] ^= shade;
        return png;
    }

    [Fact]
    public void Avatar_is_stored_replaced_and_removed_with_the_key()
    {
        var item = AddKey(TestKeys.CreateGostPfx());

        _vault.SetAvatar(item.Id, TinyPng(1));
        string first = _vault.GetAvatarPath(_vault.GetKey(item.Id)!)!;
        Assert.True(File.Exists(first));

        _vault.SetAvatar(item.Id, TinyPng(2));
        string second = _vault.GetAvatarPath(_vault.GetKey(item.Id)!)!;
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(first));

        // Survives a restart
        var reopened = new SecureVaultService(_dir);
        Assert.Equal(second, reopened.GetAvatarPath(reopened.GetKey(item.Id)!));

        _vault.RemoveKey(item.Id);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void Avatar_can_be_removed_and_must_be_png()
    {
        var item = AddKey(TestKeys.CreateGostPfx());
        Assert.Throws<ArgumentException>(() => _vault.SetAvatar(item.Id, Encoding.UTF8.GetBytes("not an image")));

        _vault.SetAvatar(item.Id, TinyPng(3));
        string path = _vault.GetAvatarPath(_vault.GetKey(item.Id)!)!;
        _vault.RemoveAvatar(item.Id);

        Assert.Null(_vault.GetKey(item.Id)!.AvatarFileName);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Adding_the_same_key_twice_is_rejected()
    {
        var pfx = TestKeys.CreateGostPfx();
        AddKey(pfx);
        Assert.Throws<InvalidOperationException>(() => AddKey(pfx));
    }
}
