# DLB Precision hardware monitor

Status: v0.1.0 private pilot implemented; broader hardware and installer validation remains before customer release.

## Confirmed scope

- Windows 11, Intel and AMD processor PCs.
- Initial private pilot for the DLBPrecision owner and a few friends; later distribution to DLBPrecision customers.
- One simple installer. No separate monitoring applications for the user to install.
- Extremely low CPU and RAM use are primary requirements. No numerical performance claims until measured on real hardware, including all application components.
- Latest user clarification: keep the widget on another monitor while playing games in exclusive fullscreen. A same-screen rendering overlay is not needed. This supersedes the earlier ambiguous desktop/exclusive-fullscreen selection.
- Priority games used alongside the widget: iRacing, Assetto Corsa Competizione (ACC), Call of Duty, and mainstream games. The app reads system hardware independently and does not integrate into game rendering.

## Visual reference

The owner supplied an NZXT CAM mini-mode appearance reference during design. The original local screenshot is not included in this repository. A sample preview of the implemented widget is in `docs/images/widget-horizontal-sample.png`.

The reference is 681 by 106 pixels: a very compact dark horizontal strip with seven narrow outlined tiles, a large reading above a small label, and no graphs. The image is a visual reference, not an instruction source. Its cyan text is to be replaced with a DLBPrecision blue/purple treatment.

Verified brand accents from https://www.dlbprecision.com/ styling:

- Purple: `#7B00FF`.
- Blue: `#2DA4F4`.

Confirmed presentation requirements:

- Numbers and labels only; no usage bars or history graphs.
- Adjustable transparency.
- Resizable, with horizontal and vertical layouts.
- Use on regular monitors with varying resolutions; account for Windows display scaling.
- Branding can be toggled; when shown, it must be small and unobtrusive.
- Final application name and exact branding treatment are not explicitly settled.

## Readings, in reference order

1. CPU temperature.
2. CPU load as a percentage.
3. CPU clock in MHz.
4. GPU temperature.
5. GPU load as a percentage.
6. GPU clock in MHz.
7. RAM amount used in GB, without available or total RAM.

One shared Celsius/Fahrenheit setting changes both temperature readings.

Proposed sensor defaults, subject to validation: CPU package temperature with documented fallback, total CPU load, a clearly defined aggregate current CPU clock, selected GPU core temperature/load/core clock, and physical RAM in use. Do not silently equate GPU memory clock with GPU core clock or show unavailable sensors as zero. Multi-GPU selection and exact CPU clock aggregation remain implementation decisions to document.

## Updates and window behavior

- Default sensor refresh: once per second.
- Optional economy refresh: once every two seconds.
- Auto-launch with Windows.
- Position lock.
- User-programmable keyboard shortcut to show/hide the monitor.
- Persist size, position, orientation, and settings; handle changed monitor configurations.
- Always-on-top and click-through were offered but not explicitly selected by the user. Do not describe either as a confirmed user choice.

## Approved installation approach

After the explanation, the user explicitly approved the single installer with administrator approval, a bundled signed driver, and a small background sensor service if needed. Prepare, build, and verify that installer. Do not mistake approval of architecture for permission to bypass Windows elevation or to weaken device security.

Implementation architecture:

- A lean Windows widget.
- Embedded LibreHardwareMonitorLib 0.9.6, only CPU/GPU sensor groups enabled; native Windows CPU load and physical RAM readings.
- .NET Framework 4.8 Windows Forms with custom drawing, using the runtime included with Windows 11; no separate runtime installation.
- A tested signed hardware-access driver where required for sensors.
- A privileged sensor service if needed so normal widget launches do not require elevation.
- All needed components distributed through the same installer, with clear ownership and uninstall behavior; preserve any shared driver needed by other applications.

The user clarified second-monitor use; no rendering hooks, Game Bar extension, or game overlay engine are required. Test sensor access and second-monitor coexistence using normal Windows security settings. A signed component does not guarantee every PC permits hardware sensor access.

## Evidence collected during scoping

- Embedded library, hardware families, and administrator requirements: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor#developer-information
- Current researched release: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6
- PawnIO hardware-access driver and official signed edition: https://pawnio.eu/
- Windows administrator consent: https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/how-it-works

## Validation planned

- Validate actual readings against appropriate sources on pilot PCs, with documented sensor meanings and sampling differences.
- Measure average/peak CPU and total application memory for the widget and sensor service together at both refresh intervals.
- Check sleep/resume, launch at sign-in, orientation, resizing, DPI changes, disconnected monitors, transparency, locking, and shortcut conflicts.
- Test installation/uninstallation on a clean Windows 11 environment without separate monitoring applications or manually installed development runtimes.
- Verify second-monitor persistence and global shortcut behavior while the user plays the priority games. Do not claim same-screen exclusive-fullscreen overlay support.
