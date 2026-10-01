# DLB Precision Monitor

A compact Windows 11 hardware widget in DLBPrecision blue and purple. Designed to stay on another monitor while you play; it does not inject into games.

![Horizontal widget with sample readings](docs/images/widget-horizontal-sample.png)

Appearance preview with sample data. The widget also supports a resizable vertical layout.

## Install

**[Download DLB Precision Monitor for Windows 11](https://github.com/dlbprecision/dlb-precision-monitor/releases/download/v0.1.7/DLB-Precision-Monitor-0.1.7-Setup.exe)**

**v0.1.7 stops the box that flashed around the widget after clicking another window, makes the 1-second refresh update every second, and lets the widget size go from 50% to 150%.** It keeps the CPU sensor startup fix and bundled driver setup from previous releases. Setup, the uninstaller and DLB's application files carry DLB Precision, LLC signatures and timestamps. It remains a prerelease for testing. No GitHub account is needed. Download only the setup EXE; the `.sha256` file on the [release page](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.7) is an optional checksum, not another installer.

1. Download the setup using the link above. If your browser says it **isn't commonly downloaded**, open its Downloads list and choose **Keep** / **Keep anyway**, if offered, for this DLB download.
2. Double-click the downloaded setup. If Windows says **Windows protected your PC** and describes an **unrecognized app**, choose **More info**, then **Run anyway**, only if you trust this official DLB pilot download.
3. Check that the Windows administrator prompt names **DLB Precision, LLC**, choose **Yes**, then follow setup. The single installer includes the widget, sensor service, application libraries, and signed PawnIO setup if needed; no separate hardware-monitoring program or driver download is required. A desktop shortcut is selected by default on fresh installations.

**Updating from the monitor (v0.1.8 and later):** right-click the monitor, or
open **Settings**, and choose **Check for updates…**. If a newer version is
available, its notes are shown; choose **Update now**. The monitor downloads the
update, confirms it is genuinely signed by DLB Precision, LLC, and installs it.
If Windows asks for permission, choose **Yes**. The monitor closes and reopens on
the new version with your settings, size and position kept, including whether
it launches at sign-in. It checks only when you ask, and only then contacts
GitHub. If another Windows user on the same PC also has the monitor open, setup
closes their copy too; it reopens at their next sign-in or from the shortcut.

**Updating from v0.1.7 or earlier, or by hand:** download the current setup EXE
above, right-click the monitor and choose **Exit**, then run the new setup. There
is no need to uninstall first. Setup upgrades the existing installation and keeps
your saved settings. Open the monitor from its desktop shortcut afterward.

If setup requests a Windows restart before it can finish the driver step, restart
and run the same DLB installer again. Working shared PawnIO installations are
preserved. Setup does not automatically downgrade a detected newer shared driver.

These instructions apply to reputation warnings. If Windows names a virus or threat, or provides no **Run anyway** option, stop and send DLB the exact message. Keep Windows Security enabled.

A publisher signature identifies DLB; it does not guarantee immediate SmartScreen trust, so new downloads may still show reputation warnings. The bundled PawnIO installer retains its original signature. Broader clean-PC and hardware testing remain required before general customer distribution. [Microsoft explains these reputation warnings](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

The earlier [v0.1.0 pilot](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.0) remains available as an unsigned historical release. [v0.1.1](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.1) was the first publisher-signed pilot, [v0.1.2](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.2) added the widget size slider, and [v0.1.3](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.3) fixed clipped Settings text. These releases retain their original downloads and checksums.

Windows 11 already contains the .NET Framework runtime this app uses. Local builds place the installer under `artifacts/installer/`.

## Use

- Right-click the widget, open **Settings**, move the **Widget size** slider from **50% to 150%**, then click **Apply**. The whole widget scales evenly, including its borders and footer, in horizontal and vertical layouts, and the resulting dimensions are saved.
- For a custom shape, unlock position/size and drag any edge or corner. Applying unrelated settings preserves those custom dimensions unless you request a size or layout change.
- Drag the unlocked widget to the desired monitor. Position/size locking prevents dragging and edge/corner resizing.
- Right-click for settings, horizontal/vertical layout, position/size locking, recovery to the primary screen, or Exit.
- Default show/hide shortcut: **Ctrl+Alt+F10**. Change it in Settings by selecting the shortcut field and pressing a new combination, then Apply. Conflicts and reserved keys are rejected.
- One Celsius/Fahrenheit switch changes both temperatures.
- RAM shows physical memory used, with one decimal place and no total.
- Settings offers 1-second and 2-second refresh, 30–100% opacity, graphics-card selection, launch at sign-in, and small optional branding.
- Hiding the widget stops its sensor polling. Exit ends the widget; the sensor service remains idle until a widget requests readings.
- **Check for updates…** in the right-click menu or in Settings updates the monitor in place; see Updating above.
- The tray icon recovers a hidden/locked widget. Reopening the app shows the existing widget where you left it; use **Move to primary monitor** to bring it to the main display.
- Showing the widget with the shortcut, at sign-in, or by reopening the app never takes keyboard focus from a game.
- If something unexpected fails, the widget keeps running and records the details in `%LOCALAPPDATA%\DLBPrecision\Monitor\error.log`.

The monitor saves position, dimensions, layout, units, branding, opacity, GPU selection, and shortcut in `%LOCALAPPDATA%\DLBPrecision\Monitor\settings.json`. A disconnected display is handled by moving the widget into an available working area. Startup is a per-user Windows Run entry.

## Readings

CPU temperature is package/die temperature with a documented fallback for supported processors. CPU usage is Windows busy time across logical processors. CPU clock is the arithmetic mean of reported core clocks, not bus clock or a claim of maximum boost. GPU temperature, load, and clock refer to the selected GPU's core. Physical RAM used is total minus available, divided by 2^30 (Windows-style GB).

Different tools may use different sensor definitions or sampling intervals. Missing or invalid values display a dash; a stopped/unreachable service never leaves old values looking live. Hover or open Settings for sensor details. See `src/DlbPrecision.Sensors/README.md` for selection rules.

## Implementation

- .NET Framework 4.8, x64; custom-drawn Windows Forms surface, no browser engine.
- `DlbPrecision.Monitor`: per-user widget; never needs elevation for normal operation.
- `DlbPrecision.Service`: `DlbPrecisionSensors` Windows service, running as LocalSystem for sensor access.
- `DlbPrecision.Sensors`: embedded LibreHardwareMonitorLib 0.9.6 with only CPU/GPU monitoring enabled, plus native Windows CPU/RAM metrics. Library sensor history is disabled.
- `DlbPrecision.Updater`: separate updater, started only by **Check for updates…**. It makes one HTTPS request for this repository's GitHub **Latest** release, downloads the setup and its checksum, and runs setup only after verifying the checksum and that Windows trusts a timestamped DLB Precision, LLC signature issued through Microsoft's identity-verified signing chain. It never offers drafts, pre-releases or older versions. The widget and sensor service contain no network code.
- `DlbPrecision.Shared`: small local output-only named-pipe protocol. The widget authenticates the server PID against SCM. No network connection, file path, or command is accepted from the widget. Widgets that ask at nearly the same moment share one sample, and every 1-second refresh receives new readings.

## Build and check

On a Windows development machine with a .NET SDK:

```powershell
dotnet build DlbPrecision.sln -c Release
.\scripts\Test.ps1
.\scripts\Test.ps1 -Integration # Requires installed sensor service
.\scripts\Test-Installer.ps1 # Compiled prerequisite scenarios; no driver changes
.\scripts\Build-Installer.ps1 -Version 0.1.7 -OutputDirectory .\artifacts\unsigned-0.1.7-attempt-1
```

The build script pins and verifies downloaded build tools and driver payloads. It includes dependency license notices, exact versions, hashes, and required source material. It does not install the app. The command above makes an **unsigned local build**; release signing is opt-in and requires DLB's approved signing profile. See [installer build instructions](installer/README.md) and [code-signing instructions](docs/CODE-SIGNING.md). Use a new output directory for each attempt; existing installer files and checksums are never overwritten.

Useful diagnostic commands:

```powershell
.\src\DlbPrecision.Probe\bin\Release\net48\DlbPrecision.Probe.exe --sample
.\src\DlbPrecision.Probe\bin\Release\net48\DlbPrecision.Probe.exe --report
.\src\DlbPrecision.Monitor\bin\Release\net48\DlbPrecision.Monitor.exe --render-preview .\artifacts\preview
```

Preview images are labeled SAMPLE DATA; they are appearance references, not live readings. An unelevated standalone probe can lack CPU temperature/clock even when the installed service has access.

**Releasing an update:** in-app updates offer only the GitHub release marked
**Latest**. Publish a new version as a pre-release, test it, then mark it as the
latest release to offer it to everyone; marking it as a pre-release again stops
new in-app updates to it. To try the in-app update with a pre-release first, run
the installed updater with a test feed, for example
`"C:\Program Files\DLB Precision Monitor\DlbPrecision.Updater.exe" --feed https://api.github.com/repos/dlbprecision/dlb-precision-monitor/releases/tags/v0.1.9`.
A feed can only choose which release to offer; the same version and signature
checks apply. `DlbPrecision.Updater.exe --render-preview <folder>` saves pictures
of each updater screen.

For accessibility/UI testing only, `--test-window` exposes the otherwise taskbar-hidden widget to native test tools. Regular launches omit this switch. `--reset-position` recovers saved offscreen placement.

## Uninstall

Use Windows Settings > Apps > Installed apps > DLB Precision Monitor. The widget, service, and matching startup entries in loaded user profiles are removed. User display preferences remain for reinstall. Shared PawnIO is retained because other applications may use it; its separate removal is described in the installer notes.

## Validation status

See `docs/VALIDATION.md` for evidence and remaining pilot checks. Measurements describe the tested PC and workload; they are not universal performance guarantees.
