# SysPulse developer guide

Everything you need to work on SysPulse: how it's put together, the conventions it follows, the traps already
found, how to test changes, and how to release. The [README](../README.md) covers what the app does for users.

- [1. Orientation](#1-orientation)
- [2. Build, run, publish, release](#2-build-run-publish-release)
- [3. Architecture](#3-architecture)
- [4. Features in detail](#4-features-in-detail)
- [5. Conventions](#5-conventions)
- [6. Gotchas already solved](#6-gotchas-already-solved)
- [7. Testing and verification](#7-testing-and-verification)
- [8. Known gaps and roadmap](#8-known-gaps-and-roadmap)

---

## 1. Orientation

SysPulse is a Windows hardware monitor with a warm "instrument panel" look: live readings, an hour of history,
plain-language diagnostics, alert rules, a mini widget, and a read-only phone dashboard.

| Piece | Technology |
|---|---|
| Runtime | .NET 10, C# |
| Window shell | WPF (custom title bar via `WindowChrome`), plus a WinForms `NotifyIcon` for the tray |
| UI | Blazor Hybrid: Razor components in `BlazorWebView` (WebView2), scoped CSS |
| Hosting / DI | `Microsoft.Extensions.Hosting` (`Host.CreateEmptyApplicationBuilder`) |
| Sensors | LibreHardwareMonitorLib 0.9.6 |
| Per-process network | Microsoft.Diagnostics.Tracing.TraceEvent (kernel ETW, admin only) |
| Counters | System.Diagnostics.PerformanceCounter (GPU engines, disk, CPU performance limit) |
| Specs | System.Management (WMI) + registry |
| Audio | NAudio.Wasapi 3.x |
| Phone dashboard | `HttpListener` (http.sys), QRCoder for the pairing QR code |
| Fonts | IBM Plex Sans / Mono, bundled (OFL) |

### Solution layout

```
SysPulse.slnx
nuget.config                 nuget.org only (see Gotchas)
src/SysPulse.Core/           Everything that isn't UI. Targets net10.0-windows.
  Audio/                     IAudioService, CoreAudioService
  Collectors/                One class per data source (internal)
  Diagnostics/               SlowdownAnalyzer (Why Slow?)
  Models/                    SystemSnapshot, DeviceMetrics, ThrottleReason, ...
  Power/                     IPowerProfileService, WindowsPowerProfileService
  Processes/                 IProcessActions, ProcessActions (end, open location, priority)
  Recording/                 FlightRecorder, EventDetector, RecordedSample
  Remote/                    Phone dashboard: RemoteDashboardServer, RemotePayload, RemoteSetup, NetworkIdentity, phone.html
  Rules/                     AlertRule, RuleEngine, INotifier
  Services/                  MetricsService (the polling loop), IMetricsSource
  Settings/                  AppSettings record, JsonSettingsService
  Specs/                     HardwareSpecsProvider, Wmi (shared WMI query helpers)
  Startup/                   IStartupService (Run key / scheduled task)
  Storage/                   IDiskHealthProvider, DiskHealthProvider (per-drive health and SMART), DiskText
  Elevation.cs, ServiceCollectionExtensions.cs (AddSysPulseMetrics)
src/SysPulse.App/            WPF + Blazor. Targets net10.0-windows10.0.19041.0. AssemblyName SysPulse.
  App.xaml(.cs)              Startup, single instance, host, shutdown
  MainWindow.xaml(.cs)       Title bar, profile dropdown, BlazorWebView, close-to-tray, window messages
  TrayIcon.cs                Tray icon + notifications (INotifier)
  WidgetWindow.xaml(.cs), WidgetController.cs   Mini widget
  AccentPalette.cs           Accent colors for WPF (keep in sync with app.css)
  Native/NativeMethods.cs    user32 P/Invoke
  Components/
    Layout/MainLayout.razor  Sidebar + ErrorBoundary
    Pages/                   One file per page (see table below)
    Shared/                  Reusable components
    TracePath.cs, Units.cs   SVG trace builder, size/rate formatting
  wwwroot/                   index.html, css/app.css (design tokens), js/, fonts/
tools/
  New-AppIcon.ps1            Regenerates Assets/SysPulse.ico
  Publish.ps1                Closes a running SysPulse cleanly, publishes, optionally copies
docs/
  DEVELOPMENT.md             This file
  screenshots/               README images
.github/workflows/
  ci.yml                     Build on push / pull request
  release.yml                Tag v* → zips on a GitHub Release
```

### Pages

| Sidebar | Route | File | What it does |
|---|---|---|---|
| Dashboard | `/` | `Monitoring.razor` | Hardware cards, memory/network/storage cards, process table with right-click actions |
| Flight Recorder | `/recorder` | `Timeline.razor` | 5/15/60-minute scrubbable traces, busiest processes at the cursor, spike events |
| Why Slow? | `/check` | `Checkup.razor` | Plain-language findings from recent history, with one-click fixes |
| Alerts & Rules | `/rules` | `Rules.razor` | Profile picker, sentence-style rules, activity log |
| Storage | `/storage` | `Drives.razor` | One card per physical drive: health, live activity and throughput, SMART detail, volume meters |
| Phone | `/remote` | `Remote.razor` | Phone dashboard toggle, trusted networks, one-time setup, QR pairing |
| System Specs | `/specs` | `SystemSpecs.razor` | Hardware inventory, "Copy specs" |
| Audio | `/audio` | `Audio.razor` | Default devices, VU meters, volume/mute |
| Settings | `/settings` | `Settings.razor` | Display, startup, monitoring, processes, about |

---

## 2. Build, run, publish, release

Prerequisites: .NET 10 SDK, WebView2 runtime. Optional: the [PawnIO](https://pawnio.eu/) driver (CPU sensors).

```powershell
dotnet build SysPulse.slnx                      # from the repo root
dotnet run --project src/SysPulse.App           # debug run
.\tools\Publish.ps1                             # Release publish; closes a running copy first
.\tools\Publish.ps1 -Destination C:\Tools\SysPulse -SelfContained
```

- **Only one copy runs at a time.** A second launch just brings the first one forward, so close a running
  SysPulse before testing a new build. Closing the window only hides it to the tray; use tray → **Exit**,
  or `tools\Publish.ps1`, which broadcasts the `SysPulse.Quit` window message.
- **Publish output** is `src/SysPulse.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`. The whole
  folder is needed (DLLs and `wwwroot` sit next to the exe). Self-contained is about 220 MB unzipped.
- **Version** lives in `<Version>` in `src/SysPulse.App/SysPulse.App.csproj`. Settings → About shows it.

### Releasing

1. Bump `<Version>`, update the README if features changed, commit, push.
2. `git tag vX.Y.Z` then `git push origin vX.Y.Z`.
3. `.github/workflows/release.yml` runs on `windows-latest`: publishes framework-dependent and
   self-contained builds with `-p:Version=X.Y.Z`, strips `.pdb` files, zips them as
   `SysPulse-X.Y.Z-win-x64.zip` and `SysPulse-X.Y.Z-win-x64-self-contained.zip`, and creates the GitHub Release
   with generated notes. Check the repo's **Actions** tab if a release doesn't appear.

`ci.yml` builds the solution in Release on every push to `main`, every pull request, and on demand.

The exe isn't code-signed, so users see SmartScreen ("More info → Run anyway"). The README says so.

---

## 3. Architecture

### Startup (`App.xaml.cs`)

1. Global exception handlers show a message box and shut down.
2. **Single instance:** a named mutex `Local\SysPulse.SingleInstance`. If another copy owns it, broadcast the
   registered window message `SysPulse.Activate` (with `AllowSetForegroundWindow`) and exit. A hidden or
   elevated copy still receives it (see Gotchas).
3. `ShutdownMode = OnExplicitShutdown`: closing the window doesn't end the app. `App.Quit()` does.
4. Build the host: `AddWpfBlazorWebView`, `TrayIcon` registered as `INotifier`, then `AddSysPulseMetrics()`.
   The tray icon is resolved on the UI thread before `host.Start()`.
5. Create `MainWindow`. With `--minimized` (used by Start with Windows) only its handle is created
   (`StartHidden`), so window messages still work.
6. Create `WidgetController`, which opens or closes the mini widget to follow settings.
7. On exit: dispose the widget controller, stop and dispose the host (hosted services stop in reverse order,
   so rules revert profiles and the phone server closes), release the mutex.

### Data flow

```
MetricsService (BackgroundService, PeriodicTimer = settings refresh rate, 0.5–5 s)
   ├─ CpuLoadCollector            GetSystemTimes
   ├─ MemoryCollector             GlobalMemoryStatusEx
   ├─ NetworkCollector            NetworkInterface stats, physical adapters only
   ├─ StorageCollector            DriveInfo, fixed drives, per volume (every 5 ticks)
   ├─ DiskActivityCollector       PhysicalDisk counters: busiest disk, per-disk active time and throughput,
   │                              and the drive letter -> disk number map the volumes are tagged with
   ├─ ProcessCollector            Process.GetProcesses, CPU-time deltas
   ├─ GpuEngineCollector          "GPU Engine" counters (per process + total)
   ├─ HardwareSensorCollector     LibreHardwareMonitor: temps, clocks, fans, power
   ├─ ThrottleCollector           CPU % Performance Limit + NVIDIA NVML reasons
   └─ ProcessNetworkCollector     private ETW session "SysPulse-ProcessNetwork" (admin only)
        │
        ▼  SystemSnapshot (immutable record), IMetricsSource.Updated (background thread)
        ├─ Blazor pages (InvokeAsync → StateHasChanged)
        ├─ WidgetWindow (Dispatcher.InvokeAsync)
        ├─ FlightRecorder ──Recorded──▶ Dashboard sparklines, Flight Recorder page
        ├─ RuleEngine (notifications, profile switches)
        └─ RemoteDashboardServer (phone JSON + SSE stream)
```

- **Error isolation:** each collector runs through `MetricsService.Sample()`. A throwing collector falls back to
  its last value and logs once per failure streak, so one bad source never blanks the dashboard.
- **Collectors are only touched by the `ExecuteAsync` thread** and are disposed in its `finally`.
- **Process grouping:** grouped by name by default (`ProcessMetrics.ProcessId` is null), or one row per PID.

### Services (`ServiceCollectionExtensions.AddSysPulseMetrics`)

| Service | Kind | Notes |
|---|---|---|
| `ISettingsService` → `JsonSettingsService` | singleton | `%APPDATA%\SysPulse\settings.json`; atomic save; `Changed` fires on the caller's thread |
| `IHardwareSpecsProvider` | singleton | Collects once, cached; Refresh re-collects |
| `IDiskHealthProvider` → `DiskHealthProvider` | singleton | Per-drive health and SMART; cached, re-reads when older than a minute. Deliberately off the polling loop (WMI takes ~1 s) |
| `IAudioService` → `CoreAudioService` | singleton | NAudio; default devices cached, rechecked each second |
| `IPowerProfileService` → `WindowsPowerProfileService` | singleton | Profiles ↔ power mode overlays |
| `IStartupService` → `WindowsStartupService` | singleton | Run key or scheduled task |
| `IProcessActions` → `ProcessActions` | singleton | End / open location / priority, protected list |
| `INotifier` | singleton | `TrayIcon` in the app; `NullNotifier` otherwise (`TryAdd`) |
| `MetricsService` / `IMetricsSource` | hosted | The polling loop |
| `FlightRecorder` / `IFlightRecorder` | hosted | One hour of compact samples in memory |
| `RuleEngine` / `IRuleEngine` | hosted | Evaluates `AppSettings.Rules` every sample |
| `RemoteDashboardServer` / `IRemoteDashboard` | hosted | Phone dashboard |

Hosted services are registered three ways (concrete, interface, hosted), so **their `Dispose` must be
idempotent** (see Gotchas).

### Settings (`AppSettings`)

An immutable record; every change goes through `ISettingsService.Update(s => s with { ... })`, which calls
`Normalize()` (clamps and repairs hand-edited values), saves, and raises `Changed`.

| Setting | Default | Notes |
|---|---|---|
| `TemperatureUnit` | Celsius | Stored readings are always °C; convert at display time |
| `Accent` | Amber | Amber / Phosphor / Arctic |
| `PollingIntervalSeconds` | 1 | 0.5–5 |
| `GroupProcesses` | true | |
| `ProcessRows` | 50 | 10–500 |
| `CloseToTray` | true | |
| `ShowWidget`, `WidgetLeft`, `WidgetTop` | false, null | Position saved after dragging |
| `Rules` | 2 defaults | Max 50 |
| `RemoteEnabled`, `RemotePort`, `RemoteKey` | false, 8787, "" | Key generated when first enabled |
| `RemoteOnlyTrustedNetworks`, `RemoteTrustedNetworks` | true, [] | Max 20 networks |

Add new settings with an `init` default so older settings files load unchanged.

### UI

- **Blazor pages** subscribe to service events in `OnInitialized`, unsubscribe in `Dispose`, and marshal with
  `InvokeAsync`. `MainLayout` wraps pages in an `ErrorBoundary` that recovers on navigation.
- **Shared components:** `Gauge` (270° dial), `MetricBar` (segmented LED meter), `HardwareCard`, `LoadStatus`,
  `ProcessTable` (sorting + right-click menu), `Sparkline`, `SegmentedControl`, `ToggleSwitch`,
  `ConfirmDialog`, `SpecCard`/`SpecRow`/`SpecHeading`, `VuMeter`, `AudioEndpointCard`.
- **WPF parts:** `MainWindow` title bar with the accent "power lamp" and profile `ComboBox`; `WidgetWindow` is
  native WPF (no second WebView); `TrayIcon` uses WinForms `NotifyIcon` (balloon tips show as Windows notifications).
- **JavaScript** is minimal: `webview-repaint.js` (stale-frame workaround, don't remove) and `scope.js`
  (Flight Recorder pointer tracking, plus `sysPulseViewport()` for keeping popups on screen).

### Window messages

| Message (`RegisterWindowMessage`) | Sent by | Effect |
|---|---|---|
| `SysPulse.Activate` | A second launch | Running copy shows and focuses its window |
| `SysPulse.Quit` | `tools/Publish.ps1` | Running copy quits cleanly (same as tray → Exit) |

Both are allowed through UIPI with `ChangeWindowMessageFilterEx`, so a non-elevated sender reaches an elevated copy.

---

## 4. Features in detail

### Flight Recorder (`Recording/`)
- Keeps **1 hour** of `RecordedSample`s: loads, temps, network, CPU/GPU power, throttle reasons, busiest-disk
  activity, and the top processes (5 by CPU, 3 by GPU, 3 by memory). Nothing is written to disk.
- `TracePath` draws traces into a 1000×100 SVG view box with 500 columns, keeping each column's min and max so
  spikes survive; gaps over 12 s break the line.
- `EventDetector` thresholds: CPU ≥ 85%, GPU ≥ 90%, memory ≥ 90%, CPU ≥ 90 °C, GPU ≥ 85 °C, and thermal
  throttling. An event needs ≥ 3 samples; dips shorter than 5 s merge. Culprits are the top 3 processes by
  average share (≥ 2%).

### Why Slow? (`Diagnostics/SlowdownAnalyzer.cs`)
Pure function over recent samples; needs ≥ 5 samples. Findings, most severe first:

| Finding | Rule of thumb |
|---|---|
| Overheating (CPU/GPU) | Thermal throttling ≥ 5% of samples (warning), ≥ 20% (problem) |
| CPU maxed out / busy | Average ≥ 80% or ≥ 90% for half the samples (problem); average ≥ 50% (warning) |
| App hogging CPU | Top process averages ≥ 20% while the CPU isn't otherwise busy |
| Memory nearly full / getting full | Average ≥ 90% / ≥ 80%; biggest process using ≥ 30% of RAM is an FYI |
| Disk at 100% / busy | Average ≥ 85% or ≥ 95% for half the samples / average ≥ 50% |
| CPU held back | "Limited" throttling ≥ 30% of samples; offers Switch to Performance |
| GPU at power limit / fully loaded | Power-limited ≥ 40% / average ≥ 90% (FYI) |
| Short history | Recording is shorter than the chosen window |

Findings can carry an action (`SwitchToPerformance`, `OpenRecorder`, `EndProcess` with `ProcessName`).

### Throttling (`Collectors/ThrottleCollector.cs`)
- Only counted while the device is ≥ 20% busy.
- **CPU:** `Processor Information(_Total)\% Performance Limit` below 99 → throttled. Thermal if an Intel core is
  within 5 °C of TjMax ("Distance to TjMax" sensors), or ≥ 95 °C without that data; otherwise "Limited".
- **NVIDIA GPU:** NVML clock event reasons. Thermal bits `0x20 | 0x40`; power `0x4 | 0x80`; hardware slowdown
  `0x8` → Limited. "Power limited" is normal under heavy load, so only thermal throttling becomes an event.
- AMD and Intel GPUs report null (unknown).

### Alerts & rules (`Rules/`)
- A rule is "When [metric above threshold for N seconds | app running] → [notify | switch profile]".
- Evaluated every sample. Recovery needs the value to drop 3 points below the threshold (hysteresis).
  Notifications are rate-limited to one per rule per 5 minutes. A profile switch reverts when the trigger ends,
  unless the profile was changed by hand meanwhile, and reverts on exit.
- Defaults: GPU temperature ≥ 85 °C for 30 s → notify; CPU temperature ≥ 90 °C for 30 s → notify.
- The Rules page warns when the Windows notifications master switch is off
  (`HKCU\...\PushNotifications\ToastEnabled = 0`).

### Profiles (`Power/`)
Silent / Default / Performance map to the Windows **power mode** (best efficiency / balanced / best performance),
which is an overlay scheme on the Balanced plan. Applying a profile switches to the Balanced plan first. On a
custom plan the dropdown shows no selection.

### Phone dashboard (`Remote/`)
- **Server:** `HttpListener` on `http://+:{port}/`. Without a URL reservation (and not elevated) it falls back to
  `http://localhost:{port}/` (state `LocalOnly`).
- **Endpoints:** `GET /` (phone.html, embedded resource), `/manifest.webmanifest`, `/fonts/*.woff2` (from the
  app's wwwroot), `/api/snapshot`, `/api/history?minutes=1..60` (≤ 300 points, bucket peaks), `/api/stream`
  (Server-Sent Events, max 8 phones).
- **Security:** read-only. API calls need the pairing key (header `X-SysPulse-Key` or `?key=`), compared in
  constant time. The key is 24 random bytes, base64url. Requests from non-local addresses get 403. 20 bad keys
  from one address lock it out for 10 minutes. The page has a strict CSP and renders all data with
  `textContent`. A new key disconnects every open stream.
- **One-time setup** (`RemoteSetup`, one UAC prompt): `netsh http add urlacl` for the current user plus the
  firewall rule `SysPulse phone dashboard`, TCP port only, private and domain profiles. *Remove setup* deletes both.
- **Trusted networks** (`NetworkIdentity`): a network is a SHA-256 hash (first 16 hex) of its gateway's MAC
  address from `SendARP`, named by `netsh wlan show interfaces` when Windows shares the SSID. Checked on
  `NetworkChange.NetworkAddressChanged` (after a 3 s settle) and every 30 s. The first network seen while enabled
  is trusted automatically. On an untrusted network the state is `Paused` and the listener is closed.
- **JSON** is written with `Utf8JsonWriter` (`RemotePayload`); NaN/infinity become null.

### Storage and drive health (`Storage/`, `Collectors/`, `Pages/Drives.razor`)
Three sources, joined on the Windows disk number:

| What | Source | Cadence | Needs admin |
|---|---|---|---|
| Space per volume | `DriveInfo` (`StorageCollector`) | every 5 ticks | No |
| Active time, read/write per disk | `PhysicalDisk` counters (`DiskActivityCollector`) | every tick | No |
| Model, size, media/bus type, health status | `MSFT_PhysicalDisk` (`DiskHealthProvider`) | cached, ~1 min | No |
| Temperature, wear, power-on hours, error counts | `MSFT_StorageReliabilityCounter` | cached, ~1 min | **Yes** |

- **Volumes find their disk without WMI.** The `PhysicalDisk` counter instance names are `"<number> <letters>"`
  ("0 C:", "1 D: E:"), so `DiskActivityCollector` parses the mapping out of names it already reads every tick.
  A disk only appears once it has two samples, because all three counters are rates.
- **The page builds one card per disk number** seen in any of the three sources, so a drive still shows up when
  only one of them knows about it. Volumes Windows won't place on a disk get an "Other volumes" card rather
  than vanishing.
- **SMART rows are per drive, not per machine.** Drives report different subsets (the NVMe this was built
  against reports temperature and wear but not power-on hours or error counts), so `SpecRow` hides anything
  that came back null. `DiskHealth.HasDetail` gates the whole block on temperature or power-on hours being
  present, because `Wear` reads 0 both for a new drive and for one that doesn't track it.
- **"Failure predicted"** is `OperationalStatus` containing 5 (Predictive Failure); it's the one state on the
  page shown in red.

### Process actions (`Processes/ProcessActions.cs`)
- Grouped rows act on every process with that name; PID rows re-check that the PID still has that name.
- **Protected** (never ended or re-prioritized): System, Idle, Registry, Memory Compression, Secure System,
  smss, csrss, wininit, winlogon, services, lsass, LsaIso, svchost, dwm, fontdrvhost, sihost, ctfmon, spoolsv,
  audiodg, WUDFHost, Defender processes, and SysPulse itself.
- File location uses `QueryFullProcessImageName` with limited query rights, then `explorer /select`.
- The UI always confirms before ending (`ConfirmDialog`).

### Start with Windows (`Startup/`)
- Enabled while elevated → scheduled task `SysPulse` (logon trigger, highest privileges, no time limit, runs on
  battery, priority 5). Otherwise → `HKCU\...\Run\SysPulse`. Both pass `--minimized`.
- A task created elevated can only be removed by an elevated SysPulse; the UI explains that.

### Mini widget (`WidgetWindow`)
Topmost, no taskbar entry, no activation. CPU/GPU/RAM readouts with 12-segment bars (last 2 red), network,
THROTTLE lamp (red thermal, amber power/limited) and ALERT lamp (any rule active). Drag saves the position;
an off-screen saved position resets to top-center.

### System Specs and Audio
- Specs sections are collected independently so one failing WMI class doesn't hide the rest. GPU memory comes
  from the display adapter registry key (`HardwareInformation.qwMemorySize`) because WMI caps at 4 GB. Serial
  numbers and MAC addresses are never collected; "Copy specs" also leaves out IPs.
- Audio polls levels ~15×/s and devices every ~2 s; meters use −60…0 dB and decay smoothly.

---

## 5. Conventions

- **Match the surrounding code:** file-scoped namespaces, primary constructors where the file already uses them,
  records for data, comments that explain *why*.
- **Design tokens:** colors, fonts, and radii live in `wwwroot/css/app.css`. Use the variables
  (`--accent`, `--accent-soft`, `--accent-border`, `--accent-glow`, `--text-muted`, ...); never hard-code accent
  colors. A new accent theme needs a `[data-accent]` block in `app.css` **and** an entry in `AccentPalette.cs`.
  WPF colors in `App.xaml` and the phone page's tokens mirror `app.css`.
- **Type:** Plex Sans for text; Plex Mono for readouts and uppercase labels (letter spacing ≈ 0.14em).
- **Retro touches:** dial ticks, segmented LED meters, glowing lamps, dot-grid background, oscilloscope traces.
- **Threads:** service events arrive on background threads. Use `InvokeAsync` in components and
  `Dispatcher.InvokeAsync` in WPF.
- **Destructive or system-changing actions** need an explicit user action, a confirmation where data can be lost,
  and a clear result message. Protected processes are never touched.
- **Privacy:** no telemetry, no outbound connections. Don't collect serials or MAC addresses (network identity
  stores only a hash). Anything new written outside `%APPDATA%\SysPulse` / `%LOCALAPPDATA%\SysPulse` must be
  listed in the README's privacy paragraph.
- **JSON for the phone:** `Utf8JsonWriter`, not reflection serialization.
- **Hosted services and anything registered more than once:** idempotent `Dispose`.
- **Docs checklist when adding a feature:** README (Pages, privacy paragraph, admin table, Layout, Data sources),
  this guide, `THIRD-PARTY-NOTICES.md` for new packages, and a screenshot if the feature is visual.

---

## 6. Gotchas already solved

**Build and tooling**
- **Private NuGet feeds in a global config** can break restore with 401s. The repo's `nuget.config` clears sources
  and uses nuget.org only. Run `dotnet` tools (like `dotnet dnx`) from the repo so it applies.
- **`Microsoft.Windows.SDK.NET` not found at startup:** BlazorWebView.Wpf 10 needs a Windows SDK TFM, hence
  `net10.0-windows10.0.19041.0` for the App.
- **WinForms alongside WPF:** `UseWindowsForms` adds implicit `System.Windows.Forms` / `System.Drawing` usings that
  clash with WPF types. The csproj removes them; alias as `Forms = System.Windows.Forms`.
- **`App.Exit()` clashes** with `Application.Exit` (CS0108), hence `App.Quit()`.
- **`UniformGrid`** is in `System.Windows.Controls.Primitives`.
- **`Path` / `Directory`** in WPF code-behind and `.razor` files: write `System.IO.Path` in full (WPF has `System.Windows.Shapes.Path`).

**Blazor / WebView2**
- **Page looks stuck until the mouse moves:** `WebView2CompositionControl` sometimes doesn't present the last frame.
  `wwwroot/js/webview-repaint.js` flips one corner pixel after DOM changes to force a frame. **Don't remove it.**
- **Never put classes on `<div id="app">`**: Blazor keeps its attributes, which once turned the whole UI faint.
- **NavLink styling** needs `::deep` (its `<a>` doesn't get the scoped attribute).
- **Razor `<` in `@code` switch patterns** (`< 25 =>`) parses as a tag; use `>=` patterns.
- **Generic components with enum callbacks** need an explicitly typed lambda:
  `ValueChanged="@((TemperatureUnit unit) => ...)"`.
- **WebView2 profile folder** defaults next to the exe (fails under Program Files; elevated and non-elevated can't
  share). It's set to `%LOCALAPPDATA%\SysPulse\WebView2` or `WebView2-Admin` in `WebView_Initializing`.
- **`Host.CreateApplicationBuilder`** reads `appsettings.json` from the working directory; the app uses
  `CreateEmptyApplicationBuilder` rooted at `AppContext.BaseDirectory`.

**Windows and hardware**
- **ETW:** never use the shared "NT Kernel Logger" session; use a private session name.
- **ICO files:** System.Drawing can't read PNG-compressed entries, so the icon uses BMP entries for 16–64 px.
- **LibreHardwareMonitor sensor names vary:** Intel hybrid cores are "P-Core #n" / "E-Core #n"; power sensors read
  **0** (not null) when unavailable, so treat ≤ 0 as missing; NVIDIA power is "GPU Package". LHM exposes no
  throttle flags. To see what a machine reports, run a throwaway file-based app with
  `#:package LibreHardwareMonitorLib@0.9.6` that dumps every sensor (copy `nuget.config` next to it).
- **Storage WMI is three different kinds of "no".** `MSStorageDriver_FailurePredictStatus` (root\wmi) answers
  **"not supported"** on NVMe — it's an ATA-era interface, so don't reach for it. Its modern replacement
  `MSFT_StorageReliabilityCounter` answers **"access denied"** without elevation when queried directly, but
  reached through `ManagementObject.GetRelated` it just comes back **empty, with no exception at all**. So
  `DiskHealthProvider` reads the data through the association (correct pairing, no path escaping) and, only
  when nothing reported any detail, runs one direct query purely to find out whether the answer was "denied"
  or "this drive doesn't do that".
- **`GetRelated` needs the key property in the SELECT.** Query `MSFT_PhysicalDisk` without `ObjectId` and the
  returned objects have no WMI path, so `GetRelated` throws `InvalidOperationException` ("operation is not
  valid due to the current state of the object") rather than anything that names the real problem.
- **NVML:** `nvml.dll` ships with the NVIDIA driver in System32 and works without admin.
  `nvmlDeviceGetCurrentClocksEventReasons` is the R535+ name; fall back to `...ThrottleReasons`.
- **Power modes** are overlay schemes (`PowerGetEffectiveOverlayScheme` / `PowerSetActiveOverlayScheme`, exported
  from powrprof.dll but not in SDK headers) and only apply under the Balanced plan.
- **Hidden window + single instance:** `Process.MainWindowHandle` is 0 for hidden windows, so activation uses a
  broadcast window message instead.
- **Windows notifications master switch** (`ToastEnabled = 0`) silently drops balloon tips; Do Not Disturb can't be
  detected via a public API.
- **Microphone meter** only moves while some app has the mic open; SysPulse doesn't open it (privacy indicator).
- **`Environment.TickCount64 - long.MinValue`** overflows; use nullable timestamps instead of `MinValue` sentinels.
- **HttpListener on `+`** needs a URL reservation (or elevation), error 5 otherwise. The firewall rule is by port
  because http.sys (System) accepts connections, not SysPulse.exe.
- **Wi-Fi names** from `netsh wlan` can be hidden on Windows 11 24H2+ without location permission; names fall back
  to "adapter via gateway".

**.NET**
- **Singletons registered three ways** (`AddSingleton<T>`, `AddSingleton<IT>(sp => ...)`,
  `AddHostedService(sp => ...)`) get disposed more than once. Make `Dispose` idempotent.
- **File-based apps** (`dotnet run file.cs`) disable reflection-based System.Text.Json and don't rebuild a changed
  `#:project` reference. Delete `%TEMP%\dotnet\runfile\<name>-*` to force a rebuild. They also disable built-in
  COM, so anything touching `System.Management` dies with "Built-in COM has been disabled via a feature switch"
  until you add `#:property BuiltInComInteropSupport=true`.

---

## 7. Testing and verification

There are no automated tests yet. Changes are verified like this:

1. **Build:** `dotnet build SysPulse.slnx` with 0 warnings. Check `SysPulse.dll`'s timestamp if a build looks
   suspiciously fast.
2. **Core services without the UI:** a throwaway file-based harness outside the repo:
   ```csharp
   #:project C:\path\to\SysPulse\src\SysPulse.Core\SysPulse.Core.csproj
   #:package Microsoft.Extensions.Hosting@10.0.12
   #:package Microsoft.Extensions.Logging.Console@10.0.12
   #:property TargetFramework=net10.0-windows
   // Host.CreateEmptyApplicationBuilder → AddSysPulseMetrics() → register a fake ISettingsService last
   // (in-memory AppSettings) so the real settings file is never touched → StartAsync → inspect services.
   ```
   This is how the phone server was tested: `curl` for status codes and payloads, and headless Edge
   (`msedge --headless=new --screenshot --window-size=... --user-data-dir=<temp>`) for the phone layout. Edge's
   window can't go below ~500 px wide, so narrower screenshots are cropped, not reflowed.
3. **Desktop UI:** close any running copy, launch the Debug build, and drive it with a small PowerShell script
   using `user32` (`SetCursorPos`, `mouse_event`, `GetWindowRect`) plus `Graphics.CopyFromScreen` for screenshots.
   Sidebar items are about 42 px apart starting near (86, 70) in window coordinates.
4. **Testing an elevated copy is a different exercise.** A non-elevated script can't drive one: UIPI blocks
   `SetForegroundWindow`, and even the `SysPulse.Quit` broadcast doesn't reach it. `SysPulse.Activate` doesn't
   help either, because a script that isn't itself foreground has no foreground rights to donate. What works is
   running the whole drive-and-screenshot script elevated, launching the app from it, and clicking while it
   still owns the foreground from launch. Assert `GetForegroundWindow()` is SysPulse's window *before* clicking,
   or a stray click lands in whatever app is actually in front.
5. **Be careful with the real machine:** back up and restore `%APPDATA%\SysPulse\settings.json` around UI tests,
   and don't click things that change the system (volume, power mode, clipboard, ending processes) unless that's
   the point of the test. Close test copies with the `SysPulse.Quit` message rather than killing the process.

---

## 8. Known gaps and roadmap

**Known gaps**
- Per-process network (ETW) and PawnIO CPU sensors haven't been verified on a machine with PawnIO installed.
- Throttle thresholds are first guesses; tune with real data under load. AMD GPU throttling isn't detected.
- Phone auto-off has been tested by identifying the home network, not yet by joining a different network.
- The Release workflow runs for the first time on the first `v*` tag.
- Audio can't switch the default device (no public API). CPU fan only shows if a header is labelled CPU/Pump.
- The Storage page has only been seen on a single-NVMe machine. Multiple disks, a spinning drive, a USB drive,
  and a disk carrying several volumes are all handled in code but unverified. The same goes for the
  "Other volumes" card, which needs a volume Windows won't place on a disk.
- SMART fields vary by drive: the NVMe tested reports temperature and wear but not power-on hours or error
  counts, and nothing yet has exercised the Warning / Unhealthy / failure-predicted states.
- No automated tests.

**Roadmap candidates** (roughly by impact)
- In-game FPS overlay and frame-time history (PresentMon), also on the widget
- Fan and pump curves (LibreHardwareMonitor controls) with strict safety fallbacks
- Cross-brand RGB (OpenRGB SDK), AIO/LCD support (liquidctl)
- Stress test with a cooling report; startup apps manager; boot/crash history
- More rule triggers (throttling, power, disk, time of day) and actions (run a program, end a process)
- Per-app audio mixer; per-core CPU grid; battery card; CSV export
- Storage follow-ups: drive temperature as a rule trigger and a Why Slow? finding, SMART history in the
  Flight Recorder, and a free-space alert
- Tray tooltip with live readings; widget channel picker and click-through mode
- Windows Service split (always-elevated sensors, phone dashboard without the app open); installer/auto-update
- Unit tests for the analyzers, rule engine, units, and process grouping
