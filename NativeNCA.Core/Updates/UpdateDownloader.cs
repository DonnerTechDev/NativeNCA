using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace NativeNCA.Core.Updates;

public readonly record struct DownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;
}

/// <summary>The update was downloaded but must not be installed (bad signature, wrong version, ...).</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>
/// Downloads a release zip and its signature from GitHub, verifies the signature against the embedded
/// release key for exactly the expected version and architecture, and extracts <c>NativeNCA.exe</c> from
/// it. Nothing unverified ever reaches the install folder.
/// </summary>
public sealed class UpdateDownloader
{
    private const long MaxZipBytes = 512L * 1024 * 1024;
    private const int MaxSignatureBytes = 4096;
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly string _downloadDir;
    private readonly byte[] _publicKey;

    public UpdateDownloader(HttpClient http, string? downloadDir = null, byte[]? publicKeySpki = null)
    {
        _http = http;
        _downloadDir = downloadDir ?? Path.Combine(Path.GetTempPath(), "NativeNCA-update");
        _publicKey = publicKeySpki ?? Convert.FromBase64String(ReleaseSignature.PublicKeyBase64);
    }

    /// <summary>
    /// Downloads, verifies and writes the new exe to <paramref name="targetExePath"/>
    /// (normally <see cref="AppInstallation.NewExePath"/>).
    /// </summary>
    /// <exception cref="UpdateRejectedException">The files are not a valid, newer, signed NativeNCA release.</exception>
    public async Task DownloadAndStageAsync(ReleasePackage package, AppVersion currentVersion, string targetExePath,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (package.Release.Version <= currentVersion)
            throw new UpdateRejectedException($"Версия {package.Release.Version} не новее установленной {currentVersion}");
        if (package.Zip.Size > MaxZipBytes)
            throw new UpdateRejectedException("Файл обновления слишком большой");

        Directory.CreateDirectory(_downloadDir);
        string zipPath = Path.Combine(_downloadDir, package.Zip.Name);

        try
        {
            string signature = System.Text.Encoding.ASCII.GetString(
                await DownloadAsync(package.Signature.DownloadUrl, MaxSignatureBytes, cancellationToken));

            byte[] hash = await DownloadToFileAsync(package.Zip, zipPath, progress, cancellationToken);

            VerifyAndExtract(zipPath, hash, signature, package.Release.Version, package.Architecture, targetExePath, _publicKey);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* temp folder */ }
        }
    }

    /// <summary>
    /// Checks the signature for <paramref name="version"/> and <paramref name="architecture"/>, extracts the exe
    /// and makes sure its own version matches. Separate from the download so tests can run it offline.
    /// </summary>
    internal static void VerifyAndExtract(string zipPath, byte[] zipSha256, string signature, AppVersion version,
        string architecture, string targetExePath, byte[] publicKeySpki)
    {
        if (!ReleaseSignature.Verify(signature, version, architecture, zipSha256, publicKeySpki))
            throw new UpdateRejectedException("Подпись обновления недействительна. Обновление не установлено");

        string temp = targetExePath + ".download";
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.SingleOrDefault(e => string.Equals(e.FullName, AppInstallation.ExeName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new UpdateRejectedException("В архиве обновления нет NativeNCA.exe");
                entry.ExtractToFile(temp, overwrite: true);
            }

            var exeVersion = AppInstallation.ReadExeVersion(temp);
            if (exeVersion == null || !exeVersion.Equals(version))
                throw new UpdateRejectedException($"Версия файла ({exeVersion?.ToString() ?? "неизвестна"}) не совпадает с версией обновления {version}");

            File.Move(temp, targetExePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    public static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    /// <summary>GET with redirects followed by hand, so every hop is checked to be HTTPS on a GitHub host.</summary>
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!GitHubReleaseClient.IsTrustedDownloadUri(uri))
                throw new UpdateRejectedException($"Обновление загружается только с GitHub по HTTPS, а не с {uri.Host}");

            HttpResponseMessage response;
            using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri),
                        HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new HttpRequestException("GitHub не отвечает");
                }
            }

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) break;
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
        throw new HttpRequestException("Слишком много перенаправлений при загрузке обновления");
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes) throw new UpdateRejectedException("Файл подписи обновления слишком большой");
        }
        return buffer.ToArray();
    }

    /// <summary>Streams the zip to disk, hashing as it goes. Stalls longer than a minute abort the download.</summary>
    private async Task<byte[]> DownloadToFileAsync(ReleaseAsset asset, string path, IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await GetAsync(asset.DownloadUrl, cancellationToken);
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > MaxZipBytes) throw new UpdateRejectedException("Файл обновления слишком большой");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        var lastReport = DateTime.MinValue;
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(TimeSpan.FromMinutes(1));
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("Загрузка обновления прервалась: нет ответа от сервера");
            }
            if (read == 0) break;

            received += read;
            if (received > MaxZipBytes) throw new UpdateRejectedException("Файл обновления слишком большой");
            sha.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(100))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new DownloadProgress(received, total));
            }
        }
        progress?.Report(new DownloadProgress(received, Math.Max(total, received)));

        if (asset.Size > 0 && received != asset.Size)
            throw new UpdateRejectedException("Файл обновления загружен не полностью");

        return sha.GetHashAndReset();
    }
}
