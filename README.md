# DLB Precision Monitor

A compact Windows 11 hardware widget in DLBPrecision blue and purple. Designed to stay on another monitor while you play; it does not inject into games.

![Horizontal widget with sample readings](docs/images/widget-horizontal-sample.png)

Appearance preview with sample data. The widget also supports a resizable vertical layout.

## Install

**[Download DLB Precision Monitor for Windows 11](https://github.com/dlbprecision/dlb-precision-monitor/releases/download/v0.1.4/DLB-Precision-Monitor-0.1.4-Setup.exe)**

**v0.1.4 fixes the installer stopping at the PawnIO dependency check.** Setup checks the actual driver, starts an existing stopped driver, and uses the included official driver installer when installation or restoration is needed. Setup, the uninstaller and DLB's application files carry DLB Precision, LLC signatures and timestamps. It remains a prerelease for testing. No GitHub account is needed. Download only the setup EXE; the `.sha256` file on the [release page](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.4) is an optional checksum, not another installer.

1. Download the setup using the link above. If your browser says it **isn't commonly downloaded**, open its Downloads list and choose **Keep** / **Keep anyway**, if offered, for this DLB download.
2. Double-click the downloaded setup. If Windows says **Windows protected your PC** and describes an **unrecognized app**, choose **More info**, then **Run anyway**, only if you trust this official DLB pilot download.
3. Check that the Windows administrator prompt names **DLB Precision, LLC**, choose **Yes**, then follow setup. The single installer includes the widget, sensor service, application libraries, and signed PawnIO setup if needed; no separate hardware-monitoring program or driver download is required. A desktop shortcut is selected by default on fresh installations.

If setup requests a Windows restart before it can finish the driver step, restart
and run the same DLB installer again. Working shared PawnIO installations are
preserved. Setup does not automatically downgrade a detected newer shared driver.

These instructions apply to reputation warnings. If Windows names a virus or threat, or provides no **Run anyway** option, stop and send DLB the exact message. Keep Windows Security enabled.

A publisher signature identifies DLB; it does not guarantee immediate SmartScreen trust, so new downloads may still show reputation warnings. The bundled PawnIO installer retains its original signature. Broader clean-PC and hardware testing remain required before general customer distribution. [Microsoft explains these reputation warnings](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

The earlier [v0.1.0 pilot](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.0) remains available as an unsigned historical release. [v0.1.1](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.1) was the first publisher-signed pilot, [v0.1.2](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.2) added the widget size slider, and [v0.1.3](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.3) fixed clipped Settings text. These releases retain their original downloads and checksums.

Windows 11 already contains the .NET Framework runtime this app uses. Local builds place the installer under `artifacts/installer/`.

## Use

- Right-click the widget, open **Settings**, move the **Widget size** slider from **75% to 200%**, then click **Apply**. Sizing works in horizontal and vertical layouts, and the resulting dimensions are saved.
- For a custom shape, unlock position/size and drag any edge or corner. Applying unrelated settings preserves those custom dimensions unless you request a size or layout change.
- Drag the unlocked widget to the desired monitor. Position/size locking prevents dragging and edge/corner resizing.
- Right-click for settings, horizontal/vertical layout, position/size locking, recovery to the primary screen, or Exit.
- Default show/hide shortcut: **Ctrl+Alt+F10**. Change it in Settings by selecting the shortcut field and pressing a new combination, then Apply. Conflicts and reserved keys are rejected.
- One Celsius/Fahrenheit switch changes both temperatures.
- RAM shows physical memory used, with one decimal place and no total.
- Settings offers 1-second and 2-second refresh, 30–100% opacity, graphics-card selection, launch at sign-in, and small optional branding.
- Hiding the widget stops its sensor polling. Exit ends the widget; the sensor service remains idle until a widget requests readings.
- The tray icon recovers a hidden/locked widget. Reopening the app also restores the existing instance.

The monitor saves position, dimensions, layout, units, branding, opacity, GPU selection, and shortcut in `%LOCALAPPDATA%\DLBPrecision\Monitor\settings.json`. A disconnected display is handled by moving the widget into an available working area. Startup is a per-user Windows Run entry.

## Readings

CPU temperature is package/die temperature with a documented fallback for supported processors. CPU usage is Windows busy time across logical processors. CPU clock is the arithmetic mean of reported core clocks, not bus clock or a claim of maximum boost. GPU temperature, load, and clock refer to the selected GPU's core. Physical RAM used is total minus available, divided by 2^30 (Windows-style GB).

Different tools may use different sensor definitions or sampling intervals. Missing or invalid values display a dash; a stopped/unreachable service never leaves old values looking live. Hover or open Settings for sensor details. See `src/DlbPrecision.Sensors/README.md` for selection rules.

## Implementation

- .NET Framework 4.8, x64; custom-drawn Windows Forms surface, no browser engine.
- `DlbPrecision.Monitor`: per-user widget; never needs elevation for normal operation.
- `DlbPrecision.Service`: `DlbPrecisionSensors` Windows service, running as LocalSystem for sensor access.
- `DlbPrecision.Sensors`: embedded LibreHardwareMonitorLib 0.9.6 with only CPU/GPU monitoring enabled, plus native Windows CPU/RAM metrics. Library sensor history is disabled.
- `DlbPrecision.Shared`: small local output-only named-pipe protocol. The widget authenticates the server PID against SCM. No network connection, file path, or command is accepted from the widget. Several clients share at most one sample per second.

## Build and check

On a Windows development machine with a .NET SDK:

```powershell
dotnet build DlbPrecision.sln -c Release
.\scripts\Test.ps1
.\scripts\Test.ps1 -Integration # Requires installed sensor service
.\scripts\Test-Installer.ps1 # Compiled prerequisite scenarios; no driver changes
.\scripts\Build-Installer.ps1 -Version 0.1.4 -OutputDirectory .\artifacts\unsigned-0.1.4-attempt-1
```

The build script pins and verifies downloaded build tools and driver payloads. It includes dependency license notices, exact versions, hashes, and required source material. It does not install the app. The command above makes an **unsigned local build**; release signing is opt-in and requires DLB's approved signing profile. See [installer build instructions](installer/README.md) and [code-signing instructions](docs/CODE-SIGNING.md). Use a new output directory for each attempt; existing installer files and checksums are never overwritten.

Useful diagnostic commands:

```powershell
.\src\DlbPrecision.Probe\bin\Release\net48\DlbPrecision.Probe.exe --sample
.\src\DlbPrecision.Probe\bin\Release\net48\DlbPrecision.Probe.exe --report
.\src\DlbPrecision.Monitor\bin\Release\net48\DlbPrecision.Monitor.exe --render-preview .\artifacts\preview
```

Preview images are labeled SAMPLE DATA; they are appearance references, not live readings. An unelevated standalone probe can lack CPU temperature/clock even when the installed service has access.

For accessibility/UI testing only, `--test-window` exposes the otherwise taskbar-hidden widget to native test tools. Regular launches omit this switch. `--reset-position` recovers saved offscreen placement.

## Uninstall

Use Windows Settings > Apps > Installed apps > DLB Precision Monitor. The widget, service, and matching startup entries in loaded user profiles are removed. User display preferences remain for reinstall. Shared PawnIO is retained because other applications may use it; its separate removal is described in the installer notes.

## Validation status

See `docs/VALIDATION.md` for evidence and remaining pilot checks. Measurements describe the tested PC and workload; they are not universal performance guarantees.
