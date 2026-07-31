# Bridge to Freedom — Client app

Cross-platform GUI client for the Bridge to Freedom TCP tunnel. Acts as the
**Helper** — listens on a local TCP port and relays every connection through
an S3-compatible object storage bucket to a
[deaddrop-server](https://github.com/) on the far side (e.g. on a VPS),
which dials the real target (an SSH server, an MTProto proxy, a VLESS/Reality
endpoint, anything TCP).

This is a from-scratch C#/MAUI port of
[deaddrop](../deaddrop)'s `cmd/client` — same wire protocol
(`internal/store` SigV4-signed S3 calls, `internal/tunnel`'s store-and-forward
session/chunk layout), same zero-external-dependency philosophy (no AWS SDK,
just `System.Net.Http` + `System.Security.Cryptography`). The two
implementations are interoperable: this Helper talks to a deaddrop-server
built from the Go source, and vice versa.

> **Why not the earlier WebSocket-bridge design?** An earlier version of this
> app tunnelled through a Yandex API Gateway WebSocket + Cloud Function
> relay. Live testing during an actual mobile "white-list mode" shutdown
> found API Gateway and Cloud Functions both **unreachable** — only managed
> Object Storage stayed on the allow-list. See the [deaddrop
> README](../deaddrop/README.md#why-this-exists) for the full story and the
> reachability matrix that drove this rewrite.


## Supported platforms

- **Android** (API 26+ / Android 8.0+)
- **iOS** (15.0+)
- **Windows** (10 1809+)
- **macOS** (via Mac Catalyst, macOS 12+)
- **Linux** (via the GTK4 backend — see [Linux build](#linux-build) below)

## Requirements

- .NET 10 SDK (only thing required for the Linux head)
- MAUI workload (`dotnet workload install maui`) — needed for Android / iOS / macOS / Windows targets, **but NOT for the Linux head** (`maui` metapackage isn't even installable on Linux because it pulls in ios/maccatalyst). The Linux head builds straight from NuGet packages.
- For Android: Android SDK (installed automatically with the MAUI workload)
- For iOS/macOS: a Mac with Xcode
- For Linux: GTK4 + libadwaita + WebKitGTK runtime libs on the target machine (see [Linux build](#linux-build))

## Important

If you use a proxy client that works as a TUN, you must add an exclusion for the helper process, otherwise you'll get an infinite loop and nothing will work. That said, in the Happ client it didn't work for me even with that (but it works fine without TUN, e.g. for Telegram).

## Build

```bash
cd client

# Android (Debug)
dotnet build -f net10.0-android

# Android (Release APK)
dotnet publish -f net10.0-android -c Release
# The Android APK will be in `bin/Release/net10.0-android/publish/`.

# iOS (requires a Mac with Xcode)
dotnet build -f net10.0-ios

# Windows
dotnet build -f net10.0-windows10.0.19041.0

# macOS (Mac only)
dotnet build -f net10.0-maccatalyst

# Linux
cd ../maui-client-linux
dotnet publish -c Release -r linux-x64 --self-contained -o publish/linux-x64
# -> publish/linux-x64/BridgeToFreedom.Linux  (the executable)
```

## Linux build

Linux support uses the GTK4 backend from [dotnet/maui-labs](https://github.com/dotnet/maui-labs/tree/main/platforms/Linux.Gtk4) (NuGet: [`Microsoft.Maui.Platforms.Linux.Gtk4`](https://www.nuget.org/packages/Microsoft.Maui.Platforms.Linux.Gtk4) + `.Essentials`). The Linux head project lives at [`maui-client-linux/`](../maui-client-linux/) and references the shared MAUI project as a `net10.0` library — the same App / MainPage / TunnelService code runs on every platform.


### Build & run on Linux


Install the GTK4 / libadwaita / WebKitGTK runtime libs (the GTK4 backend P/Invokes them on startup — without them the app throws `DllNotFoundException`):

> **Required: GTK 4.12+** — the Microsoft GTK4 backend P/Invokes `gtk_css_provider_load_from_string`, added in GTK 4.12 (Sep 2023). On older GTK the app crashes at first render with `EntryPointNotFoundException`. Check with `pkg-config --modversion gtk4` or `dpkg -s libgtk-4-1 | grep Version`.
>
> **Distros that work out of the box (GTK 4.12+):** Ubuntu 24.04+, Debian 13 (trixie)+, Fedora 40+, RHEL 9+, Arch, openSUSE Tumbleweed.
> **Distros that DO NOT work** (ship GTK < 4.12): Ubuntu 22.04 LTS (GTK 4.6), Debian 12 bookworm (GTK 4.8).

| Distro | Command |
| --- | --- |
| **Debian 13+ / Ubuntu 24.04+ / Mint 22+ / WSL** | `sudo apt install -y libgtk-4-1 libadwaita-1-0 libwebkitgtk-6.0-4 libgirepository-1.0-1 gsettings-desktop-schemas` |
| **Fedora 40+ / RHEL 9+** | `sudo dnf install -y gtk4 libadwaita webkitgtk6.0 gobject-introspection glib2 cairo pango` |
| **Arch / Manjaro** | `sudo pacman -S --needed gtk4 libadwaita webkitgtk-6.0 gobject-introspection glib2 cairo pango` |
| **openSUSE Tumbleweed** | `sudo zypper install gtk4 libadwaita webkitgtk-6_0 gobject-introspection glib2 cairo pango` |


## Usage

1. Enter **Endpoint** — the S3-compatible API base URL (e.g. `https://storage.yandexcloud.net`)
2. Enter **Region** and **Prefix** — defaults (`ru-central1` / `deaddrop`) match deaddrop's own config defaults; only change these if the server side was configured differently
3. Enter **Bucket**, **Access Key ID**, **Secret Access Key** — a scoped static key for the bucket (see the [deaddrop README](../deaddrop/README.md#security-notes--known-limitations) — use a dedicated, write-`c2s`/read-`s2c` credential, not your main account key, since this device is the one most likely to be lost/seized)
4. Set **Listen Address** and **Port** — where the local app (Shadowrocket, curl, anything) should connect (default `127.123.45.67:1080`). If you want to share the tunnel with other devices on your local network, change the address to `0.0.0.0` manually so the listener binds on all interfaces.
5. Press **CONNECT**

On connect, the app automatically round-trips a small test object through the bucket (PUT + GET + DELETE) and shows the result as a status pill — a quick way to confirm the endpoint/region/bucket/credentials are actually right before routing real traffic through it.

The app keeps the tunnel running in the background:
- **Android**: foreground service with a wake lock
- **iOS**: `beginBackgroundTask` + `BGProcessingTask`, optionally backed by a silent-audio loop (`IOS_BACKGROUND_AUDIO`, see the csproj) for longer-lived background survival on a free (non-Developer-Program) signing identity

Press **DISCONNECT** to stop.

Settings — including the secret key — are saved automatically and restored on next launch; they can also be imported/exported as a `dd://config?...` URL via the clipboard.

## Recommended iOS setup (no NetworkExtension entitlement required)

Apple's Personal VPN / NetworkExtension entitlement requires a paid Apple
Developer Program membership; a free "Personal Team" signing identity
(AltStore/SideStore-style sideloading) can't get it. This Helper works
around that by never trying to be a system VPN — it just runs a local TCP
listener that another already-App-Store-signed app can point at:

- Install [Shadowrocket](https://apps.apple.com/app/shadowrocket/id932747118) (a one-time paid App Store app, so it already has the NetworkExtension entitlement) and this Helper (sideloaded via AltStore/SideStore, since it needs no special entitlements — it's a plain foreground/background app with a TCP listener).
- Configure this Helper with your bucket/keys and press **CONNECT**.
- In Shadowrocket, add an upstream SOCKS/proxy entry pointing at this Helper's `Listen Address:Port` (default `127.123.45.67:1080`), and route Shadowrocket's outbound traffic through it.
- On the server side (the VPS), run `deaddrop-server` with `target` pointing at whatever real proxy is listening there (Xray/VLESS+Reality, an SSH SOCKS tunnel, etc.) — see the [deaddrop README](../deaddrop/README.md).

Flow: `apps on phone → Shadowrocket (VPN entitlement) → this Helper :1080 → bucket → deaddrop-server → real proxy → internet`.

## Recommended setup for Android (proven stable, same idea)

- On the phone: install this app and [v2rayNG](https://github.com/2dust/v2rayNG). In v2rayNG, enable per-app proxying and pick the apps you actually want to route through the tunnel (important so v2rayNG does NOT try to route this Helper's own upstream traffic, which would cause a loop). Create a new outbound profile of type SOCKS (or VLESS) and point it to this Helper's listen address/port. Start this Helper first and connect, then enable v2rayNG.
- On the server side (VPS): run `deaddrop-server` with `target` pointing at Dante (SOCKS) or XRay (VLESS), listening locally on the VPS. `deaddrop-server` delivers each session's bytes to it and the proxy exits to the open internet.

Flow: `app → v2rayNG (per-app) → Helper :1080 → bucket → deaddrop-server → Dante/XRay → internet`.
