using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Services;

namespace NativeNCA.Core.Server;

/// <summary>
/// NCALayer-compatible request handling, independent of the transport.
///
/// Supported:
///  - module kz.gov.pki.knca.commonUtils: getActiveTokens, getKeyInfo, createCAdESFromBase64,
///    createCMSSignatureFromBase64, createCAdESFromBase64Hash, signXml, signXmls, changeLocale;
///  - module kz.gov.pki.knca.basics: sign (format cms/xml, single or multiple documents).
/// Only one confirmation overlay is shown at a time; concurrent requests wait in a queue.
/// </summary>
public class NcaProtocolHandler
{
    public const string CommonUtilsModule = "kz.gov.pki.knca.commonUtils";
    public const string BasicsModule = "kz.gov.pki.knca.basics";
    private const int PreviewLimit = 4000;

    private readonly SecureVaultService _vault;
    private readonly IUserPromptService _prompt;
    private readonly ITimestampProvider _tsa;
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public NcaProtocolHandler(SecureVaultService vault, IUserPromptService prompt, ITimestampProvider? tsa = null)
    {
        _vault = vault;
        _prompt = prompt;
        _tsa = tsa ?? new NcaTimestampService();
    }

    /// <summary>CAdES-BES → CAdES-T when requested.</summary>
    private async Task<string> FinishCmsAsync(byte[] cms, bool timestamp, CancellationToken ct) =>
        Convert.ToBase64String(timestamp ? await CmsSigner.AddTimestampAsync(cms, _tsa, ct) : cms);

    public async Task<string> HandleAsync(string requestJson, string origin, CancellationToken ct)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(requestJson)?.AsObject() ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return CommonError("Malformed request");
        }

        string module = request["module"]?.GetValue<string>() ?? CommonUtilsModule;
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonNode? args = request["args"];

        try
        {
            return module switch
            {
                CommonUtilsModule => await HandleCommonUtilsAsync(method, args as JsonArray ?? new JsonArray(), origin, ct),
                BasicsModule when method == "sign" => await HandleBasicsSignAsync(args as JsonObject ?? new JsonObject(), origin, ct),
                BasicsModule => BasicsError("unsupported.method", $"Метод {method} не поддерживается"),
                _ => CommonError($"Модуль {module} не поддерживается NativeNCA")
            };
        }
        catch (OperationCanceledException)
        {
            return module == BasicsModule ? BasicsCancelled() : CommonCancelled();
        }
        catch (Exception ex)
        {
            return module == BasicsModule ? BasicsError("internal.error", ex.Message) : CommonError(ex.Message);
        }
    }

    // ===================== commonUtils =====================

    private async Task<string> HandleCommonUtilsAsync(string method, JsonArray args, string origin, CancellationToken ct)
    {
        switch (method)
        {
            case "getActiveTokens":
                // Hardware tokens are not supported; sites fall back to the file storage (PKCS12).
                return CommonOk(new JsonArray());

            case "changeLocale":
                return CommonOk((JsonNode?)null);

            case "getKeyInfo":
            {
                var result = await PromptAsync(new SigningPromptRequest
                {
                    Origin = origin,
                    Method = method,
                    Description = "Предоставление сертификата",
                    DataToSignPreview = "Сайт запрашивает сведения о вашем сертификате: ФИО, ИИН, срок действия. Закрытый ключ не используется.",
                    RequiredUsage = KeyUsageFilter.Any,
                    RequiresPrivateKey = false
                }, ct);

                return result.Approved && result.SelectedKey != null
                    ? CommonOk(BuildKeyInfo(result.SelectedKey))
                    : CommonCancelled();
            }

            case "createCAdESFromBase64":
            case "createCMSSignatureFromBase64":
            {
                var usage = ParseKeyType(Arg(args, 1));
                byte[] data = Convert.FromBase64String(Arg(args, 2) ?? string.Empty);
                bool attached = args.Count > 3 && args[3]?.GetValueKind() == JsonValueKind.True;

                bool tsa = _vault.GetSettings().UseTimestamp;

                return await SignAsync(origin, method, "Подписание документа (CMS)", DescribeBinary(data), usage, ct,
                    key => FinishCmsAsync(CmsSigner.Sign(key, data, attached), tsa, ct));
            }

            case "createCAdESFromBase64Hash":
            {
                var usage = ParseKeyType(Arg(args, 1));
                byte[] hash = Convert.FromBase64String(Arg(args, 2) ?? string.Empty);

                bool tsa = _vault.GetSettings().UseTimestamp;

                return await SignAsync(origin, method, "Подписание хеша документа", $"Хеш: {Convert.ToBase64String(hash)}", usage, ct,
                    key => FinishCmsAsync(CmsSigner.SignDigest(key, hash), tsa, ct));
            }

            case "signXml":
            {
                var usage = ParseKeyType(Arg(args, 1));
                string xml = Arg(args, 2) ?? throw new ArgumentException("Пустые данные XML");
                string? tbs = NullIfEmpty(Arg(args, 3));
                string? parent = NullIfEmpty(Arg(args, 4));

                return await SignAsync(origin, method, "Подписание XML-документа", Truncate(xml), usage, ct,
                    key => Task.FromResult(XmlDsigSigner.Sign(key, xml, tbs, parent)));
            }

            case "signXmls":
            {
                var usage = ParseKeyType(Arg(args, 1));
                var xmls = (args.Count > 2 ? args[2] as JsonArray : null)?.Select(n => n!.GetValue<string>()).ToList()
                           ?? throw new ArgumentException("Пустой список XML");
                string? tbs = NullIfEmpty(Arg(args, 3));
                string? parent = NullIfEmpty(Arg(args, 4));

                return await SignManyAsync(origin, method, $"Подписание XML-документов ({xmls.Count})",
                    Truncate(string.Join("\n\n", xmls)), usage, ct,
                    key => Task.FromResult<JsonNode>(new JsonArray(xmls.Select(x => (JsonNode)XmlDsigSigner.Sign(key, x, tbs, parent)).ToArray())));
            }

            case "createCMSSignatureFromFile":
            case "createCAdESFromFile":
                return CommonError("Подписание файлов с диска не поддерживается. Используйте подписание данных в base64.");

            default:
                return CommonError($"Неизвестный метод {method}");
        }
    }

    private async Task<string> SignAsync(string origin, string method, string description, string preview,
        KeyUsageFilter usage, CancellationToken ct, Func<SigningKey, Task<string>> sign)
    {
        var result = await PromptAsync(new SigningPromptRequest
        {
            Origin = origin, Method = method, Description = description, DataToSignPreview = preview, RequiredUsage = usage
        }, ct);

        if (!result.Approved || result.Key == null || result.SelectedKey == null)
            return CommonCancelled();

        string signed = await sign(result.Key);
        _vault.MarkKeyUsed(result.SelectedKey.Id);
        return CommonOk(signed);
    }

    private async Task<string> SignManyAsync(string origin, string method, string description, string preview,
        KeyUsageFilter usage, CancellationToken ct, Func<SigningKey, Task<JsonNode>> sign)
    {
        var result = await PromptAsync(new SigningPromptRequest
        {
            Origin = origin, Method = method, Description = description, DataToSignPreview = preview, RequiredUsage = usage
        }, ct);

        if (!result.Approved || result.Key == null || result.SelectedKey == null)
            return CommonCancelled();

        var signed = await sign(result.Key);
        _vault.MarkKeyUsed(result.SelectedKey.Id);
        return CommonOk(signed);
    }

    // ===================== basics =====================

    private async Task<string> HandleBasicsSignAsync(JsonObject args, string origin, CancellationToken ct)
    {
        if (args["allowedStorages"] is JsonArray storages && storages.Count > 0 &&
            !storages.Any(s => string.Equals(s?.GetValue<string>(), "PKCS12", StringComparison.OrdinalIgnoreCase)))
        {
            return BasicsError("storage.unsupported", "Сайт требует аппаратный носитель (токен или ID-карту), он не поддерживается NativeNCA");
        }

        string format = args["format"]?.GetValue<string>()?.ToLowerInvariant() ?? "cms";
        var signingParams = args["signingParams"] as JsonObject;
        var signerParams = args["signerParams"] as JsonObject;

        bool decode = Flag(signingParams?["decode"]);
        bool encapsulate = Flag(signingParams?["encapsulate"]);
        bool digested = Flag(signingParams?["digested"]);
        // NCALayer adds a TSA time-stamp when the site passes a tsaProfile (even an empty one).
        bool timestamp = signingParams?.ContainsKey("tsaProfile") == true && signingParams["tsaProfile"] != null;

        bool isArray = args["data"] is JsonArray;
        List<string> items = args["data"] switch
        {
            JsonArray arr => arr.Select(n => n?.GetValue<string>() ?? string.Empty).ToList(),
            JsonValue v => new List<string> { v.GetValue<string>() },
            _ => throw new ArgumentException("Нет данных для подписи")
        };

        var usage = KeyUsageFilter.Any;
        if (signerParams?["extKeyUsageOids"] is JsonArray ekus)
        {
            var oids = ekus.Select(e => e?.GetValue<string>()).ToHashSet();
            if (oids.Contains(KzOids.EkuClientAuth) && !oids.Contains(KzOids.EkuEmailProtection)) usage = KeyUsageFilter.Auth;
            else if (oids.Contains(KzOids.EkuEmailProtection) && !oids.Contains(KzOids.EkuClientAuth)) usage = KeyUsageFilter.Sign;
        }

        string preview = format == "xml"
            ? Truncate(string.Join("\n\n", items))
            : digested
                ? $"Хеш: {string.Join(", ", items)}"
                : string.Join("\n\n", items.Select(i => DescribeBinary(decode ? Convert.FromBase64String(i) : Encoding.UTF8.GetBytes(i))));

        var result = await PromptAsync(new SigningPromptRequest
        {
            Origin = origin,
            Method = "basics.sign",
            Description = items.Count > 1 ? $"Подписание документов ({items.Count})" : format == "xml" ? "Подписание XML-документа" : "Подписание документа",
            DataToSignPreview = Truncate(preview),
            RequiredUsage = usage,
            RequiredIin = signerParams?["iin"]?.GetValue<string>(),
            RequiredBin = signerParams?["bin"]?.GetValue<string>()
        }, ct);

        if (!result.Approved || result.Key == null || result.SelectedKey == null)
            return BasicsCancelled();

        var signed = new List<string>();
        foreach (var item in items)
        {
            if (format == "xml")
            {
                signed.Add(XmlDsigSigner.Sign(result.Key, item));
                continue;
            }

            byte[] bytes = decode ? Convert.FromBase64String(item) : Encoding.UTF8.GetBytes(item);
            byte[] cms = digested ? CmsSigner.SignDigest(result.Key, bytes) : CmsSigner.Sign(result.Key, bytes, encapsulate);
            signed.Add(await FinishCmsAsync(cms, timestamp, ct));
        }

        _vault.MarkKeyUsed(result.SelectedKey.Id);

        JsonNode payload = isArray ? new JsonArray(signed.Select(s => (JsonNode)s).ToArray()) : JsonValue.Create(signed[0])!;
        return new JsonObject { ["status"] = true, ["body"] = new JsonObject { ["result"] = payload } }.ToJsonString();
    }

    // ===================== helpers =====================

    private async Task<SigningPromptResult> PromptAsync(SigningPromptRequest request, CancellationToken ct)
    {
        await _promptGate.WaitAsync(ct);
        try
        {
            return await _prompt.PromptForSigningAsync(request, ct);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private static JsonObject BuildKeyInfo(VaultKeyItem key)
    {
        var cert = string.IsNullOrEmpty(key.CertificateBase64)
            ? null
            : new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(Convert.FromBase64String(key.CertificateBase64));
        var info = cert != null ? CertificateInfo.From(cert) : null;

        string? aki = null;
        var akiExt = cert?.GetExtensionValue(Org.BouncyCastle.Asn1.X509.X509Extensions.AuthorityKeyIdentifier);
        if (akiExt != null)
        {
            var akiObj = Org.BouncyCastle.Asn1.X509.AuthorityKeyIdentifier.GetInstance(
                Org.BouncyCastle.Asn1.Asn1Object.FromByteArray(akiExt.GetOctets()));
            aki = akiObj.KeyIdentifier is { } id ? Convert.ToHexString(id.GetOctets()).ToLowerInvariant() : null;
        }

        return new JsonObject
        {
            ["alias"] = key.Name,
            ["keyId"] = key.Id.ToString("N"),
            ["algorithm"] = key.KeyAlgorithm == "RSA" ? "RSA" : "ECGOST34310",
            ["subjectCn"] = info?.CommonName ?? key.FullName,
            ["subjectDn"] = key.Subject,
            ["issuerCn"] = info?.IssuerCommonName,
            ["issuerDn"] = key.Issuer,
            ["serialNumber"] = key.CertificateSerial,
            ["certNotAfter"] = new DateTimeOffset(DateTime.SpecifyKind(key.NotAfter, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(),
            ["certNotBefore"] = new DateTimeOffset(DateTime.SpecifyKind(key.NotBefore, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(),
            ["authorityKeyIdentifier"] = aki,
            ["pem"] = cert != null ? CertificateInfo.ToPem(cert) : null
        };
    }

    private static KeyUsageFilter ParseKeyType(string? keyType) => keyType?.ToUpperInvariant() switch
    {
        "SIGNATURE" or "SIGN" => KeyUsageFilter.Sign,
        "AUTHENTICATION" or "AUTH" => KeyUsageFilter.Auth,
        _ => KeyUsageFilter.Any
    };

    private static string? Arg(JsonArray args, int index) =>
        args.Count > index && args[index] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool Flag(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => bool.TryParse(v.GetValue<string>(), out var b) && b,
        _ => false
    };

    private static string Truncate(string s) => s.Length > PreviewLimit ? s[..PreviewLimit] + "…" : s;

    /// <summary>Shows text when the data is readable UTF-8, otherwise size and fingerprint.</summary>
    internal static string DescribeBinary(byte[] data)
    {
        try
        {
            string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
            if (text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t'))
                return Truncate(text);
        }
        catch (DecoderFallbackException)
        {
        }

        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))[..16];
        return $"Двоичные данные, {data.Length:N0} байт · SHA-256 {sha}…";
    }

    // commonUtils responses
    private static string CommonOk(JsonNode? responseObject) =>
        new JsonObject { ["code"] = "200", ["responseObject"] = responseObject }.ToJsonString();

    private static string CommonOk(string responseObject) => CommonOk(JsonValue.Create(responseObject));

    private static string CommonError(string message) =>
        new JsonObject { ["code"] = "500", ["message"] = message }.ToJsonString();

    private static string CommonCancelled() => CommonError("action.canceled");

    // basics responses
    private static string BasicsCancelled() =>
        new JsonObject { ["status"] = true, ["body"] = new JsonObject() }.ToJsonString();

    private static string BasicsError(string code, string message) =>
        new JsonObject { ["status"] = false, ["code"] = code, ["message"] = message, ["details"] = message }.ToJsonString();
}
