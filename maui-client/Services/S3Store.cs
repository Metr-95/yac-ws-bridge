using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace BridgeToFreedom.Services;

/// <summary>Non-2xx (and non-404-on-delete) response from the bucket.</summary>
public sealed class S3Exception : Exception
{
    public S3Exception(string message) : base(message) { }
}

/// <summary>
/// Thin async wrapper over an S3-compatible bucket (PUT/GET/DELETE/List),
/// signed with hand-rolled AWS SigV4 (<see cref="Sigv4Signer"/>) — no AWS
/// SDK dependency, matching the philosophy (and the wire behaviour) of the
/// Go deaddrop-server/-client this Helper talks to. Ported from deaddrop's
/// internal/store package.
/// </summary>
public sealed class S3Store
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _region;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly string _accessKey;
    private readonly string _secretKey;

    public S3Store(string endpoint, string region, string bucket, string prefix, string accessKey, string secretKey)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _endpoint = endpoint.TrimEnd('/');
        _region = region;
        _bucket = bucket;
        _prefix = prefix.Trim('/');
        _accessKey = accessKey;
        _secretKey = secretKey;
    }

    private string Key(string rel) => $"{_prefix}/{rel.TrimStart('/')}";

    private static string EscapeSegments(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            segments[i] = Uri.EscapeDataString(segments[i]);
        return string.Join("/", segments);
    }

    private string ObjectUrl(string fullKey) =>
        $"{_endpoint}/{Uri.EscapeDataString(_bucket)}/{EscapeSegments(fullKey)}";

    private async Task<HttpResponseMessage> DoAsync(HttpMethod method, string url, byte[]? body, CancellationToken ct)
    {
        var payload = body ?? Array.Empty<byte>();
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
        if (body != null)
            request.Content = new ByteArrayContent(payload);

        Sigv4Signer.Sign(request, _region, _accessKey, _secretKey, payload);

        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    public async Task PutAsync(string relKey, byte[] data, CancellationToken ct = default)
    {
        using var resp = await DoAsync(HttpMethod.Put, ObjectUrl(Key(relKey)), data, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new S3Exception($"put {relKey}: {(int)resp.StatusCode} {body}");
        }
    }

    /// <summary>Returns null if the key does not exist (404) — mirrors Go's ErrNotFound.</summary>
    public async Task<byte[]?> GetAsync(string relKey, CancellationToken ct = default)
    {
        using var resp = await DoAsync(HttpMethod.Get, ObjectUrl(Key(relKey)), null, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new S3Exception($"get {relKey}: {(int)resp.StatusCode} {body}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deleting a missing key is not an error — S3 DELETE is idempotent.</summary>
    public async Task DeleteAsync(string relKey, CancellationToken ct = default)
    {
        using var resp = await DoAsync(HttpMethod.Delete, ObjectUrl(Key(relKey)), null, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new S3Exception($"delete {relKey}: {(int)resp.StatusCode} {body}");
        }
    }

    private async Task<(List<string> Keys, List<string> Prefixes, bool Truncated, string NextToken)> ListObjectsV2Async(
        string prefix, string delimiter, string continuationToken, CancellationToken ct)
    {
        var qs = new StringBuilder("list-type=2");
        qs.Append("&prefix=").Append(Uri.EscapeDataString(prefix));
        if (!string.IsNullOrEmpty(delimiter))
            qs.Append("&delimiter=").Append(Uri.EscapeDataString(delimiter));
        if (!string.IsNullOrEmpty(continuationToken))
            qs.Append("&continuation-token=").Append(Uri.EscapeDataString(continuationToken));

        var url = $"{_endpoint}/{Uri.EscapeDataString(_bucket)}/?{qs}";
        using var resp = await DoAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new S3Exception($"list {prefix}: {(int)resp.StatusCode} {errBody}");
        }

        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var keys = doc.Descendants().Where(e => e.Name.LocalName == "Contents")
            .Select(e => e.Elements().First(c => c.Name.LocalName == "Key").Value).ToList();
        var prefixes = doc.Descendants().Where(e => e.Name.LocalName == "CommonPrefixes")
            .Select(e => e.Elements().First(c => c.Name.LocalName == "Prefix").Value).ToList();
        var truncatedEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "IsTruncated");
        var truncated = truncatedEl != null && string.Equals(truncatedEl.Value, "true", StringComparison.OrdinalIgnoreCase);
        var nextTokenEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken");
        var nextToken = nextTokenEl?.Value ?? "";

        return (keys, prefixes, truncated, nextToken);
    }

    /// <summary>Every object key under keyPrefix, with the store's own bucket prefix stripped back off.</summary>
    public async Task<List<string>> ListKeysAsync(string keyPrefix, CancellationToken ct = default)
    {
        var full = Key(keyPrefix);
        var outKeys = new List<string>();
        var token = "";
        while (true)
        {
            var (keys, _, truncated, nextToken) = await ListObjectsV2Async(full, "", token, ct).ConfigureAwait(false);
            var stripPrefix = _prefix + "/";
            foreach (var k in keys)
                outKeys.Add(k.StartsWith(stripPrefix, StringComparison.Ordinal) ? k[stripPrefix.Length..] : k);
            if (!truncated) break;
            token = nextToken;
        }
        return outKeys;
    }

    /// <summary>Immediate "subdirectories" under keyPrefix (delimiter="/") — used to enumerate session IDs.</summary>
    public async Task<List<string>> ListDirsAsync(string keyPrefix, CancellationToken ct = default)
    {
        var full = Key(keyPrefix);
        if (!full.EndsWith('/')) full += "/";
        var outDirs = new List<string>();
        var token = "";
        while (true)
        {
            var (_, prefixes, truncated, nextToken) = await ListObjectsV2Async(full, "/", token, ct).ConfigureAwait(false);
            foreach (var p in prefixes)
            {
                var trimmed = p.StartsWith(full, StringComparison.Ordinal) ? p[full.Length..] : p;
                trimmed = trimmed.TrimEnd('/');
                if (trimmed.Length > 0) outDirs.Add(trimmed);
            }
            if (!truncated) break;
            token = nextToken;
        }
        return outDirs;
    }
}
