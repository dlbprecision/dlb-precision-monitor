# Pilot validation

Baseline testing: 2026-09-07. Signed v0.1.1 pilot verification: 2026-09-08.
Dates use America/Chicago.

Test machine: Windows 11 Pro x64, Ryzen 9 9950X3D, NVIDIA GeForce RTX 5090, approximately 96 GB physical RAM. Existing signed PawnIO 2.2.0 was preserved.

## September 9 startup follow-up

After a real restart, the installed v0.1.3 sensor service was Automatic and
Running, but the widget had not opened. Windows' startup enumeration
(`Win32_StartupCommand`) did not contain DLB. The Windows registry provider
(`StdRegProv`) also reported the DLB Run value missing, although direct registry
reads from the earlier execution environment reported it present. Those earlier
registry-only checks did not establish a working Windows startup registration;
the startup claims below must be read with this correction.

The current user's Run entry was repaired through the Windows registry provider,
pointing to the quoted installed monitor executable. Both a provider read-back
and Windows' startup enumeration then confirmed the exact command. The installed
widget also opened normally and remained responding. No application binary,
service configuration, or display preference was changed for this repair.

Future startup validation must check Windows' own startup enumeration as well as
the application's setting. Actual automatic launch after the repair still needs
confirmation at the next sign-in; manually launching the widget does not prove
the sign-in trigger. This local repair does not establish a normal clean-install
startup result for other PCs.

## Baseline completed on September 7

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

## Signed v0.1.1 pilot verified on September 8

The first publisher-signed pilot installer is
`DLB-Precision-Monitor-0.1.1-Setup.exe`, 7,429,968 bytes. Its SHA256 is:

```text
5dc174d7af619dd4fc6ee45ae610916d6112f067bd617d57244008a4188b986a
```

- Source builds completed with zero warnings/errors. Prebuild checks passed all
  18 codec/client, 15 sensor-selection and 25 widget assertions.
- Setup and the temporary uninstaller embedded by Inno passed build-time
  signature verification with zero warnings/errors.
- The four installed first-party files (`DlbPrecision.Monitor.exe`,
  `DlbPrecision.Service.exe`, `DlbPrecision.Shared.dll` and
  `DlbPrecision.Sensors.dll`) report version 0.1.1.0. Those files and the installed
  `unins000.exe` passed SignTool and Authenticode verification: Valid signatures,
  the exact publisher **DLB Precision, LLC**, and timestamps.
- All 19 installed runtime hash checks passed. Installed first-party files match
  the signed staging files. The original PawnIO payload still matches its pinned
  vendor-manifest hash.
- An upgrade from the local unsigned v0.1.0 build containing the Settings Close
  fix to signed v0.1.1 completed with exit code 0 and no restart required. Setup
  used `/SILENT` from a non-elevated parent. All four service-configuration
  commands returned 0; `DlbPrecisionSensors` was running with Automatic startup.
- All 22 codec/client and installed-service integration assertions passed. The
  installed service returned all seven readings with `Status=ok` and no warnings
  on the same Ryzen 9 9950X3D / RTX 5090 test PC.
- All 25 installed-widget smoke assertions passed, including actual modeless
  Settings Close and disposal behavior.
- Restart Manager closed the previous widget during upgrade. The existing
  desktop shortcut still targeted the installed monitor without arguments and
  launched the responding v0.1.1 application. The upgrade preserved the previous
  desktop-task choice; it did not create a new public-desktop shortcut. The fresh
  installation desktop-shortcut default is confirmed in installer source, not
  by a clean-install test.
- The settings file's SHA256 was unchanged after upgrade. The existing user's
  startup command was preserved and still targeted the installed application;
  the log contained no startup-fallback warning. This is preservation evidence,
  not a fresh startup-configuration test.
- The signed-pilot installation notes were installed correctly.

This verifies that specific upgrade path on a PC with PawnIO already installed.
It does not establish clean installation, normal double-click setup, reboot or
uninstall behavior. A valid publisher signature identifies DLB; it does not
guarantee immediate SmartScreen reputation. The historical v0.1.0 release remains
unsigned with its original assets and checksum.

## September 8 v0.1.3 performance

A fresh 120.81-second desktop measurement includes the installed v0.1.3 widget
and its LocalSystem sensor service, using the current horizontal layout and
1-second refresh. It collected 115 samples after a 5-second warmup. The user's
settings file was unchanged through the measurement.

| Process | Average CPU | Average private resident RAM |
| --- | ---: | ---: |
| Widget | 0.0097% | 15.83 MiB |
| Sensor service | 0.0259% | 33.59 MiB |
| Combined | **0.0356%** | **49.42 MiB** |

The largest combined CPU sample was **0.1398%**; peak private resident RAM was
**52.36 MiB**. Combined private committed memory averaged 83.39 MiB (peak 86.22),
and summed working set averaged 117.83 MiB (peak 120.76). Private committed
memory is not necessarily all resident in RAM; working set includes shared
pages and summing it can double-count them.

The corrected measurement script uses
`Win32_PerfRawData_PerfProc_Process` counters for both processes. CPU percentages
normalize across all 32 logical processors. Whole-window CPU counter deltas
were checked against interval-weighted averages; both processes contributed
measurable CPU time. Missing counters or changed process identities fail the
measurement instead of silently being counted as zero. Samples are roughly one
second apart, so shorter CPU peaks can be missed.

These results cover the two application processes on the test PC during normal
desktop use. Separate System/driver activity is not attributed to the app. This
is not a gaming FPS test or a whole-system overhead measurement. Raw evidence:
[performance-v0.1.3-1s-20260908.json](validation/performance-v0.1.3-1s-20260908.json).

## September 7 baseline performance (CPU results superseded)

The earlier collector could not read the LocalSystem service's CPU-time property
without elevation. PowerShell returned an empty value that the script silently
treated as zero. The old combined CPU figures, including the reported 0.0129%
average and 0.1455% peak at 1-second refresh, are therefore unreliable. Use the
corrected v0.1.3 measurement above; the difference is not evidence of an app
performance regression.

The original [performance-1s.json](validation/performance-1s.json) is preserved.
Its separately collected memory counters averaged 77.76 MiB of private committed
memory and 112.61 MiB of summed working set, with peaks of 81.57 and 116.68 MiB.
The historical `privateMiB` field means private committed memory, not private
resident RAM.

A diagnostic 2-second run kept Settings open because a Close-button bug was
discovered. It also used the faulty CPU collector and is not a valid CPU or
like-for-like refresh comparison. The original
[performance-2s-settings-open.json](validation/performance-2s-settings-open.json)
is preserved. The Close bug and resource disposal were fixed and regression
tested in v0.1.1; a fresh 2-second measurement remains pending.

## Remaining before customer release

- Test clean Windows 11 installation where PawnIO was not already present, normal double-click setup/startup, restart/sign-in, repair, and uninstall. The specific unsigned v0.1.0-to-signed-v0.1.1 upgrade above passed; other installation states still need coverage.
- Validate other Intel/AMD CPUs and AMD/Intel GPUs, multi-GPU systems, and laptops as applicable to pilot PCs. Current sensor compatibility evidence covers only this machine.
- Physically test mixed-DPI/multiple-display movement, monitor disconnect/reconnect, sleep/resume, and game-focus shortcut behavior on the pilot setups.
- Run iRacing, ACC, and COD with the widget on the other display. No title-specific gaming session or FPS-impact test has been performed.

Same-screen exclusive-fullscreen rendering was explicitly excluded by the user's final second-monitor clarification. There is no missing game-overlay engine in this scope.

## Signed v0.1.2 resizing pilot verified on September 8

`DLB-Precision-Monitor-0.1.2-Setup.exe` is 7,430,184 bytes. Its SHA256 is:

```text
60a1d8f55bdcac79a635e2eef8c9661553e31107761d2ddd482108c079670516
```

- The build completed with zero warnings/errors. Prebuild checks passed all
  18 client and 15 sensor assertions.
- Setup, the temporary uninstaller embedded by Inno, and the four first-party
  DLB files passed build-time signature and timestamp verification with zero
  warnings/errors.
- Installed `DlbPrecision.Monitor.exe`, `DlbPrecision.Service.exe`,
  `DlbPrecision.Shared.dll` and `DlbPrecision.Sensors.dll` report version
  0.1.2.0. Those files and installed `unins000.exe` had Valid signatures, the
  exact publisher **DLB Precision, LLC**, and timestamps.
- All 19 installed runtime hashes matched the staged files and manifest. The
  builder verified that PawnIO still matched its unchanged pinned vendor hash.
- The upgrade from signed v0.1.1 used `/SILENT` setup from a non-elevated parent
  and finished with exit code 0 without a reboot. All four service-configuration
  commands returned 0. `DlbPrecisionSensors` was running with Automatic startup.
- The actual user's settings-file hash and existing startup command were
  unchanged through the upgrade. The existing desktop shortcut launched the
  installed v0.1.2 monitor. This confirms preservation of existing configuration,
  not new-user startup or fresh-install shortcut creation.
- All 40 installed-widget assertions passed. Coverage includes compact and enlarged
  sizes, both orientations, DPI scaling calculations, compact-layout round
  trips, actual Settings slider callbacks and repeated Apply behavior,
  preservation of custom size when unrelated settings are applied, temporary
  dimension-persistence checks, and a small Settings window with vertical
  scrolling, fixed Close controls and no horizontal overflow.
- All 22 live client/integration assertions passed. The installed service
  supplied all seven readings with `Status=ok` and no warnings on the same
  Ryzen 9 9950X3D / RTX 5090 test machine.
- Horizontal and vertical renders at 75%, default and large sizes were visually
  checked and readable. An isolated 490-by-450 Settings-window check verified
  the fixed footer and vertical scrolling without horizontal overflow.

The Settings control is labeled **Widget size**; its 75%-to-200% selection is
applied with **Apply**, fits the current display and saves the resulting
dimensions. An unlocked widget shows a resize grip. The native `WS_THICKFRAME`
style and edge/corner hit-test implementation were reviewed in code. Actual
mouse edge/corner dragging and physical high-DPI behavior have not been manually
tested; calculation checks and isolated renders do not establish those results.

This verifies the signed v0.1.1-to-v0.1.2 upgrade on a PC with PawnIO already
installed. Clean-PC installation without PawnIO, ordinary double-click setup,
reboot/sign-in, repair, uninstall, other hardware, physical mixed-DPI displays,
sleep/resume and actual game sessions remain pending as described above. No new
performance measurement is claimed for v0.1.2; the September 7 figures and prior
release evidence are preserved as historical results.

## Signed v0.1.3 Settings fix verified on September 8

`DLB-Precision-Monitor-0.1.3-Setup.exe` is 7,430,472 bytes. SHA256:

```text
b9ddb9bdb4af1c66ef856a07f7d072aca396923b3c12477cd3aa2864a581e27b
```

- The reported four-line sensor-details text was reproduced in an isolated
  Settings window. The label now uses its full preferred height (60 pixels in
  the 96-DPI check), displaying the final resizing instruction without clipping.
- Long wrapped details grew to 150 pixels and remained reachable in the
  scrolling content area of a 490-by-450 window. Apply/Close stayed fixed and
  accessible; no horizontal scrollbar appeared. Both cases were visually checked.
- Build completed with zero warnings/errors. Setup, its embedded uninstaller
  and first-party files were signed and timestamped. All four installed DLB files
  report version 0.1.3.0; those files and `unins000.exe` passed exact publisher
  verification for **DLB Precision, LLC**. All 19 installed runtime hashes passed.
- The automated signed v0.1.2-to-v0.1.3 upgrade exited 0 without a reboot. The
  sensor service remained Automatic and Running. User settings and startup
  registration were preserved, and the desktop shortcut relaunched the app.
- All 40 installed-widget checks and 22 installed-service/client checks passed,
  with all seven live readings available and no sensor warnings.

The fresh v0.1.3 desktop performance measurement is documented above. Outstanding
clean-PC, physical mixed-DPI, hardware and game coverage remains unchanged.
