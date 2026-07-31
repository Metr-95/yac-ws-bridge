using BridgeToFreedom.Services;
using System.Text;
using System.Web;

namespace BridgeToFreedom;

public partial class MainPage : ContentPage
{
    private readonly TunnelService _tunnel;
    private readonly StringBuilder _logBuffer = new();
    private bool _isRunning;

    public bool IsNotRunning => !_isRunning;

    public MainPage(TunnelService tunnel)
    {
        InitializeComponent();
        BindingContext = this;
        _tunnel = tunnel;
        _tunnel.OnLog += OnTunnelLog;
        _tunnel.OnProbeStatusChanged += OnProbeStatusChanged;

        // Load saved settings
        EndpointEntry.Text = Preferences.Default.Get("Endpoint", "https://storage.yandexcloud.net");
        RegionEntry.Text = Preferences.Default.Get("Region", "ru-central1");
        PrefixEntry.Text = Preferences.Default.Get("Prefix", "deaddrop");
        BucketEntry.Text = Preferences.Default.Get("Bucket", "");
        AccessKeyIdEntry.Text = Preferences.Default.Get("AccessKeyId", "");
        SecretAccessKeyEntry.Text = Preferences.Default.Get("SecretAccessKey", "");
        ListenAddressEntry.Text = Preferences.Default.Get("ListenAddress", "127.123.45.67");
        ListenPortEntry.Text = Preferences.Default.Get("ListenPort", "1080");

        // Restore UI state if tunnel is already running (e.g. after activity recreate from background)
        if (_tunnel.IsRunning)
        {
            _isRunning = true;
            ConnectButton.Text = "DISCONNECT";
            ConnectButton.BackgroundColor = Color.FromArgb("#D32F2F");
            OnPropertyChanged(nameof(IsNotRunning));
            _tunnel.OnStopped += OnTunnelStopped;
            AddLog("[resumed — tunnel is running in background]");
        }
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        try
        {
            var qs = HttpUtility.ParseQueryString("");
            qs["endpoint"] = EndpointEntry.Text?.Trim() ?? "";
            qs["region"] = RegionEntry.Text?.Trim() ?? "";
            qs["prefix"] = PrefixEntry.Text?.Trim() ?? "";
            qs["bucket"] = BucketEntry.Text?.Trim() ?? "";
            qs["ak"] = AccessKeyIdEntry.Text?.Trim() ?? "";
            qs["sk"] = SecretAccessKeyEntry.Text?.Trim() ?? "";
            qs["listen"] = $"{ListenAddressEntry.Text?.Trim()}:{ListenPortEntry.Text?.Trim()}";

            var ddUrl = $"dd://config?{qs}";
            await Clipboard.Default.SetTextAsync(ddUrl);
            AddLog("Config exported to clipboard");
        }
        catch (Exception ex)
        {
            AddLog($"Export failed: {ex.Message}");
        }
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        try
        {
            var text = await Clipboard.Default.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("dd://"))
            {
                await DisplayAlertAsync("Import", "No dd:// config found in clipboard", "OK");
                return;
            }

            var uri = new Uri(text);
            var qs = HttpUtility.ParseQueryString(uri.Query);

            EndpointEntry.Text = qs["endpoint"] ?? EndpointEntry.Text;
            RegionEntry.Text = qs["region"] ?? RegionEntry.Text;
            PrefixEntry.Text = qs["prefix"] ?? PrefixEntry.Text;
            BucketEntry.Text = qs["bucket"] ?? "";
            AccessKeyIdEntry.Text = qs["ak"] ?? "";
            SecretAccessKeyEntry.Text = qs["sk"] ?? "";

            var listen = qs["listen"] ?? "";
            var colonIdx = listen.LastIndexOf(':');
            if (colonIdx > 0)
            {
                ListenAddressEntry.Text = listen[..colonIdx];
                ListenPortEntry.Text = listen[(colonIdx + 1)..];
            }

            AddLog("Config imported from clipboard");
        }
        catch (Exception ex)
        {
            AddLog($"Import failed: {ex.Message}");
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        if (_isRunning)
        {
            StopPlatformService();
            _tunnel.Stop();
            _isRunning = false;
            ConnectButton.Text = "CONNECT";
            ConnectButton.BackgroundColor = Color.FromArgb("#512BD4");
            OnPropertyChanged(nameof(IsNotRunning));
            AddLog("Stopped by user.");
            return;
        }

        // Validate
        var endpoint = EndpointEntry.Text?.Trim();
        var region = RegionEntry.Text?.Trim();
        var prefix = PrefixEntry.Text?.Trim();
        var bucket = BucketEntry.Text?.Trim();
        var accessKeyId = AccessKeyIdEntry.Text?.Trim();
        var secretAccessKey = SecretAccessKeyEntry.Text?.Trim();
        var addr = ListenAddressEntry.Text?.Trim();
        var portStr = ListenPortEntry.Text?.Trim();

        if (string.IsNullOrEmpty(endpoint) || !(endpoint.StartsWith("http://") || endpoint.StartsWith("https://")))
        {
            await DisplayAlertAsync("Error", "Endpoint must start with http:// or https://", "OK");
            return;
        }
        if (string.IsNullOrEmpty(bucket))
        {
            await DisplayAlertAsync("Error", "Bucket is required", "OK");
            return;
        }
        if (string.IsNullOrEmpty(accessKeyId) || string.IsNullOrEmpty(secretAccessKey))
        {
            await DisplayAlertAsync("Error", "Access Key ID and Secret Access Key are required", "OK");
            return;
        }
        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
        {
            await DisplayAlertAsync("Error", "Port must be 1-65535", "OK");
            return;
        }

        _tunnel.Endpoint = endpoint;
        _tunnel.Region = string.IsNullOrEmpty(region) ? "ru-central1" : region;
        _tunnel.Prefix = string.IsNullOrEmpty(prefix) ? "deaddrop" : prefix;
        _tunnel.Bucket = bucket;
        _tunnel.AccessKeyId = accessKeyId;
        _tunnel.SecretAccessKey = secretAccessKey;
        _tunnel.ListenAddress = addr ?? "127.123.45.67";
        _tunnel.ListenPort = port;

        // Save settings (secret key included — this device is the whole
        // point of the app, same trust boundary as the rest of the config).
        Preferences.Default.Set("Endpoint", endpoint);
        Preferences.Default.Set("Region", _tunnel.Region);
        Preferences.Default.Set("Prefix", _tunnel.Prefix);
        Preferences.Default.Set("Bucket", bucket);
        Preferences.Default.Set("AccessKeyId", accessKeyId);
        Preferences.Default.Set("SecretAccessKey", secretAccessKey);
        Preferences.Default.Set("ListenAddress", _tunnel.ListenAddress);
        Preferences.Default.Set("ListenPort", portStr!);

        _isRunning = true;
        ConnectButton.Text = "DISCONNECT";
        ConnectButton.BackgroundColor = Color.FromArgb("#D32F2F");
        OnPropertyChanged(nameof(IsNotRunning));

        _logBuffer.Clear();
        LogLabel.Text = "";

        // Start: on Android the foreground service runs the tunnel;
        // on other platforms we run it in a Task.
        StartPlatformService();

        _tunnel.OnStopped += OnTunnelStopped;
    }

    private void OnTunnelStopped()
    {
        _tunnel.OnStopped -= OnTunnelStopped;
        StopPlatformService();
        // Use the page's own Dispatcher (works on every platform incl. Linux/GTK4)
        // instead of the static MainThread facade, which has no implementation on
        // Linux and throws NotImplementedInReferenceAssemblyException.
        Dispatcher.Dispatch(() =>
        {
            _isRunning = false;
            ConnectButton.Text = "CONNECT";
            ConnectButton.BackgroundColor = Color.FromArgb("#512BD4");
            OnPropertyChanged(nameof(IsNotRunning));
        });
    }

    private void OnTunnelLog(string line)
    {
        Dispatcher.Dispatch(() => AddLog(line));
    }

    /// <summary>
    /// Updates the probe status pill below the Connect button. Called from
    /// arbitrary threads — marshals to the UI thread itself.
    /// </summary>
    private void OnProbeStatusChanged(ProbeStatus status, string detail)
    {
        Dispatcher.Dispatch(() =>
        {
            switch (status)
            {
                case ProbeStatus.Idle:
                    ProbeStatusBorder.IsVisible = false;
                    return;

                case ProbeStatus.Testing:
                    ProbeStatusBorder.IsVisible = true;
                    ProbeStatusBorder.BackgroundColor = Color.FromArgb("#FFF3CD"); // amber
                    ProbeStatusIcon.TextColor   = Color.FromArgb("#856404");
                    ProbeStatusLabel.TextColor  = Color.FromArgb("#856404");
                    ProbeStatusIcon.Text  = "⧗";
                    ProbeStatusLabel.Text = string.IsNullOrEmpty(detail) ? "Testing bucket connectivity..." : detail;
                    return;

                case ProbeStatus.Ok:
                    ProbeStatusBorder.IsVisible = true;
                    ProbeStatusBorder.BackgroundColor = Color.FromArgb("#2E7D32"); // vivid green
                    ProbeStatusIcon.TextColor   = Color.FromArgb("#FFFFFF");
                    ProbeStatusLabel.TextColor  = Color.FromArgb("#FFFFFF");
                    ProbeStatusIcon.Text  = "✓";
                    ProbeStatusLabel.Text = string.IsNullOrEmpty(detail) ? "Bucket reachable" : detail;
                    return;

                case ProbeStatus.Failed:
                    ProbeStatusBorder.IsVisible = true;
                    ProbeStatusBorder.BackgroundColor = Color.FromArgb("#F8D7DA"); // red
                    ProbeStatusIcon.TextColor   = Color.FromArgb("#721C24");
                    ProbeStatusLabel.TextColor  = Color.FromArgb("#721C24");
                    ProbeStatusIcon.Text  = "✕";
                    ProbeStatusLabel.Text = string.IsNullOrEmpty(detail) ? "Bucket connectivity test failed" : detail;
                    return;
            }
        });
    }

    private void AddLog(string line)
    {
        _logBuffer.AppendLine(line);
        // Keep last 200 lines
        var lines = _logBuffer.ToString().Split('\n');
        if (lines.Length > 200)
        {
            _logBuffer.Clear();
            foreach (var l in lines[^200..])
                _logBuffer.AppendLine(l);
        }
        LogLabel.Text = _logBuffer.ToString();
        try { LogScrollView.ScrollToAsync(LogLabel, ScrollToPosition.End, false); }
        catch { }
    }

    private void StartPlatformService()
    {
#if ANDROID
        Platforms.Android.TunnelForegroundService.Tunnel = _tunnel;
        var context = Android.App.Application.Context;
        var intent = new Android.Content.Intent(context, typeof(Platforms.Android.TunnelForegroundService));
        context.StartForegroundService(intent);
#else
        // iOS, macOS, Windows: run the tunnel in a background task.
        // iOS stays alive via beginBackgroundTask + BGProcessingTask + silent
        // audio (AppDelegate.cs / SilentAudioService.cs).
        _ = Task.Run(async () =>
        {
            try { await _tunnel.StartAsync(); }
            catch (Exception ex) { AddLog($"Fatal: {ex.Message}"); }
            finally { OnTunnelStopped(); }
        });
#endif
    }

    private static void StopPlatformService()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        var intent = new Android.Content.Intent(context, typeof(Platforms.Android.TunnelForegroundService));
        context.StopService(intent);
        Platforms.Android.TunnelForegroundService.Tunnel = null;
#endif
        // iOS/macOS/Windows: tunnel stops via _tunnel.Stop() called before this
    }
}
