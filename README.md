# SysPulse

A PC hardware monitor for Windows with a warm, instrument-panel look. Live CPU, GPU, memory,
network, and storage readings, plus a per-process breakdown.

The shell is WPF and the UI is Blazor running inside `BlazorWebView` (WebView2).

## Pages

- **Dashboard:** CPU and GPU dials with temperature, clock, power, and fan meters, plus a badge when
  either one is thermal throttling or held at a power limit; memory, network, and storage cards with
  two-minute sparklines; a sortable process table (CPU, GPU, RAM, download, upload).
- **Flight Recorder:** keeps the last hour of readings in memory (nothing is written to disk). Scrub
  across oscilloscope-style traces for CPU, GPU, memory, temperature, power, and network to see the busiest
  processes at any moment. Sustained spikes are logged as events with the processes most likely
  responsible.
- **Why Slow?:** reads the recent Flight Recorder history and explains in plain language what's most
  likely slowing the PC down: thermal throttling, a maxed-out CPU or one app hogging it, memory
  pressure, a disk stuck at 100%, or a power mode holding the CPU back. Some findings come with a
  one-click fix.
- **Alerts & Rules:** plain rules like *GPU temperature is above 85 °C for 30 s → notify me* or
  *the app Cyberpunk2077 is running → switch to Performance*. Profile switches change back when the
  trigger ends. Profiles (also in the title bar) set the Windows power mode: Silent is best power
  efficiency, Default is balanced, Performance is best performance. Notifications come from the
  SysPulse tray icon.
- **System Specs:** processor, graphics adapters, memory modules, motherboard and BIOS, OS, drives,
  and network adapters. **Copy specs** puts a plain-text summary on the clipboard with IP addresses,
  serial numbers, and MAC addresses left out.
- **Audio:** default output and input devices with a live level meter, volume, and mute, plus a list
  of active devices. Volume and mute only change when you use the controls.
- **Settings:** °C/°F, accent theme, start with Windows, mini widget, close to tray, refresh rate,
  process grouping, and table size. Saved to `%APPDATA%\SysPulse\settings.json`.
- **Mini widget:** a slim always-on-top strip styled like a piece of rack gear, with CPU, GPU, RAM, and
  network readouts plus throttle and alert lamps. Drag to move, double-click to open SysPulse. Toggle it
  from the tray menu or Settings.
- **Phone:** a read-only phone dashboard over your home Wi-Fi (off by default). Turn it on, run the one-time
  setup (one UAC prompt), and scan the QR code. Phones pair with a random key; *New key* unpairs them all.
- **Tray:** closing the window keeps SysPulse running in the tray (turn off in Settings), so rules and
  the Flight Recorder keep working. Right-click the tray icon to exit.

SysPulse only reads from your system, with two exceptions: the audio volume and mute controls, and the
Windows power mode when you pick a profile or a rule switches one. Picking a profile also switches to
the Balanced power plan, because Windows only applies power modes under that plan. It never connects out to the
internet and has no telemetry. When **Phone access** is on, it accepts connections from your local network
only, and only from devices with the pairing key. It only writes to `%APPDATA%\SysPulse` (settings and rules) and
`%LOCALAPPDATA%\SysPulse` (WebView2's browser cache). Turning on **Start with Windows** also adds a
`SysPulse` entry to your user's Run registry key, or a `SysPulse` scheduled task when SysPulse is
running as administrator; turning it off removes it. The Phone page's one-time setup adds a URL reservation
for its port and a Windows Firewall rule named `SysPulse phone dashboard` (private networks only); *Remove
setup* deletes both.

## Run

```powershell
dotnet run --project src/SysPulse.App
```

Requires the .NET 10 SDK and the WebView2 runtime (included with Windows 11).

## Publish

```powershell
dotnet publish src/SysPulse.App -c Release -r win-x64 --self-contained false
```

Or use `tools\Publish.ps1`, which first asks a running SysPulse (including one hidden in the tray) to
exit so its files aren't locked, and can copy the result somewhere with `-Destination C:\Tools\SysPulse`.

Output goes to `src/SysPulse.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`. Copy that
whole folder wherever you want to keep the app. Use `--self-contained true` to run on machines without
the .NET 10 Desktop Runtime.

### Administrator rights

Some readings only show up when SysPulse runs elevated:

| Reading | Without admin | With admin |
|---|---|---|
| CPU / RAM / storage / network totals | ✅ | ✅ |
| Per-process CPU, GPU, RAM | ✅ (a few protected processes read 0% CPU) | ✅ |
| GPU load, temperature, clock, fan, power | ✅ on most NVIDIA/AMD cards | ✅ |
| Throttling (CPU on any PC, GPU on NVIDIA) | ✅ | ✅ |
| CPU temperature, clock, fan, and power | ❌ | ✅ (also needs the [PawnIO](https://pawnio.eu/) driver installed) |
| Per-process download / upload | ❌ | ✅ (kernel ETW session) |

To start elevated at sign-in without a UAC prompt, run SysPulse as administrator once and turn on
**Settings → Start with Windows**; that creates a scheduled task with highest privileges. To run elevated
every time you open it by hand, right-click `SysPulse.exe` → Properties → Compatibility → *Run this
program as an administrator*. Only one copy runs at a time: launching SysPulse again brings the
existing window to the front, so close a non-elevated copy before starting an elevated one.

## Layout

```
src/
  SysPulse.Core/        Data collection (no UI)
    Collectors/         One class per data source
    Models/             SystemSnapshot and friends
    Services/           MetricsService: polls collectors on a timer, raises Updated
    Settings/           AppSettings record and the JSON settings store
    Specs/              HardwareSpecsProvider (WMI + registry) for the System Specs page
    Audio/              CoreAudioService (NAudio) for the Audio page
    Recording/          FlightRecorder (rolling one-hour history) and EventDetector (spikes)
    Rules/              AlertRule and the RuleEngine that evaluates rules on every sample
    Power/              Profiles mapped to Windows power modes
    Diagnostics/        SlowdownAnalyzer for the Why Slow? page
    Startup/            Start with Windows (Run key or scheduled task)
    Remote/             Phone dashboard: HttpListener server, one-time setup, and the phone page (phone.html)
  SysPulse.App/         WPF host + Blazor UI
    MainWindow.xaml     Custom title bar (with the profile dropdown) and the BlazorWebView
    TrayIcon.cs         Notification-area icon; shows rule notifications
    WidgetWindow.xaml   The always-on-top mini widget (native WPF, no web view)
    Assets/             App icon
    Components/
      Layout/           Sidebar shell
      Pages/            Monitoring (dashboard), Timeline (Flight Recorder), Checkup (Why Slow?), Rules, SystemSpecs, Audio, Settings
      Shared/           Gauge, MetricBar, HardwareCard, ProcessTable, Sparkline, VuMeter, ...
    wwwroot/            index.html, global CSS (theme tokens live in css/app.css), fonts
tools/
  New-AppIcon.ps1       Regenerates Assets/SysPulse.ico
  Publish.ps1           Closes a running SysPulse cleanly, then publishes (optionally copies to a folder)
```

Data sources:

- **CPU load:** `GetSystemTimes`
- **RAM:** `GlobalMemoryStatusEx`
- **Network totals:** `NetworkInterface` statistics (virtual adapters excluded)
- **Storage:** `DriveInfo` across all fixed drives
- **Per-process CPU/RAM:** `System.Diagnostics.Process`, grouped by process name
- **Per-process GPU:** "GPU Engine" performance counters (the same source Task Manager uses)
- **Temperatures, clocks, fans, power:** [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- **Per-process network:** kernel TCP/IP ETW events via `Microsoft.Diagnostics.Tracing.TraceEvent`
- **System specs:** WMI (`Win32_*`, `MSFT_PhysicalDisk`) and the display adapter registry key
- **Audio:** Windows Core Audio via [NAudio](https://github.com/naudio/NAudio)
- **Profiles:** `powrprof.dll` power plan and power mode (overlay scheme) functions
- **CPU throttling:** the `Processor Information\% Performance Limit` performance counter
- **GPU throttling (NVIDIA):** clock event reasons from NVML (`nvml.dll`, installed with the NVIDIA driver)
- **Disk activity:** `PhysicalDisk\% Idle Time` performance counters (busiest disk)

## License

SysPulse is released under the [MIT License](LICENSE).

## Acknowledgements

The dashboard layout was inspired by NZXT CAM. SysPulse is an independent project and is not
affiliated with or endorsed by NZXT, Inc. NZXT and CAM are trademarks of NZXT, Inc.

Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
