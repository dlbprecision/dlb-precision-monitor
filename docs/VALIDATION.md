# Pilot validation

Date: 2026-09-07 (America/Chicago).

Test machine: Windows 11 Pro x64, Ryzen 9 9950X3D, NVIDIA GeForce RTX 5090, approximately 96 GB physical RAM. Existing signed PawnIO 2.2.0 was preserved.

## Completed

- All six projects build in Release with zero warnings/errors.
- 15 sensor selection assertions: package/core selection, clock/load aggregation, invalid values, zero-temperature placeholder rejection, and GPU core-vs-memory distinctions.
- 25 widget smoke assertions: C/F conversion, missing/invalid readings, GPU choice/fallback, settings persistence/corruption, monitor recovery, reserved/invalid shortcuts, minimum rendering, and real modeless Settings close/disposal behavior.
- 18 codec/client assertions, plus 4 installed-service integration assertions: bounded JSON, protocol, stale/future values, cancellation, missing service, untrusted pipe server rejection, explicit diagnostic bypass, live sensor delivery.
- Sample horizontal, vertical, minimum, 150% DPI, Fahrenheit, unavailable, and settings images rendered and visually inspected.
- Actual Windows UI: opened settings, applied vertical layout, hid and restored the widget using Ctrl+Alt+F10. Diagnostic `--test-window` was used so the native test tool could discover a normally taskbar-hidden widget.
- Single 7 MB-class setup compiled with pinned/signed vendor payload and verified x64 sensor implementation. Third-party notices and provenance included.
- Installed in protected Program Files; LocalSystem DlbPrecisionSensors service registered, auto-start enabled, and running.
- Production pipe-server authentication succeeded for the installed service.
- All seven real readings returned with `Status=ok` and no warnings. Example during test: CPU 53.4 C, 3.4%, 5617 MHz; GPU 38.7 C, 20%, 2407 MHz; RAM 21.3 GB. These are changing measurements, not fixed expected values.
- Startup command configured successfully for the normal signed-in user, pointing at the installed widget.

The automated installation was launched already elevated for testing. Its documented original-user startup fallback was exercised: setup logged that startup could not be enabled in that launch context, then the normal-user helper successfully enabled it with exit code 0. This is not evidence that the ordinary double-click install path needs a second action; verify that path separately.

## Performance

Measurements include the installed service and visible widget together. CPU percentages normalize across all 32 logical processors. Memory reports both summed working set (which can double-count shared pages) and private bytes. Peak CPU is the largest one-second sample, not a sub-second trace.

At 1-second refresh, a 60-second desktop run averaged 0.0129% total CPU (largest one-second sample 0.1455%), 77.76 MiB private memory, and 112.61 MiB summed working set. Peak private memory was 81.57 MiB; peak working set was 116.68 MiB. Evidence: [performance-1s.json](validation/performance-1s.json).

A diagnostic 2-second run kept the Settings window open because a live-test Close-button bug was discovered. Its higher memory/CPU is not a like-for-like refresh comparison. That run is preserved as [performance-2s-settings-open.json](validation/performance-2s-settings-open.json). The Close bug and resource disposal have been fixed and regression tested; a fresh economy run is pending installation of that update.

## Remaining before customer release

- Test clean Windows 11 installation where PawnIO was not already present, normal double-click setup/startup, restart/sign-in, repair/upgrade, and uninstall.
- Validate other Intel/AMD CPUs and AMD/Intel GPUs, multi-GPU systems, and laptops as applicable to pilot PCs. Current sensor compatibility evidence covers only this machine.
- Physically test mixed-DPI/multiple-display movement, monitor disconnect/reconnect, sleep/resume, and game-focus shortcut behavior on the pilot setups.
- Run iRacing, ACC, and COD with the widget on the other display. No title-specific gaming session or FPS-impact test has been performed.
- Obtain DLB publisher signing before a customer release; the pilot DLB binaries are unsigned. Never substitute silent trust-certificate installation or weaker Windows security settings.

Same-screen exclusive-fullscreen rendering was explicitly excluded by the user's final second-monitor clarification. There is no missing game-overlay engine in this scope.
