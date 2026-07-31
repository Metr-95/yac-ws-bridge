using System.Security.Cryptography;
using System.Text;

namespace BridgeToFreedom.Services;

/// <summary>
/// The object-storage "dead drop" protocol: two sides that can never connect
/// to each other directly exchange a byte stream by leaving numbered chunks
/// for one another in a shared bucket. Ported from deaddrop's
/// internal/tunnel/session.go — see that project's README for the full
/// rationale (this Helper is the client half; a deaddrop-server on the AWS
/// box plays the other role).
///
///   sessions/&lt;id&gt;/client.state   "open" | "closed" — written only by the client
///   sessions/&lt;id&gt;/server.state   "open" | "closed" — written only by the server
///   sessions/&lt;id&gt;/c2s/&lt;seq&gt;      chunk, client -&gt; server
///   sessions/&lt;id&gt;/s2c/&lt;seq&gt;      chunk, server -&gt; client
///
/// Each side only ever writes its own state key and chunk directory, and
/// only ever reads the other's — no read-modify-write race on shared state,
/// which matters because S3-compatible stores generally don't offer
/// compare-and-swap.
/// </summary>
public static class DeaddropSession
{
    public const string StateOpen = "open";
    public const string StateClosed = "closed";
    public const string RoleClient = "client";
    public const string RoleServer = "server";

    public static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static string OtherRole(string role) => role == RoleClient ? RoleServer : RoleClient;

    public static string StateKey(string id, string role) => $"sessions/{id}/{role}.state";

    public static string ChunkDir(string id, string dir) => $"sessions/{id}/{dir}/";

    public static string ChunkKey(string id, string dir, ulong seq) => $"{ChunkDir(id, dir)}{seq:D12}";

    public static Task PutStateAsync(S3Store store, string id, string role, string state, CancellationToken ct = default) =>
        store.PutAsync(StateKey(id, role), Encoding.UTF8.GetBytes(state), ct);

    /// <summary>Returns null if that side hasn't written anything yet.</summary>
    public static async Task<string?> GetStateAsync(S3Store store, string id, string role, CancellationToken ct = default)
    {
        var data = await store.GetAsync(StateKey(id, role), ct).ConfigureAwait(false);
        return data == null ? null : Encoding.UTF8.GetString(data);
    }

    /// <summary>Session IDs currently visible in the bucket (every "directory" under sessions/).</summary>
    public static Task<List<string>> ListSessionsAsync(S3Store store, CancellationToken ct = default) =>
        store.ListDirsAsync("sessions", ct);
}
