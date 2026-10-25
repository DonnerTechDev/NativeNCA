using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NativeNCA.Core.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>A stable GitHub release of NativeNCA.</summary>
public sealed record ReleaseInfo(AppVersion Version, string TagName, Uri HtmlUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>The zip and its signature for this machine's architecture.</summary>
public sealed record ReleasePackage(ReleaseInfo Release, string Architecture, ReleaseAsset Zip, ReleaseAsset Signature);

/// <summary>
/// Reads <c>releases/latest</c> of the NativeNCA repository (GitHub excludes drafts and pre-releases there).
/// Conditional requests with the last ETag do not count against the anonymous rate limit. Network
/// problems and rate limits are not errors for the caller: the check just finds nothing this time.
/// </summary>
public sealed class GitHubReleaseClient
{
    public const string Repository = "northbound-oss/NativeNCA";
    public static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPageUrl = new($"https://github.com/{Repository}/releases");

    private readonly HttpClient _http;
    private string? _etag;
    private ReleaseInfo? _cached;

    public GitHubReleaseClient(HttpClient http) => _http = http;

    /// <summary>When the API last refused because of the rate limit, the time it resets.</summary>
    public DateTimeOffset? RateLimitedUntil { get; private set; }

    /// <summary>Latest stable release, or null if there is none or GitHub cannot be reached.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        if (RateLimitedUntil is { } until && until > DateTimeOffset.UtcNow) return _cached;

        Uri url = LatestReleaseUrl;
        for (int hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return null; // offline
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null; // timeout
            }

            using (response)
            {
                // A renamed or transferred repository answers with a redirect to /repositories/<id>/...
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location == null || hop >= MaxApiRedirects) return _cached;
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                    if (!IsApiUri(url)) return _cached;
                    continue;
                }

                return await ReadResponseAsync(response, cancellationToken);
            }
        }
    }

    private const int MaxApiRedirects = 3;

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsApiUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.Equals(uri.IdnHost, "api.github.com", StringComparison.OrdinalIgnoreCase);

    private async Task<ReleaseInfo?> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotModified) return _cached;

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            RateLimitedUntil = ReadRateLimitReset(response) ?? DateTimeOffset.UtcNow.AddHours(1);
            return _cached;
        }

        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        if (!response.IsSuccessStatusCode) return _cached;

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = ParseRelease(json);
        _cached = release;
        _etag = response.Headers.ETag?.ToString();
        RateLimitedUntil = null;
        return release;
    }

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return DateTimeOffset.UtcNow + delta;
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), out long epoch))
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        return null;
    }

    /// <summary>Parses a GitHub release JSON. Drafts, pre-releases and tags that are not versions give null.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!AppVersion.TryParse(tag, out var version) || version!.IsPreRelease) return null;

        string? html = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        if (!Uri.TryCreate(html, UriKind.Absolute, out var htmlUrl) || !IsTrustedDownloadUri(htmlUrl))
            htmlUrl = ReleasesPageUrl;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long l) ? l : 0;
                if (name != null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    assets.Add(new ReleaseAsset(name, uri, size));
            }
        }

        return new ReleaseInfo(version, tag!, htmlUrl, assets);
    }

    /// <summary><c>win-x64</c> or <c>win-arm64</c>; null where NativeNCA is not published.</summary>
    public static string? ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => null
    };

    public static string? CurrentArchitecture => ArchitectureName(RuntimeInformation.ProcessArchitecture);

    /// <summary>
    /// The zip named exactly as CI names it (<c>NativeNCA-0.2.0-win-x64.zip</c>) and its <c>.sig</c>;
    /// null if either is missing.
    /// </summary>
    public static ReleasePackage? SelectPackage(ReleaseInfo release, string? architecture)
    {
        if (architecture == null) return null;
        string zipName = $"NativeNCA-{release.Version}-{architecture}.zip";
        var zip = release.Assets.FirstOrDefault(a => string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase));
        var sig = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, zipName + ReleaseSignature.SignatureExtension, StringComparison.OrdinalIgnoreCase));
        if (zip == null || sig == null || !IsTrustedDownloadUri(zip.DownloadUrl) || !IsTrustedDownloadUri(sig.DownloadUrl))
            return null;
        return new ReleasePackage(release, architecture, zip, sig);
    }

    private static readonly string[] TrustedHosts =
    {
        "github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    /// <summary>Release files are fetched only over HTTPS from GitHub (downloads redirect to its storage hosts).</summary>
    public static bool IsTrustedDownloadUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        TrustedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// HttpClient for GitHub: User-Agent and no automatic redirects (each hop is checked). Downloads pass
    /// an infinite <paramref name="timeout"/> and time out on stalls instead.
    /// </summary>
    public static HttpClient CreateHttpClient(AppVersion currentVersion, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NativeNCA", currentVersion.ToString()));
        return http;
    }
}
