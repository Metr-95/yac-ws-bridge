using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BridgeToFreedom.Services;

/// <summary>
/// Outcome of the one-shot bucket-connectivity self-test shown in the UI as
/// a status indicator. Fired automatically once the listener is up.
/// </summary>
public enum ProbeStatus
{
    Idle,
    Testing,
    Ok,
    Failed,
}

/// <summary>
/// Core tunnel service: a local TCP listener where every accepted
/// connection becomes a new deaddrop session, relayed through an
/// S3-compatible bucket to whatever a deaddrop-server on the far side (the
/// AWS box) is configured to dial. See the deaddrop project's README for
/// the full store-and-forward protocol this implements — this class is a
/// C#/MAUI port of deaddrop's cmd/client, using the same internal/store and
/// internal/tunnel logic (<see cref="S3Store"/>, <see cref="Pump"/>,
/// <see cref="DeaddropSession"/>).
///
/// This Helper always plays the client role: point Shadowrocket (or any
/// SOCKS/TCP-aware app) at ListenAddress:ListenPort instead of at the real
/// target directly.
/// </summary>
public sealed class TunnelService : IDisposable
{
    public event Action<string>? OnLog;
    public event Action? OnStopped;

    /// <summary>
    /// Fires when the bucket-connectivity probe status changes. Always
    /// invoked on a thread-pool thread — marshal to the UI yourself.
    /// </summary>
    public event Action<ProbeStatus, string>? OnProbeStatusChanged;

    private CancellationTokenSource? _cts;
    // Retains the currently-running (or last-run) generation task so
    // lifecycle transitions can await its full teardown. Guarded by _lifecycleLock.
    private Task? _runTask;
    // Serializes Start/Stop transitions so a new generation is never set up
    // while a previous one is still tearing down shared state.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private volatile bool _stopping;
    private TcpListener? _listener;
    private S3Store? _store;

    // Config
    public string Endpoint { get; set; } = "https://storage.yandexcloud.net";
    public string Region { get; set; } = "ru-central1";
    public string Bucket { get; set; } = "";
    public string Prefix { get; set; } = "deaddrop";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string ListenAddress { get; set; } = "127.123.45.67";
    public int ListenPort { get; set; } = 1080;
    public int PollIntervalMs { get; set; } = 750;
    public int SessionIdleSec { get; set; } = 90;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public async Task StartAsync()
    {
        Task run;
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = _runTask;
            if (previous != null && !previous.IsCompleted
                && _cts != null && !_cts.IsCancellationRequested)
            {
                Log("StartAsync called but already running, ignoring.");
                return;
            }

            // If a previous generation is still shutting down, wait for it to
            // fully clean up BEFORE starting a new one — its cleanup touches
            // shared state (listener, store, events).
            if (previous != null)
            {
                try { await previous.ConfigureAwait(false); } catch { }
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _stopping = false;
            EmitProbeStatus(ProbeStatus.Idle, "");

            run = RunGenerationAsync(cts);
            _runTask = run;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        // Await OUTSIDE the lock so Stop/StopAsync can cancel while it runs.
        await run.ConfigureAwait(false);
    }

    // Owns one tunnel generation: the listener plus the teardown of every
    // resource it created.
    private async Task RunGenerationAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        Log("Starting tunnel service...");
        try
        {
            if (string.IsNullOrWhiteSpace(Bucket))
                throw new InvalidOperationException("bucket is required");
            if (string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey))
                throw new InvalidOperationException("access key id / secret access key are required");

            _store = new S3Store(Endpoint, Region, Bucket, Prefix, AccessKeyId, SecretAccessKey);

            _listener = new TcpListener(IPAddress.Parse(ListenAddress), ListenPort);
            _listener.Start();
            Log($"Listening on {ListenAddress}:{ListenPort}, relaying via bucket {Bucket} (prefix {Prefix})");

            _ = Task.Run(async () =>
            {
                try { await RunProbeAsync(ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Log($"Probe crashed: {ex.GetType().Name}: {ex.Message}");
                    EmitProbeStatus(ProbeStatus.Failed, $"crashed: {ex.GetType().Name}");
                }
            }, ct);

            await AcceptLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Service error: {ex.Message}"); }

        StopListener();
        Log("Tunnel service stopped.");
        OnStopped?.Invoke();
    }

    /// <summary>Requests cancellation without waiting for full teardown. Prefer StopAsync when you need it fully torn down first.</summary>
    public void Stop()
    {
        _stopping = true;
        Log("Stop requested.");
        try { _cts?.Cancel(); } catch { }
    }

    /// <summary>Cancels the running generation and awaits its full teardown.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        Log("Stop requested.");
        var cts = _cts;
        var run = _runTask;
        try { cts?.Cancel(); } catch { }
        if (run != null)
        {
            try { await run.ConfigureAwait(false); } catch { }
        }
    }

    private void StopListener()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Log($"Accept error: {ex.Message}");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { Log("Listener stopped."); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = "?";
        try { remote = client.Client.RemoteEndPoint?.ToString() ?? "?"; } catch { }

        var store = _store;
        if (store == null)
        {
            try { client.Dispose(); } catch { }
            return;
        }

        var id = DeaddropSession.NewSessionId();
        Log($"New connection remote={remote} session={Shorten(id)}");

        var pump = new Pump
        {
            Store = store,
            SessionId = id,
            Role = DeaddropSession.RoleClient,
            WriteDir = "c2s",
            ReadDir = "s2c",
            PollInterval = TimeSpan.FromMilliseconds(PollIntervalMs),
            SessionIdle = TimeSpan.FromSeconds(SessionIdleSec),
        };

        try
        {
            var stream = client.GetStream();
            await pump.RunAsync(stream, ct).ConfigureAwait(false);
            Log($"session {Shorten(id)}: closed");
        }
        catch (Exception ex)
        {
            Log($"session {Shorten(id)}: relay ended: {ex.Message}");
        }
        finally
        {
            try { client.Dispose(); } catch { }
            try
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(PollIntervalMs, 250) * 4));
                await Pump.CleanupAsync(store, id, cleanupCts.Token).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    // --- Connectivity probe ---
    // The whole reason this app exists is "can we actually reach the
    // bucket" — so unlike a live-peer handshake, the probe here just
    // round-trips a small object through the bucket itself. That's the one
    // thing worth checking automatically before the user routes real
    // traffic through this.

    private void EmitProbeStatus(ProbeStatus status, string detail)
    {
        try { OnProbeStatusChanged?.Invoke(status, detail); }
        catch { /* never let UI exceptions kill the tunnel */ }
    }

    private async Task RunProbeAsync(CancellationToken ct)
    {
        EmitProbeStatus(ProbeStatus.Testing, "Testing bucket connectivity...");
        var store = _store;
        if (store == null)
        {
            EmitProbeStatus(ProbeStatus.Failed, "not initialized");
            return;
        }

        var key = $"healthcheck/{DeaddropSession.NewSessionId()}";
        var payload = Encoding.UTF8.GetBytes($"probe {DateTime.UtcNow:O}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await store.PutAsync(key, payload, ct).ConfigureAwait(false);
            var got = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (got == null || !got.AsSpan().SequenceEqual(payload))
            {
                Log("Probe: round-trip mismatch (wrote one value, read back another/nothing)");
                EmitProbeStatus(ProbeStatus.Failed, "round-trip mismatch");
                return;
            }
            try { await store.DeleteAsync(key, ct).ConfigureAwait(false); } catch { }
            sw.Stop();
            Log($"Probe: OK, bucket round-trip in {sw.ElapsedMilliseconds}ms");
            EmitProbeStatus(ProbeStatus.Ok, $"OK — bucket reachable ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Log($"Probe failed: {ex.GetType().Name}: {ex.Message}");
            EmitProbeStatus(ProbeStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Log(string msg)
    {
        if (_stopping) return;
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        OnLog?.Invoke(line);
        System.Diagnostics.Debug.WriteLine(line);
    }

    private static string Shorten(string id) => id.Length > 12 ? id[..8] + "..." : id;

    public void Dispose()
    {
        Stop();
        StopListener();
    }
}
