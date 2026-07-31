using System.Net.Sockets;

namespace BridgeToFreedom.Services;

/// <summary>
/// Relays bytes between a local TCP connection and one session's dead drop.
/// Ported from deaddrop's internal/tunnel/pump.go. This Helper app always
/// instantiates it in the client role (WriteDir="c2s", ReadDir="s2c") — a
/// deaddrop-server on the far side (the AWS box) dials the real target and
/// plays the server role.
/// </summary>
public sealed class Pump
{
    // Bounds how much of a single local read is uploaded as one object.
    // Well under typical S3-compatible per-request limits; the real ceiling
    // on throughput is the polling interval, not this.
    private const int MaxChunkSize = 48 * 1024;

    public required S3Store Store { get; init; }
    public required string SessionId { get; init; }
    public required string Role { get; init; }
    public required string WriteDir { get; init; }
    public required string ReadDir { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan SessionIdle { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Relays until <paramref name="conn"/> closes, the peer closes and
    /// drains, or the session goes idle past <see cref="SessionIdle"/>.
    /// Always leaves this side's state key as "closed", and closes
    /// <paramref name="conn"/>, on the way out — matching the Go Pump.Run.
    /// </summary>
    public async Task RunAsync(NetworkStream conn, CancellationToken ct)
    {
        await DeaddropSession.PutStateAsync(Store, SessionId, Role, DeaddropSession.StateOpen, ct).ConfigureAwait(false);

        var uploadTask = UploadAsync(conn, ct);
        var downloadTask = DownloadAsync(conn, ct);

        Exception? upErr = null, downErr = null;
        try { await uploadTask.ConfigureAwait(false); } catch (Exception ex) { upErr = ex; }
        try { await downloadTask.ConfigureAwait(false); } catch (Exception ex) { downErr = ex; }

        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await DeaddropSession.PutStateAsync(Store, SessionId, Role, DeaddropSession.StateClosed, cleanupCts.Token).ConfigureAwait(false);
        }
        catch { /* best effort */ }

        try { conn.Close(); } catch { }

        if (upErr != null) throw upErr;
        if (downErr != null) throw downErr;
    }

    /// <summary>Reads from conn, writes sequential chunks to WriteDir. A clean EOF just means the local side is done sending.</summary>
    private async Task UploadAsync(NetworkStream conn, CancellationToken ct)
    {
        var buf = new byte[MaxChunkSize];
        ulong seq = 0;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await conn.ReadAsync(buf, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }
            if (n == 0) return; // clean EOF

            var chunk = new byte[n];
            Buffer.BlockCopy(buf, 0, chunk, 0, n);
            await Store.PutAsync(DeaddropSession.ChunkKey(SessionId, WriteDir, seq), chunk, ct).ConfigureAwait(false);
            seq++;
        }
    }

    /// <summary>
    /// Polls ReadDir for new chunks in order, writes them to conn, deletes
    /// each once delivered. Stops once the peer has marked its side closed
    /// with nothing left to drain, or after SessionIdle with no peer
    /// activity at all (peer crashed / network died without writing "closed").
    /// </summary>
    private async Task DownloadAsync(NetworkStream conn, CancellationToken ct)
    {
        var peerRole = DeaddropSession.OtherRole(Role);
        ulong nextSeq = 0;
        var lastActivity = DateTime.UtcNow;

        while (true)
        {
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            List<string> keys;
            try
            {
                keys = await Store.ListKeysAsync(DeaddropSession.ChunkDir(SessionId, ReadDir), ct).ConfigureAwait(false);
            }
            catch
            {
                if (DateTime.UtcNow - lastActivity > SessionIdle) return;
                continue;
            }

            var pending = ParseSeqs(keys, ReadDir, SessionId);
            pending.Sort();

            var drainedAny = false;
            foreach (var seq in pending)
            {
                if (seq < nextSeq) continue;
                byte[]? data;
                try
                {
                    data = await Store.GetAsync(DeaddropSession.ChunkKey(SessionId, ReadDir, seq), ct).ConfigureAwait(false);
                }
                catch
                {
                    break; // transient — retry next tick
                }
                if (data == null) break; // not fully propagated yet

                await conn.WriteAsync(data, ct).ConfigureAwait(false);
                try { await Store.DeleteAsync(DeaddropSession.ChunkKey(SessionId, ReadDir, seq), ct).ConfigureAwait(false); }
                catch { /* best effort */ }
                nextSeq = seq + 1;
                drainedAny = true;
            }

            if (drainedAny)
            {
                lastActivity = DateTime.UtcNow;
                continue;
            }

            string? peerState = null;
            try { peerState = await DeaddropSession.GetStateAsync(Store, SessionId, peerRole, ct).ConfigureAwait(false); }
            catch { /* treat as not-closed-yet */ }
            if (peerState == DeaddropSession.StateClosed) return;
            if (DateTime.UtcNow - lastActivity > SessionIdle) return;
        }
    }

    private static List<ulong> ParseSeqs(List<string> keys, string dir, string id)
    {
        var prefix = DeaddropSession.ChunkDir(id, dir);
        var outSeqs = new List<ulong>(keys.Count);
        foreach (var k in keys)
        {
            var s = k.StartsWith(prefix, StringComparison.Ordinal) ? k[prefix.Length..] : k;
            if (ulong.TryParse(s, out var n)) outSeqs.Add(n);
        }
        return outSeqs;
    }

    /// <summary>Best-effort deletes every object belonging to a session (chunks + both state keys).</summary>
    public static async Task CleanupAsync(S3Store store, string id, CancellationToken ct)
    {
        foreach (var dir in new[] { "c2s", "s2c" })
        {
            List<string> keys;
            try { keys = await store.ListKeysAsync(DeaddropSession.ChunkDir(id, dir), ct).ConfigureAwait(false); }
            catch { continue; }
            foreach (var k in keys)
            {
                try { await store.DeleteAsync(k, ct).ConfigureAwait(false); } catch { /* best effort */ }
            }
        }
        try { await store.DeleteAsync(DeaddropSession.StateKey(id, DeaddropSession.RoleClient), ct).ConfigureAwait(false); } catch { }
        try { await store.DeleteAsync(DeaddropSession.StateKey(id, DeaddropSession.RoleServer), ct).ConfigureAwait(false); } catch { }
    }
}
