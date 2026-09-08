# Windows 11 installer

Build an unsigned local installer from the repository root with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 -Version 0.1.1 -OutputDirectory .\artifacts\unsigned-0.1.1-attempt-1
```

This needs a .NET SDK on the **build computer**, network access for restore and the first tool download, and the Windows trust service to validate signed payloads. End-user Windows 11 computers already include the .NET Framework runtime used by this application. No separate desktop monitoring program is required. The script downloads the compiler in its official portable mode into `.tools`; it does not install the monitor, driver, or service on the build machine.

This command outputs `artifacts/unsigned-0.1.1-attempt-1/DLB-Precision-Monitor-0.1.1-Setup.exe` and a SHA256 sidecar. Omitting `-OutputDirectory` uses `artifacts/installer`. Choose a new output directory for each attempt; the script refuses to overwrite an existing installer or checksum. `-Version` applies to the application builds and setup label. `-SkipBuild` reuses Release outputs and checks their version, but cannot establish source freshness; release builds should rebuild. `-IsccPath` can select an existing trusted Inno compiler. The default compiler and PawnIO downloads are pinned by SHA256 and signing-certificate thumbprint in `vendor/manifest.json`; both checks must pass. A changed upstream certificate/release requires deliberate manifest review.

Publisher signing requires `-Sign` and the tool, metadata and expected-publisher parameters documented in [code signing](../docs/CODE-SIGNING.md). Default builds remain unsigned. Signed builds fail if signing or timestamp verification fails; they do not fall back to unsigned output. Signing tools and credentials belong on the build computer and are not packaged for users.

Downloaded vendor executables and source archives are excluded from Git. The committed manifest lets the build fetch and verify them on a fresh checkout. Generated application binaries and installer output are also excluded; pilot installers are attached to GitHub releases.

## Install and upgrade behavior

- Supports native x64 Windows 11 build 22000 and newer; Windows 10 and ARM are excluded.
- Selects the desktop shortcut task by default on fresh installations. Inno may preserve a user's earlier task selection during an upgrade.
- Prompts for administrator approval, installs files in protected Program Files, and creates the LocalSystem `DlbPrecisionSensors` auto-start service. The service should do no sensor polling without a client.
- Preserves an existing PawnIO installation; only absent installations get the original signed 2.2.0 payload with `-install -silent`. Handles driver exit 3010 as reboot required.
- A stray driver file or incomplete registration is not considered a usable installation: setup stops with shared-driver repair guidance, without replacing/removing it. Registered presence is not a sensor accuracy claim.
- Stops the DLB sensor service before updating files, and restarts it after configuration. Inno Restart Manager handles app files in use.
- Creates startup for the original interactive user through `--enable-startup`; the UI is started using `runasoriginaluser`, not intentionally elevated. An already-elevated installer cannot recover credentials Inno never received, so app startup logic should refuse elevated ordinary UI launch; users can open the Start menu entry normally afterward.
- Checks the startup helper's exit code. If user startup configuration fails, the finished page/log explicitly says it was not enabled and setup exits with custom code **20**; the core app and service are still installed. Open the monitor as the intended standard user and enable startup in Settings. The postinstall launch uses `--from-installer`, which refuses an elevated widget launch.
- Removes the DLB service and its files on uninstall. Clears matching DLB startup entries in currently loaded user hives only. Unloaded user hives are not loaded or modified. User preferences remain. A stale startup entry in an offline user's profile may need disabling at their next sign-in.
- Stopping the service occurs only after uninstall confirmation, so opening then cancelling the uninstaller does not stop monitoring.
- Leaves shared PawnIO installed even if first supplied by DLB: ownership of later consumers is not reliably knowable. The installer explains its separate removal through Installed apps when no other application needs it.

## Distribution and verification

[v0.1.1](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.1) is the first publisher-signed pilot. Its setup, uninstaller, monitor and service executables, and DLB Shared and Sensors libraries carry DLB Precision, LLC signatures and timestamps. Third-party files retain their original bytes, including PawnIO's valid publisher signature. No self-signed trust certificate is installed. The earlier [v0.1.0](https://github.com/dlbprecision/dlb-precision-monitor/releases/tag/v0.1.0) remains an unsigned historical release with its original assets and checksums.

`SignRelease` makes Inno display `SIGNED-INSTALLATION-NOTES.txt` and install it as `INSTALLATION-NOTES.txt`. Unsigned local builds use the original filename's unsigned-pilot guidance. The installed notes therefore describe the selected build mode.

The signed build verifies signatures, expected publisher names and required timestamps before producing the final checksum. A signature does not guarantee immediate SmartScreen reputation. App/service/startup behavior still needs clean-PC installation, reboot and uninstall coverage, along with broader hardware and game testing before general customer distribution. Compiler or signature verification alone does not establish those behaviors. See the [validation status](../docs/VALIDATION.md) and the release's specific verification results.

Inno Setup 6.7.3 prints a non-commercial banner when no paid key is configured, but its included license explicitly permits commercial applications. The publisher's [commercial license FAQ](https://jrsoftware.org/isorder.php) says purchase is not strictly required, and its early-stage guidance does not expect purchase before production readiness. No paid license was purchased or activated for this build. See `BUILD-LICENSE-REVIEW.md` for the checked sources; retaining current security fixes is preferable to downgrading solely because of that banner.

The build inventories runtime NuGet dependencies and copies included licenses/notices plus SPDX license texts and source pointers. Preserve these notices with every distribution. Review any new dependency with a missing or unrecognized license before packaging. The official PawnIO installer contains its own permission to redistribute it **unmodified**, recorded in `licenses/PawnIO-NOTICE.txt`; do not substitute source-license assumptions for that binary permission.

Gaming usage is documented in `../docs/OVERLAY-COMPATIBILITY.md`. The user selected a desktop widget on another monitor while gaming, so game injection and same-screen exclusive-fullscreen rendering are outside the current scope.
