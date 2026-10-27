using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeNCA.Core.Crypto;
using NativeNCA.Core.Models;
using NativeNCA.Core.Security;
using NativeNCA.Core.Services;
using NativeNCA.Core.Localization;

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
    private readonly PendingCsrStore _pendingCsrStore;
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    public NcaProtocolHandler(SecureVaultService vault, IUserPromptService prompt, ITimestampProvider? tsa = null, PendingCsrStore? pendingCsrStore = null)
    {
        _vault = vault;
        _prompt = prompt;
        _tsa = tsa ?? new NcaTimestampService();
        _pendingCsrStore = pendingCsrStore ?? new PendingCsrStore();
    }

    /// <summary>CAdES-BES → CAdES-T when requested.</summary>
    private async Task<string> FinishCmsAsync(byte[] cms, bool timestamp, CancellationToken ct) =>
        Convert.ToBase64String(timestamp ? await CmsSigner.AddTimestampAsync(cms, _tsa, ct) : cms);

    public async Task<string> HandleAsync(string requestJson, string origin, CancellationToken ct)
    {
        LogProtocol($"[REQ from {origin}] {requestJson}");

        JsonObject request;
        try
        {
            request = JsonNode.Parse(requestJson)?.AsObject() ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return CommonError("Malformed request");
        }

        string module = GetString(request["module"]) ?? CommonUtilsModule;
        string method = GetString(request["method"]) ?? string.Empty;
        JsonNode? args = request["args"];

        try
        {
            string response = module switch
            {
                CommonUtilsModule => await HandleCommonUtilsAsync(method, args as JsonArray ?? new JsonArray(), origin, ct),
                BasicsModule when method == "sign" => await HandleBasicsSignAsync(args as JsonObject ?? new JsonObject(), origin, ct),
                BasicsModule when method == "generateCsr" => await HandleBasicsGenerateCsrAsync(args as JsonObject ?? new JsonObject(), origin, ct),
                BasicsModule when method == "importCertificate" => await HandleBasicsImportCertificateAsync(args as JsonObject ?? new JsonObject(), origin, ct),
                BasicsModule when method == "obtainAllStorages" => HandleBasicsObtainAllStorages(),
                BasicsModule => BasicsError("unsupported.method", $"Метод {method} не поддерживается"), // NoLoc: protocol message
                _ => CommonError($"Модуль {module} не поддерживается NativeNCA") // NoLoc: protocol message
            };

            LogProtocol($"[RESP {module}.{method}] {response}");
            return response;
        }
        catch (OperationCanceledException)
        {
            LogProtocol($"[CANCELED {module}.{method}]");
            return module == BasicsModule ? BasicsCancelled() : CommonCancelled();
        }
        catch (Exception ex)
        {
            LogProtocol($"[EX in {module}.{method}] {ex}");
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
                    Description = new LocText(() => Strings.Request_ShareCertificate),
                    DataToSignPreview = new LocText(() => Strings.Request_ShareCertificatePreview),
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

                return await SignAsync(origin, method, new LocText(() => Strings.Request_SignCms), new LocText(() => DescribeBinary(data)), usage, ct,
                    key => FinishCmsAsync(CmsSigner.Sign(key, data, attached), tsa, ct));
            }

            case "createCAdESFromBase64Hash":
            {
                var usage = ParseKeyType(Arg(args, 1));
                byte[] hash = Convert.FromBase64String(Arg(args, 2) ?? string.Empty);

                bool tsa = _vault.GetSettings().UseTimestamp;

                return await SignAsync(origin, method, new LocText(() => Strings.Request_SignHash),
                    new LocText(() => Loc.Format(Strings.Request_HashPreview, Convert.ToBase64String(hash))), usage, ct,
                    key => FinishCmsAsync(CmsSigner.SignDigest(key, hash), tsa, ct));
            }

            case "signXml":
            {
                var usage = ParseKeyType(Arg(args, 1));
                string xml = Arg(args, 2) ?? throw new ArgumentException("Пустые данные XML"); // NoLoc: protocol message
                string? tbs = NullIfEmpty(Arg(args, 3));
                string? parent = NullIfEmpty(Arg(args, 4));

                return await SignAsync(origin, method, new LocText(() => Strings.Request_SignXml), LocText.Raw(Truncate(xml)), usage, ct,
                    key => Task.FromResult(XmlDsigSigner.Sign(key, xml, tbs, parent)));
            }

            case "signXmls":
            {
                var usage = ParseKeyType(Arg(args, 1));
                var xmls = (args.Count > 2 ? args[2] as JsonArray : null)?.Select(n => n!.GetValue<string>()).ToList()
                           ?? throw new ArgumentException("Пустой список XML"); // NoLoc: protocol message
                string? tbs = NullIfEmpty(Arg(args, 3));
                string? parent = NullIfEmpty(Arg(args, 4));

                return await SignManyAsync(origin, method, new LocText(() => Loc.Format(Strings.Request_SignXmls, xmls.Count)),
                    LocText.Raw(Truncate(string.Join("\n\n", xmls))), usage, ct,
                    key => Task.FromResult<JsonNode>(new JsonArray(xmls.Select(x => (JsonNode)XmlDsigSigner.Sign(key, x, tbs, parent)).ToArray())));
            }

            case "createCMSSignatureFromFile":
            case "createCAdESFromFile":
                return CommonError("Подписание файлов с диска не поддерживается. Используйте подписание данных в base64."); // NoLoc: protocol message

            default:
                return CommonError($"Неизвестный метод {method}"); // NoLoc: protocol message
        }
    }

    private async Task<string> SignAsync(string origin, string method, LocText description, LocText preview,
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

    private async Task<string> SignManyAsync(string origin, string method, LocText description, LocText preview,
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
        if (args["allowedStorages"] is JsonArray storages)
        {
            var valid = storages.Select(GetString).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (valid.Count > 0 && !valid.Any(s => string.Equals(s, "PKCS12", StringComparison.OrdinalIgnoreCase)))
            {
                return BasicsError("storage.unsupported", "Сайт требует аппаратный носитель (токен или ID-карту), он не поддерживается NativeNCA"); // NoLoc: protocol message
            }
        }

        string format = GetString(args["format"])?.ToLowerInvariant() ?? "cms";
        var signingParams = args["signingParams"] as JsonObject;
        var signerParams = args["signerParams"] as JsonObject;

        bool decode = Flag(signingParams?["decode"]);
        bool encapsulate = Flag(signingParams?["encapsulate"]);
        bool digested = Flag(signingParams?["digested"]);
        // NCALayer adds a TSA time-stamp when the site passes a tsaProfile (even an empty one).
        bool timestamp = signingParams?.ContainsKey("tsaProfile") == true && signingParams["tsaProfile"] != null;

        List<string> items = args["data"] switch
        {
            JsonArray arr => arr.Select(GetString).Where(s => s != null).Select(s => s!).ToList(),
            JsonNode n when GetString(n) is string s => new List<string> { s },
            _ => throw new ArgumentException("Нет данных для подписи") // NoLoc: protocol message
        };

        var usage = KeyUsageFilter.Any;
        if (signerParams?["extKeyUsageOids"] is JsonArray ekus)
        {
            var oids = ekus.Select(GetString).Where(s => s != null).ToHashSet();
            if (oids.Contains(KzOids.EkuClientAuth) && !oids.Contains(KzOids.EkuEmailProtection)) usage = KeyUsageFilter.Auth;
            else if (oids.Contains(KzOids.EkuEmailProtection) && !oids.Contains(KzOids.EkuClientAuth)) usage = KeyUsageFilter.Sign;
        }

        var preview = new LocText(() => Truncate(format == "xml"
            ? string.Join("\n\n", items)
            : digested
                ? Loc.Format(Strings.Request_HashPreview, string.Join(", ", items))
                : string.Join("\n\n", items.Select(i => DescribeBinary(decode ? Convert.FromBase64String(i) : Encoding.UTF8.GetBytes(i))))));

        var result = await PromptAsync(new SigningPromptRequest
        {
            Origin = origin,
            Method = "basics.sign",
            Description = new LocText(() => items.Count > 1 ? Loc.Format(Strings.Request_SignDocuments, items.Count)
                : format == "xml" ? Strings.Request_SignXml : Strings.Request_SignDocument),
            DataToSignPreview = preview,
            RequiredUsage = usage,
            RequiredIin = GetString(signerParams?["iin"]),
            RequiredBin = GetString(signerParams?["bin"])
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

        // NCALayer always answers with an array, even for a single string: eGov reads body.result[0].
        var payload = new JsonArray(signed.Select(s => (JsonNode)s).ToArray());
        return new JsonObject { ["status"] = true, ["body"] = new JsonObject { ["result"] = payload } }.ToJsonString();
    }

    private async Task<string> HandleBasicsGenerateCsrAsync(JsonObject args, string origin, CancellationToken ct)
    {
        bool hasPkcs12 = false;
        bool hasStorageRestriction = false;

        if (args["allowedStorages"] is JsonArray storages)
        {
            var valid = storages.Select(GetString).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (valid.Count > 0)
            {
                hasStorageRestriction = true;
                hasPkcs12 = valid.Any(s => string.Equals(s, "PKCS12", StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (args["allowedStorage"] != null)
        {
            string? asStr = GetString(args["allowedStorage"]);
            if (!string.IsNullOrWhiteSpace(asStr))
            {
                hasStorageRestriction = true;
                hasPkcs12 = string.Equals(asStr, "PKCS12", StringComparison.OrdinalIgnoreCase);
            }
        }
        else if (args["storageType"] != null)
        {
            string? stStr = GetString(args["storageType"]);
            if (!string.IsNullOrWhiteSpace(stStr))
            {
                hasStorageRestriction = true;
                hasPkcs12 = string.Equals(stStr, "PKCS12", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (hasStorageRestriction && !hasPkcs12)
        {
            return BasicsError("CSR_GENERATING_FAILURE", "NativeNCA поддерживает только файловое хранилище (PKCS#12)"); // NoLoc: protocol message
        }

        var dnNode = args["dn"] as JsonObject;
        var dn = new Dictionary<string, string>();
        if (dnNode != null)
        {
            foreach (var (k, v) in dnNode)
            {
                string? strVal = GetString(v);
                if (!string.IsNullOrEmpty(strVal)) dn[k] = strVal;
            }
        }

        if (dn.Count == 0)
        {
            return BasicsError("CSR_GENERATING_FAILURE", "Не указаны данные субъекта (DN)"); // NoLoc: protocol message
        }

        string? email = GetString(args["email"]);
        string? defaultPassword = GetString(args["defaultPassword"]);

        var sans = new List<string>();
        if (args["subjectAltNames"] is JsonArray sanArray)
        {
            foreach (var item in sanArray)
            {
                string? s = GetString(item);
                if (!string.IsNullOrWhiteSpace(s)) sans.Add(s);
            }
        }

        var paramsArray = args["params"] as JsonArray;
        var keyParams = new List<(string alg, int length)>();
        if (paramsArray != null && paramsArray.Count > 0)
        {
            foreach (var p in paramsArray)
            {
                if (p is JsonObject po)
                {
                    string alg = GetString(po["algorithm"]) ?? "GOST512";
                    int len = GetInt(po["length"]) ?? (alg.Contains("RSA", StringComparison.OrdinalIgnoreCase) ? 2048 : 512);
                    keyParams.Add((alg, len));
                }
            }
        }
        else if (args["algorithm"] != null)
        {
            string alg = GetString(args["algorithm"]) ?? "GOST512";
            int len = GetInt(args["keyLength"]) ?? (alg.Contains("RSA", StringComparison.OrdinalIgnoreCase) ? 2048 : 512);
            keyParams.Add((alg, len));
        }
        else
        {
            keyParams.Add(("GOST512", 512));
        }

        string? cn = dn.GetValueOrDefault("2.5.4.3");
        string? iin = dn.GetValueOrDefault("2.5.4.5");
        string? bin = dn.GetValueOrDefault("2.5.4.11");
        string? org = dn.GetValueOrDefault("2.5.4.10");

        string algDisplayName = keyParams[0].alg.Contains("RSA", StringComparison.OrdinalIgnoreCase)
            ? Strings.Crypto_AlgorithmRsa2048
            : Strings.Crypto_AlgorithmGost2015_512;

        var promptReq = new CsrPromptRequest
        {
            Origin = origin,
            Description = new LocText(() => Strings.Overlay_CsrTitle),
            DistinguishedName = dn,
            SubjectCommonName = cn,
            SubjectIin = iin,
            SubjectBin = bin,
            SubjectOrganization = org,
            Email = email,
            KeyAlgorithm = algDisplayName
        };

        CsrPromptResult promptResult;
        await _promptGate.WaitAsync(ct);
        try
        {
            promptResult = await _prompt.PromptForCsrAsync(promptReq, ct);
        }
        finally
        {
            _promptGate.Release();
        }

        if (!promptResult.Approved)
            return BasicsCancelled();

        string chosenPassword = !string.IsNullOrEmpty(promptResult.Password)
            ? promptResult.Password
            : (!string.IsNullOrEmpty(defaultPassword) ? defaultPassword : Guid.NewGuid().ToString("N"));

        var pems = new JsonArray();
        foreach (var (alg, len) in keyParams)
        {
            var generated = CsrGenerator.Generate(dn, email, sans, alg, len);
            _pendingCsrStore.Add(new PendingCsrItem
            {
                KeyId = generated.KeyId,
                Prefix = generated.Prefix,
                Kind = generated.Kind,
                PrivateKeyInfo = generated.PrivateKeyInfo,
                PublicKeyInfo = generated.PublicKeyInfo,
                Password = chosenPassword,
                BackupFolder = promptResult.BackupFolder,
                CustomName = cn
            });
            pems.Add(generated.CsrPem);
        }

        return new JsonObject
        {
            ["status"] = true,
            ["body"] = new JsonObject
            {
                ["pems"] = pems
            }
        }.ToJsonString();
    }

    private async Task<string> HandleBasicsImportCertificateAsync(JsonObject args, string origin, CancellationToken ct)
    {
        bool hasPkcs12 = false;
        bool hasStorageRestriction = false;

        if (args["allowedStorages"] is JsonArray storages)
        {
            var valid = storages.Select(GetString).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (valid.Count > 0)
            {
                hasStorageRestriction = true;
                hasPkcs12 = valid.Any(s => string.Equals(s, "PKCS12", StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (args["allowedStorage"] != null)
        {
            string? asStr = GetString(args["allowedStorage"]);
            if (!string.IsNullOrWhiteSpace(asStr))
            {
                hasStorageRestriction = true;
                hasPkcs12 = string.Equals(asStr, "PKCS12", StringComparison.OrdinalIgnoreCase);
            }
        }
        else if (args["storageType"] != null)
        {
            string? stStr = GetString(args["storageType"]);
            if (!string.IsNullOrWhiteSpace(stStr))
            {
                hasStorageRestriction = true;
                hasPkcs12 = string.Equals(stStr, "PKCS12", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (hasStorageRestriction && !hasPkcs12)
        {
            return BasicsError("CERTIFICATE_IMPORTING_FAILURE", "NativeNCA поддерживает только файловое хранилище (PKCS#12)"); // NoLoc: protocol message
        }

        var certNodes = args["certificates"] as JsonArray;
        if (certNodes == null || certNodes.Count == 0)
        {
            return BasicsError("CERTIFICATE_IMPORTING_FAILURE", "Пустой список сертификатов"); // NoLoc: protocol message
        }

        string? defaultPassword = GetString(args["defaultPassword"]);
        string? keystorePath = GetString(args["keystorePath"]);

        var parser = new Org.BouncyCastle.X509.X509CertificateParser();
        var allCerts = new List<Org.BouncyCastle.X509.X509Certificate>();
        foreach (var node in certNodes)
        {
            string text = GetString(node)?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(text)) continue;

            try
            {
                if (text.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    allCerts.Add(parser.ReadCertificate(Encoding.UTF8.GetBytes(text)));
                }
                else
                {
                    allCerts.Add(parser.ReadCertificate(Convert.FromBase64String(text)));
                }
            }
            catch
            {
                // Ignore unparseable certificates
            }
        }

        if (allCerts.Count == 0)
        {
            return BasicsError("CERTIFICATE_IMPORTING_FAILURE", "Не удалось разобрать сертификаты"); // NoLoc: protocol message
        }

        int importedCount = 0;
        string? lastImportedName = null;

        foreach (var cert in allCerts)
        {
            byte[] pubBytes = cert.CertificateStructure.SubjectPublicKeyInfo.PublicKey.GetBytes();
            byte[] keyIdBytes = System.Security.Cryptography.SHA1.HashData(pubBytes);
            string keyIdHex = Convert.ToHexString(keyIdBytes).ToLowerInvariant();

            var pending = _pendingCsrStore.Find(keyIdHex);
            if (pending == null) continue;

            string finalPassword = !string.IsNullOrEmpty(defaultPassword)
                ? defaultPassword
                : (!string.IsNullOrEmpty(pending.Password) ? pending.Password : "NativeNCA123");

            var contents = new Pkcs12Contents
            {
                PrivateKey = pending.PrivateKeyInfo,
                Certificate = cert,
                AllCertificates = allCerts
            };

            byte[] p12Bytes = Pkcs12Writer.Write(contents, finalPassword);

            try
            {
                _vault.AddKey(p12Bytes, finalPassword, customName: pending.CustomName, savePassword: true);
            }
            catch (InvalidOperationException)
            {
                // Already present in vault
            }

            string? targetFolder = !string.IsNullOrEmpty(pending.BackupFolder)
                ? pending.BackupFolder
                : (!string.IsNullOrEmpty(keystorePath) ? keystorePath : null);

            if (!string.IsNullOrEmpty(targetFolder))
            {
                try
                {
                    Directory.CreateDirectory(targetFolder);
                    string fileName = $"{pending.Prefix}_{pending.KeyId}.p12";
                    File.WriteAllBytes(Path.Combine(targetFolder, fileName), p12Bytes);
                }
                catch
                {
                    // Non-fatal if backup folder write fails
                }
            }

            _pendingCsrStore.Remove(pending.KeyId);
            lastImportedName = CertificateInfo.From(cert).CommonName;
            importedCount++;
        }

        if (importedCount == 0)
        {
            return BasicsError("CERTIFICATE_IMPORTING_FAILURE",
                "Хранилище ключей не содержит открытый ключ, который соответствует сертификату для установки"); // NoLoc: protocol message
        }

        if (lastImportedName != null)
        {
            await _prompt.ShowNotificationAsync(Strings.Status_CertificateImportedTitle,
                Loc.Format(Strings.Status_CertificateImported, lastImportedName));
        }

        return new JsonObject
        {
            ["status"] = true,
            ["body"] = new JsonObject
            {
                ["result"] = true
            }
        }.ToJsonString();
    }

    private static string HandleBasicsObtainAllStorages() =>
        new JsonObject
        {
            ["status"] = true,
            ["body"] = new JsonArray
            {
                new JsonObject { ["storage"] = "PKCS12" }
            }
        }.ToJsonString();

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
        return Loc.Plural(data.Length, Strings.Request_BinaryPreview_Plural, sha);
    }

    private static string? GetString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue val when val.TryGetValue<string>(out var s) => s,
        JsonValue val => val.ToString(),
        _ => node.ToString()
    };

    private static int? GetInt(JsonNode? node) => node switch
    {
        null => null,
        JsonValue val when val.TryGetValue<int>(out int i) => i,
        JsonValue val when int.TryParse(val.ToString(), out int i) => i,
        _ => null
    };

    private static void LogProtocol(string line)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeNCA");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "protocol.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}\n");
        }
        catch { }
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
