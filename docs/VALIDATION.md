# Pilot validation

Baseline testing: 2026-09-07. Signed v0.1.1 pilot verification: 2026-09-08.
Dates use America/Chicago.

Test machine: Windows 11 Pro x64, Ryzen 9 9950X3D, NVIDIA GeForce RTX 5090, approximately 96 GB physical RAM. Existing signed PawnIO 2.2.0 was preserved.

## October 2 v0.1.9 updater hardening

v0.1.9 fixes every finding of the independent review of v0.1.8 and is meant to
be the first release marked **Latest**, so every v0.1.8 copy takes its first
in-app update to it.

- **Signed release candidate**, built from commit `c416bbc`
  (`release-0.1.9-signed-attempt-9`): **7,475,512 bytes**, SHA256
  `cf538c2df9be4ce09c37ef46748d48c00189a165ba40ad9fdf6feef4c7e12944`. Setup, its
  uninstaller and the five DLB binaries carry valid, timestamped **DLB Precision,
  LLC** signatures, and the binaries record `0.1.9+c416bbc…`. The build's own
  in-app updater check accepted it. A signed test build versioned **0.1.8.9**
  (SHA256 `ac98610c…41e07c6`) was built from the same commit and never published.
  Attempts 1 to 8 (`9fc4761`, `65496db`, `8ce5e7f`, `5eeb15a`, `e7c00f6`,
  `347c370`, `3333cfb`, `c7ab40f`) predate the last review fixes and must never
  be published.
- **Checks:** **224** client/startup/updater/integration assertions (including
  live verification: the genuine signed v0.1.8 setup is accepted, while the
  installed monitor program and uninstaller renamed as a setup are refused),
  **15** sensor-selection, **64** widget and **130** updater smoke checks. Zero
  warnings. Each new test for a fixed bug was confirmed to fail without its
  fix (Enter in the shown window, Settings at 150%, the Settings heading, GPU
  driver installs, CPU retries behind a GPU wait, a bad CPU sample during a
  driver install, reopens closer than 10 s, a window minimized during the
  install, Settings leaving always-on-top, a card that drops out while another
  card's retries run, faults that keep returning, a failed read, one-sample
  dropouts, a sensor reader that can't be rebuilt, a false "will retry", a slow
  reopen, readings blank on every other sample, two separate dropouts a few
  seconds apart, dropouts around a pause in sampling, a blip just after a reopen,
  a dropout soon after a flicker, and a day of random one-sample dropouts, which
  reopened sensors 13 times with attempt 7 and once with this one). The
  integration suite passed 40 consecutive runs on the final commit.
- **Randomized checks of sensor recovery**, one sample a second for an hour per
  scenario: **30,000** scenarios on the final commit (CPU outages, graphics-driver
  installs, unplugged, broken and flickering cards, faults that a reopen heals,
  failed reads, up to three cards) found no problems; reopens were never closer
  than 10 s apart. Earlier versions of the fix failed the same checks (73 lost
  cards in one; 1,046 problems in 3,000 multi-card scenarios in another).
- **Display scaling**, measured by running the real windows in a process with the
  app's own per-monitor DPI settings and WinForms set to 150%: v0.1.8 cut off
  **12** Settings labels and **2** updater texts (including **Update now**);
  v0.1.9 cut off **none**, also after a simulated move between displays.
- **End to end on the office PC** with the signed release candidate, driving the
  real windows through UI Automation:
  - **Settings → Check for updates** with "Keep above desktop windows" on. On
    v0.1.8 the updater was the active window but **hidden behind Settings**, and a
    second click did not raise it. On v0.1.8.9 (the v0.1.9 code) it opened
    **on top**, centred, stayed on top after a second click, and Settings left
    always-on-top.
  - **Update v0.1.8 → v0.1.9** with the installed **v0.1.8 updater** (what every
    customer runs) through a local feed describing the release candidate: offered,
    verified, installed and reopened. The release notes read correctly in the
    v0.1.8 window. Every binary reports **0.1.9.0** with valid signatures, the
    service and PawnIO were running, the settings file was byte-for-byte unchanged,
    launch at sign-in stayed off as set beforehand (then restored), no Public
    desktop shortcut was created, no failure log was kept, and the v0.1.8 copy's
    temporary folder was removed by the new v0.1.9 updater (the `--cleanup`
    contract).
  - **Update v0.1.8.9 → v0.1.9** with the new updater: the same results, with the
    stricter publisher (Arkansas, US) and setup-identity checks passing on the
    real signature. The window left always-on-top while setup ran and returned
    to it for the result; minimized during the install, it came back at full
    size, centred.
  - The live v0.1.9 sensor service reported all seven readings with status
    `ok` and no warnings.
  - These runs were repeated on every signed attempt except attempt 8, which was
    superseded before testing; attempt 9 matched attempt 6 line for line.
    Attempts 5 to 9 changed only sensor recovery, so the display-scaling and
    window checks above, made on attempt 4, still apply.
  - The published v0.1.8 setup was then reinstalled (its updater matches the
    released file, SHA256 `af95933c…`), leaving the PC on v0.1.8 for the real
    GitHub update at go-live.
- **Release tooling:** `Build-Installer.ps1 -Sign` refuses uncommitted source,
  binaries built from another commit (including through `-SkipBuild`) and
  four-part versions without `-TestBuild`. `Check-LatestOffer.ps1 -AsIfLatest`,
  run with the installed v0.1.8 updater against the real published v0.1.8
  pre-release, passed both the offer check and verification after a real
  download.

Seven rounds of independent multi-agent review checked the changes, plus three
narrow reviews of the last fixes. The first raised 15 low-severity findings;
the second found one medium regression in those fixes (the minimized window) and
eight low ones; the third found that signed builds could still reuse uncommitted
outputs and that the CPU retry change could spend GPU retries. The fourth to
seventh concentrated on sensor recovery: a stopped graphics card was not retried
once an unreadable CPU had used its retries; all cards shared one retry budget,
one good reading refilled a budget, and one failed read forgot every card;
one-sample dropouts armed retries; and the next retry was timed from the start of
a slow reopen, while readings blank on every other sample were never retried.
The first narrow review found that the fix for flickering readings counted
separate dropouts a few seconds apart as one fault, so random dropouts reopened
sensors many times a day; the second found two smaller cases of the same kind
(a blip just after a reopen, and a dropout soon after a flicker); the third, on
the final code, found nothing further. Everything they confirmed is fixed here.

Known limitations of graphics-card recovery, left for a later release:

- Under LibreHardwareMonitor 0.9.6, NVIDIA and AMD cards keep their last values
  when the vendor API fails, so after an in-place driver update (no restart) a
  card's readings may freeze instead of going blank, and recovery cannot notice.
  A restart of Windows or of the DLB sensor service clears it.
- NVIDIA and AMD cards are identified by enumeration slot, so with two cards of
  one brand a missing card can shift the other's slot; two identical Intel Arc
  cards share one id.
- A second blank-out of the same card within 5 minutes of it recovering
  continues the earlier retry schedule (longer waits, or the "restart the
  service" message) instead of starting a fresh one. This is what keeps a
  recurring fault from reopening sensors without end.

Not yet covered: a PC with default UAC (the prompt on the secure desktop, and
declining it), physical 125%/150% and mixed-DPI displays, a PC where another
Windows user has the widget open, Smart App Control or third-party antivirus,
setup's new exit codes 7 and 21 produced by a real failure, and the in-app update
offered by the real GitHub **Latest** release, which is the go-live step.

## September 30 v0.1.8 in-app updater

v0.1.8 adds **Check for updates** (Settings and the right-click/tray menu). A
separate signed `DlbPrecision.Updater.exe` does the check, download,
verification and install, so the widget loads no network code.

- **Signed release candidate.** **7,464,112 bytes**, SHA256
  `2eb6005d256f8b113bf1bf5079d46fa67e195cf16e25571893bc7072149c3657`
  (`release-0.1.8-signed-attempt-2`). Setup, its uninstaller and the five DLB
  binaries, now including the updater, carry valid, timestamped **DLB Precision,
  LLC** signatures. A signed local test build versioned **0.1.7.9** was built from
  the same commit and never published.
- **Checks:** **144** client/startup/updater/integration assertions (including
  live verification of the installed DLB files and a tampered copy), **15**
  sensor-selection, **52** widget and **14** updater smoke checks. Zero warnings.
- **End to end on the office PC**, driving the installed updater's window through
  UI Automation:
  - **Real GitHub feed** with no Latest release yet: "You're up to date"; the
    updater ran from its private `%TEMP%\DLBPrecision-Update-…` copy, and the
    folder was removed when it closed.
  - **Unreachable server** (`https://127.0.0.1:9/`): a friendly network message and
    a working **Try again**.
  - **Tampered installer** with a matching checksum: refused with
    `0x80096010` (bad digest). No setup process started, the installed version and
    the running widget were untouched.
  - **Window closed with X** while an update was offered: the temporary folder was
    still removed.
  - **Update 0.1.7.9 → 0.1.8** through a local feed describing the release
    candidate: screens went verifying, installing, "installed and reopened"; the
    widget reopened at its saved 682×101 position, on top. All installed binaries
    report **0.1.8.0** with valid signatures, the service and PawnIO were running,
    and the settings file was byte-for-byte unchanged. Launch at sign-in, turned
    off with the monitor's own switch beforehand, **stayed off** (then restored to
    the original entry), and no Public desktop shortcut was created. While setup
    ran, the installer could not be opened for writing or deleted. No temporary
    folder or failure log remained.
  - After the update the widget had keyboard focus. Windows activates the
    remaining top-level window when the updater window closes; the updater runs
    only when someone clicks it, so this does not affect gaming. Startup and
    shortcut shows were verified not to take focus in v0.1.7.
- **Resource use on 0.1.8**: widget about **0.31%** of one logical processor and
  **31.7 MB** private memory with no network modules loaded and no updater
  process while idle; service about **1.5%** and **48.7 MB**.

Plan task 1 checked how setup behaves when started the way the updater starts it:
not elevated, with `/SILENT /SUPPRESSMSGBOXES /NOCANCEL /NORESTART
/RESTARTEXITCODE=3010 /DLBUPDATE=1`. The office PC's User Account Control is set to
elevate administrators **without prompting**, so no Windows prompt appeared: setup
reinstalled v0.1.7 and returned exit code **0** after 1.5 seconds, after its log
recorded the completed installation. This shows the non-elevated setup process
waits for the elevated one and returns its result. Inno Setup does not document
the exit code for a declined prompt, so the updater judges "cancelled" by what
changed (installed version unchanged and the widget never closed) rather than by
one code. The decline path still needs a check on a PC with default UAC settings, as does
an in-app update offered by the real GitHub feed once a release newer than 0.1.8
is marked **Latest**.

A separate review agent found that publisher checks were not yet bound to the
certificate Windows verified, that silent updates would re-apply first-install
tasks (re-enabling launch at sign-in), and smaller hardening items. All were
fixed before building a release candidate: one verified signer, a trusted chain to
the pinned Microsoft root, `/MERGETASKS` from the person's current choices,
bounded release-note formatting, network-share refusal, temporary-folder cleanup
and full fake-setup tests.

## September 30 v0.1.7 review fixes

Code-review fixes, first merged from branch `fix/widget-review-fixes`, then
built, signed and upgrade-tested on the office PC as v0.1.7.

- **Signed release.** The publisher-signed installer is **7.09 MiB**, SHA256
  `ef0f4eff560a28b7274e7c91f30fa9b9721dbdbcf0ce9267ea83b140696f504e`
  (`release-0.1.7-signed-attempt-2`). Setup, its embedded uninstaller and the
  four DLB binaries (version **0.1.7.0**) carry valid, timestamped **DLB
  Precision, LLC** signatures, and the checksum sidecar matches.
- **Upgrade.** The first signed build upgraded the office PC from v0.1.6 with
  exit code **0** and no restart: the settings-file hash and Windows startup
  entry were unchanged, the running PawnIO driver and its demand-start
  configuration were preserved, and the service returned to Running. The
  corrected second build then installed over it with exit code **0**; the
  installed widget's hash matched the signed stage.
- **Focus finding in the first build.** Its widget still took focus when shown,
  because WinForms applies the `TopMost` property with a window move that
  activates the window, which overrides `ShowWithoutActivation`. A two-window
  test reproduced it (first show, re-show and stay-on-top toggle all took focus)
  and showed that the `WS_EX_TOPMOST` creation style with a non-activating
  `SetWindowPos` toggle does not. The first build was not published. With the
  corrected build installed, the widget stayed on top and never took focus at
  launch, when hidden and shown through its shortcut message, or when the app
  was opened again; it remained at its saved 682×101 position on the portrait
  display.
- **Installed checks.** **51** client/startup/integration assertions with live
  service readings, **15** sensor-selection and **47** installed-widget checks
  passed. On the installed service, **18 of 18** polls from a 1-second WinForms
  timer received new readings. Warmed-up service CPU with 1-second polling was
  **1.25–1.64%** of one logical processor (under **0.06%** of all 32), private
  memory about **48 MB**; the widget used about **0.36%** and **31 MB**. The
  first window after the installer restarted the service measured **2.5%**,
  which includes one-time sensor initialization.

- **Refresh timing.** A fresh sensor sample took about **51 ms** and the widget's
  1-second WinForms timer fired every **992–1005 ms**. The service started its
  1-second reuse window after sampling finished, so every other 1-second poll
  received the previous sample: "Every second" refreshed every 2 seconds. The
  window now starts with each sample and lasts 750 ms. Measured v0.1.6 baseline
  (effectively 2-second sampling): widget about **0.5%** of one logical
  processor and **36 MB** private memory; service about **0.6%** and **51 MB**.
  True 1-second sampling is expected to roughly double the service's sampling
  cost; this has not yet been measured on an installed build.
- **Border flash after clicking elsewhere.** An on-screen test window with the
  widget's borderless, resizable style showed **5,544** off-color edge pixels
  after losing focus. With `WM_NCACTIVATE` passed to Windows with `lParam = -1`
  it showed **0**.
- **Window behavior.** The widget shows without taking focus (shortcut, sign-in,
  reopening), reopening the app no longer moves it to the main display, and it
  is created at its saved position with that display's DPI.
- **Settings.** A shortcut owned by another app no longer blocks other
  preferences from saving; the open Settings window follows layout and lock
  changes made from the right-click menu; settings missing from an older file
  load their defaults.
- **Size slider.** The range is now **50–150%** (was 75–200%), and borders, gaps
  and the footer scale with the widget. Renders were checked at 150, 100, 75, 65,
  60, 55, 50 and 45% horizontal and 100, 75, 60 and 50% vertical; 50% is the
  smallest size with clean readings.
- **Recovery and diagnostics.** GPU readings that disappear after being
  available now use the existing bounded sensor recovery. Unexpected widget
  errors are written to `error.log` instead of showing the .NET error dialog.
- Checks: **47** client/startup assertions (10 new), **15** sensor-selection and
  **47** widget checks (7 new). All builds completed without warnings or errors.

Not yet verified: mixed-DPI startup (the office PC's displays are all at 100%),
GPU recovery after a real graphics-driver update, the shortcut while a game is
in focus, reboot/startup with v0.1.7, and clean-PC installation.

## September 22 v0.1.6 CPU sensor startup recovery

A user with a Ryzen 7 9850X3D reported CPU temperature and clock remaining
unavailable after startup until reinstalling. They confirmed that starting
PawnIO and restarting DLB Precision Sensors restored the readings without
reinstalling. Source inspection found that setup started PawnIO, but the service
previously opened sensors without ensuring the driver was running and retained
an unsuccessful sensor reader indefinitely.

The service now checks the installed PawnIO driver before opening sensors,
starts it only when stopped, and waits up to 1.5 seconds for a pending start.
Driver errors leave other available readings active and appear in diagnostics.
If either CPU temperature or clock remains unavailable, the service can reopen
the reader up to three times, waiting successively 10, 30 and 60 seconds between
attempts. These attempts occur only when a client requests a sample. Healthy
CPU readings cancel pending recovery and reset the budget. There is no new
background timer or driver polling during healthy sampling, and the shared
driver's configuration is unchanged.

- The application suite passed **41** client/startup/integration assertions,
  including **19 new startup/recovery checks**, plus **15** sensor-selection and
  **40** widget checks. All builds completed without warnings or errors.
- The new checks use the production startup/recovery helper with simulated
  driver operations. They cover stopped, already-running, starting, stalled,
  concurrent-start, missing and blocked drivers; cancellation; retry cooldowns,
  exhaustion, partial readings and recovery. They do not mutate a real driver.
- A separate native-adapter check exercised the production ServiceController
  wrapper against this PC's already-running PawnIO driver and returned no warning.
- The signed installer is **7,434,112 bytes**, SHA256
  `cb53f34835036ee18623ea2ab408129dc8b9100bb454cbe4a7f27c85b960fe75`.
  Build-time checks verified DLB publisher signatures and timestamps on setup,
  its embedded uninstaller, and the four first-party binaries.
- The office PC upgraded from v0.1.5 with exit code **0** and no restart. The
  installed v0.1.6 service passed the **41** client/startup/integration assertions
  and supplied all seven live readings with `Status=ok` and no warnings.
- Independent installed checks verified all **19 runtime hashes**, the installed
  manifest, **0.1.6.0** versions on the four DLB binaries, and valid publisher
  signatures and timestamps on those binaries and the uninstaller. All **40**
  installed-widget smoke checks passed. The fresh before/after settings hash and
  Windows startup entry matched exactly; PawnIO's running state, demand-start
  configuration and DriverStore path were preserved.
- A **60.19-second** healthy-reading measurement (58 samples after 5 seconds of
  warmup) measured the installed widget and service together: **0.0333% average
  CPU** across 32 logical processors, **0.1416% peak sampled CPU**, **44.09 MiB
  average private resident RAM**, **112.38 MiB summed working set**, and
  **78.46 MiB private committed memory**. Working-set sums can double-count shared
  pages. These process counters exclude separate System/driver activity and do
  not measure gaming FPS or the temporary cost of repeated failed initialization.
  [Measurement](validation/performance-v0.1.6.json).

The affected PC's post-update reboot remains to be verified by its owner. The
automated scenarios do not reproduce a real stopped kernel driver, and no
shared driver was stopped or Windows rebooted during local development checks.

## September 13 v0.1.5 brighter labels

All seven metric captions and the small DLB Precision footer now use neutral
white `#EBEBEB`. The existing renderer handles both orientations; the change
does not add dependencies, polling, or timers.

- The existing application checks passed: **22** client/integration, **15**
  sensor-selection and **40** widget checks. Builds had zero warnings/errors.
- Horizontal, vertical, minimum-size and 150%-DPI renders were visually checked.
  A separate invocation of the existing renderer exercised the branding footer
  with sample readings, because the regular preview command replaces branding
  with a SAMPLE DATA marker. Labels and branding were readable without clipping;
  unavailable readings and status messages retained their muted color.
- The signed v0.1.5 installer passed build-time publisher and timestamp checks,
  including its embedded uninstaller and four first-party binaries. Its SHA256 is
  `ba330400522de404877dc28261d83793daa37145189e4b4b1ca99c5f14c03b2d`.
- The office PC upgrade from v0.1.4 completed with exit code **0**, all four service
  configuration commands returned **0**, and no restart was needed. The installed
  widget was reopened and responded normally.
- Independent installed verification passed: all **19** runtime hashes and the
  manifest matched the signed stage. The four DLB binaries reported **0.1.5.0**;
  those files and the uninstaller had valid, timestamped **DLB Precision, LLC**
  signatures. The installed widget passed **40** smoke checks. A fresh before/after
  comparison confirmed identical settings-file hashes and an unchanged Windows
  startup entry. The working shared PawnIO driver was preserved. Setup is
  **7,433,088 bytes**.

This is an appearance change. No new performance measurement or additional
hardware, game, clean-install, or reboot testing is claimed.

## September 9 v0.1.4 installer prerequisite checks

The reported v0.1.3 setup screen stopped before installing the application because
it considered the PawnIO footprint incomplete. That check required
`PawnIOLib.dll`, which the embedded LHM 0.9.6 sensor library does not use; its
sensor code opens the PawnIO device directly. The affected sim PC's precise
driver state has not been inspected, so the screenshot alone does not establish
whether it had a working driver with missing optional files or a driver problem.

The replacement prerequisite flow checks for a running, accessible PawnIO driver,
tries to start an existing stopped driver, and waits briefly for one already
starting. If unavailable, setup runs the included, hash-verified official
`PawnIO_setup.exe -install -silent`, then rechecks readiness. It preserves a
detected newer version instead of downgrading it. A vendor exit of 3010 retains
the restart requirement, including when setup must stop before completion.
The DLB sensor service is stopped before repair and restored if preparation is
cancelled. No automatic shared-driver uninstall is performed.

- All **23 compiled prerequisite scenarios passed** using the real
  `PawnIOPrerequisite.iss` decision helper with mocked readiness, version and
  installation operations. Cases cover missing and partial installations,
  existing-driver recovery, detected newer versions, operation failures,
  readiness rechecks and restart retention. The harness contains no driver,
  registry or service actions. [Results](validation/installer-v0.1.4-prerequisites.txt).
- The full v0.1.4 application build and checks passed: **22** client/integration,
  **15** sensor-selection and **40** widget checks. Integration used the existing
  installed service and returned all seven readings with no warnings.
- A complete unsigned validation installer compiled successfully, including the
  application runtime libraries and the unchanged official PawnIO 2.2.0 payload.
  It was not installed or published. This verifies packaging and compilation,
  not a successful driver repair or a publisher-signed release.
- The subsequent **publisher-signed installer** was built and independently
  verified: **7,431,552 bytes**, SHA256
  `ae0459cc6dd2e122e8b8de72870e78dd88d7037f5669f29b64d061350714b2d6`.
  Setup and all four staged DLB binaries passed exact publisher and timestamp
  verification, and all **19** staged runtime hashes matched their manifest.
  The unchanged PawnIO input still matched its pinned vendor hash. Build-time
  verification also covered the embedded uninstaller.
- A real signed v0.1.3-to-v0.1.4 upgrade completed with **exit code 0** and no
  restart. Its log confirmed the existing running, accessible PawnIO driver was
  preserved, exercising the production device-access check on this PC. All four
  DLB service-configuration commands returned 0. The installed v0.1.4 widget
  passed **40** smoke checks; **22** installed-service/client checks passed with
  all seven live readings and no warnings. The widget was relaunched and
  responded normally.
- After the upgrade, all **19 installed runtime hashes** matched the signed
  stage. The four DLB binaries reported version **0.1.4.0**; those files and
  the installed uninstaller had valid **DLB Precision, LLC** signatures and
  timestamps. Both Windows startup enumeration and its registry provider
  confirmed the correct installed executable target. No fresh before-upgrade
  settings hash was captured, so byte-for-byte settings preservation is not
  claimed for this upgrade.

Actual repair on a disposable Windows installation and retry on the affected sim
PC remain untested. No working host driver was deliberately damaged to simulate
those cases. Driver readiness does not establish every hardware sensor's
compatibility. The existing v0.1.3 performance results remain the latest measured
results; this installer change adds no recurring application work.

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
