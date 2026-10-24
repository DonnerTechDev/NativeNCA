using System.Net.Http.Headers;
using System.Security.Cryptography;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Tsp;
using NativeNCA.Core.Localization;

namespace NativeNCA.Core.Crypto;

/// <summary>Source of RFC 3161 time-stamp tokens for CAdES-T signatures.</summary>
public interface ITimestampProvider
{
    /// <summary>Returns a TimeStampToken (DER ContentInfo) over SHA-256 of the signature value.</summary>
    Task<byte[]> GetTokenAsync(byte[] signatureValue, CancellationToken ct = default);
}

public class TimestampException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// НУЦ РК time-stamping authority. The request format matches what NCALayer sends (verified against
/// the token embedded in NCALayer's own GOST-signed manifest): SHA-256 imprint of the signature value,
/// policy 1.2.398.3.3.2.6.2, certReq=true.
/// </summary>
public sealed class NcaTimestampService : ITimestampProvider
{
    public const string DefaultUrl = "http://tsp.pki.gov.kz:80";
    public const string Policy = "1.2.398.3.3.2.6.2";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _url;

    public NcaTimestampService(string url = DefaultUrl) => _url = url;

    public async Task<byte[]> GetTokenAsync(byte[] signatureValue, CancellationToken ct = default)
    {
        var generator = new TimeStampRequestGenerator();
        generator.SetReqPolicy(new Org.BouncyCastle.Asn1.DerObjectIdentifier(Policy));
        generator.SetCertReq(true);
        var nonce = new BigInteger(1, RandomNumberGenerator.GetBytes(8));
        var request = generator.Generate(TspAlgorithms.Sha256, SHA256.HashData(signatureValue), nonce);

        byte[] body;
        try
        {
            using var content = new ByteArrayContent(request.GetEncoded());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            using var response = await Http.PostAsync(_url, content, ct);
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TimestampException(Strings.Tsa_Unreachable, ex);
        }

        try
        {
            var tsr = new TimeStampResponse(body);
            tsr.Validate(request);
            return tsr.TimeStampToken?.GetEncoded()
                   ?? throw new TimestampException(Loc.Format(Strings.Tsa_Rejected, tsr.GetStatusString()));
        }
        catch (TspException ex)
        {
            throw new TimestampException(Strings.Tsa_BadResponse, ex);
        }
    }
}
