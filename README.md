# SysPulse

A PC hardware monitor for Windows with a warm, instrument-panel look. Live CPU, GPU, memory,
network, and storage readings, plus a per-process breakdown.

The shell is WPF and the UI is Blazor running inside `BlazorWebView` (WebView2).

## Run

```powershell
dotnet run --project src/SysPulse.App
```

Requires the .NET 10 SDK and the WebView2 runtime (included with Windows 11).

## Publish

```powershell
dotnet publish src/SysPulse.App -c Release -r win-x64 --self-contained false
```

Output goes to `src/SysPulse.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`. Copy that
whole folder wherever you want to keep the app. Use `--self-contained true` to run on machines without
the .NET 10 Desktop Runtime.

### Administrator rights

Some readings only show up when SysPulse runs elevated:

| Reading | Without admin | With admin |
|---|---|---|
| CPU / RAM / storage / network totals | ✅ | ✅ |
| Per-process CPU, GPU, RAM | ✅ (a few protected processes read 0% CPU) | ✅ |
| GPU load, temperature, clock, fan | ✅ on most NVIDIA/AMD cards | ✅ |
| CPU temperature, clock, and fan | ❌ | ✅ (also needs the [PawnIO](https://pawnio.eu/) driver installed) |
| Per-process download / upload | ❌ | ✅ (kernel ETW session) |

To always prompt for elevation, change `requestedExecutionLevel` in
`src/SysPulse.App/app.manifest` to `requireAdministrator`.

## Layout

```
src/
  SysPulse.Core/        Data collection (no UI)
    Collectors/         One class per data source
    Models/             SystemSnapshot and friends
    Services/           MetricsService: polls collectors once per second, raises Updated
  SysPulse.App/         WPF host + Blazor UI
    MainWindow.xaml     Custom title bar and the BlazorWebView
    Assets/             App icon
    Components/
      Layout/           Sidebar shell
      Pages/            Routable pages (Monitoring is the dashboard)
      Shared/           Gauge, MetricBar, HardwareCard, ProcessTable, ...
    wwwroot/            index.html, global CSS (theme tokens live in css/app.css), fonts
tools/
  New-AppIcon.ps1       Regenerates Assets/SysPulse.ico
```

Data sources:

- **CPU load:** `GetSystemTimes`
- **RAM:** `GlobalMemoryStatusEx`
- **Network totals:** `NetworkInterface` statistics (virtual adapters excluded)
- **Storage:** `DriveInfo` across all fixed drives
- **Per-process CPU/RAM:** `System.Diagnostics.Process`, grouped by process name
- **Per-process GPU:** "GPU Engine" performance counters (the same source Task Manager uses)
- **Temperatures, clocks, fans:** [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- **Per-process network:** kernel TCP/IP ETW events via `Microsoft.Diagnostics.Tracing.TraceEvent`

## License

SysPulse is released under the [MIT License](LICENSE).

## Acknowledgements

The dashboard layout was inspired by NZXT CAM. SysPulse is an independent project and is not
affiliated with or endorsed by NZXT, Inc. NZXT and CAM are trademarks of NZXT, Inc.

Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
