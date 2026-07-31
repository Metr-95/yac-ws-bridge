using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BridgeToFreedom.Services;

/// <summary>
/// AWS Signature Version 4 request signing, implemented from scratch — no
/// AWS SDK dependency, matching the Go reference implementation in the
/// deaddrop project (internal/store/sigv4.go), which was checked against
/// AWS's own published worked example. Every S3-compatible object storage
/// (Yandex Object Storage included) speaks this.
/// </summary>
public static class Sigv4Signer
{
    /// <summary>
    /// Signs <paramref name="request"/> in place. <paramref name="payload"/>
    /// must be the exact bytes that will be sent as the request body (empty
    /// array for bodyless requests) — its SHA-256 goes in the
    /// X-Amz-Content-Sha256 header and into the signature, so it has to be
    /// computed before the request is sent, not streamed.
    /// </summary>
    public static void Sign(HttpRequestMessage request, string region, string accessKey, string secretKey, byte[] payload)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("request has no URI");
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = HexSha256(payload);
        var hostHeader = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        request.Headers.Remove("X-Amz-Date");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.Remove("X-Amz-Content-Sha256");
        request.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);
        request.Headers.Host = hostHeader;

        var canonicalUri = CanonicalizePath(uri.AbsolutePath);
        var canonicalQuery = CanonicalizeQuery(uri.Query);

        // Only Host and the X-Amz-* headers we set ourselves are signed —
        // sufficient for S3 and keeps this simple (mirrors the Go signer).
        var include = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = hostHeader,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        var signedHeaderNames = string.Join(";", include.Keys);
        var canonicalHeaders = string.Concat(include.Select(kv => $"{kv.Key}:{kv.Value.Trim()}\n"));

        var canonicalRequest = string.Join("\n",
            request.Method.Method,
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            signedHeaderNames,
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            HexSha256(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(secretKey, dateStamp, region, "s3");
        var signature = Convert.ToHexStringLower(HmacSha256(signingKey, stringToSign));

        var authHeader = $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, " +
                          $"SignedHeaders={signedHeaderNames}, Signature={signature}";
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    private static string CanonicalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = Uri.EscapeDataString(segments[i]);
        return string.Join("/", segments);
    }

    // Decodes then re-encodes every query parameter so the canonical form is
    // correct regardless of how the caller originally percent-encoded the
    // URL it built (see S3Store.ObjectUrl / ListObjectsV2Async).
    private static string CanonicalizeQuery(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery)) return "";
        var q = rawQuery.TrimStart('?');
        var pairs = new List<(string Key, string Value)>();
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var rawKey = idx >= 0 ? part[..idx] : part;
            var rawVal = idx >= 0 ? part[(idx + 1)..] : "";
            pairs.Add((Uri.UnescapeDataString(rawKey), Uri.UnescapeDataString(rawVal)));
        }
        pairs.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.Key, b.Key);
            return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
        });
        return string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    private static string HexSha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] HmacSha256(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }
}
