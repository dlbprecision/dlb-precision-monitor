# Code signing

Signing is opt-in. The default build remains an unsigned pilot. A signed build
requires DLB's approved Azure Artifact Signing Public Trust certificate profile;
it never falls back to unsigned output. Signing tools run on the build computer
only and are not added to the monitor or its installer.

## Prerequisites

Follow [Microsoft's signing integration instructions](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations)
to obtain compatible x64 Windows SDK SignTool, the matching x64
`Azure.CodeSigning.Dlib.dll` from `Microsoft.ArtifactSigning.Client`, the .NET 8
runtime and the Visual C++ runtime. Use official Microsoft packages, verify their
provenance, and record the versions used for a release. These tools can be kept
under the ignored `.tools` directory; do not add them to application dependencies.

The v0.1.1 and v0.1.2 pilots were signed with Windows SDK BuildTools 10.0.28000.2705,
Microsoft.ArtifactSigning.Client 1.0.128 and Azure CLI 2.90.0. Inno's signing
wrapper uses the same PowerShell edition as the parent build, so inherited
PowerShell module paths stay compatible.

The Azure identity used by the tools must have the
[Artifact Signing Certificate Profile Signer role](https://learn.microsoft.com/en-us/azure/artifact-signing/tutorial-assign-roles).
Owner alone does not grant signing. Configure an explicit supported credential,
such as Azure CLI authentication, before compiling. Portal sign-in does not
automatically authenticate SignTool. Keep tokens and credentials out of this
repository and out of signing metadata.

Create a local metadata JSON file outside the repository or under `.tools`:

```json
{
  "Endpoint": "https://<region>.codesigning.azure.net",
  "CodeSigningAccountName": "<account-name>",
  "CertificateProfileName": "<public-trust-profile-name>"
}
```

Replace the endpoint with Microsoft's exact endpoint for the account's region.
`CorrelationId` and `ExcludeCredentials` are also supported. Use
`ExcludeCredentials` to restrict the credential chain to the intended identity,
following Microsoft's instructions. Do not put client secrets into this file.

## Azure CLI login on a local Windows account

Microsoft's Security Defaults block device-code authentication for new tenants
created on or after July 1, 2026. With Security Defaults enabled, do not use
`az login --use-device-code`; it can produce `AADSTS530035` even after the browser
accepts the account credentials. Keep tenant Security Defaults enabled and use
[normal browser login](https://learn.microsoft.com/en-us/cli/azure/authenticate-azure-cli-interactively#sign-in-with-a-browser).
See Microsoft's [device-code restriction](https://learn.microsoft.com/en-us/entra/fundamentals/security-defaults#block-device-code-flow).

For a PC using a local Windows account, select browser authentication instead of
Windows Web Account Manager with these process-only settings:

```powershell
$env:AZURE_CORE_ENABLE_BROKER_ON_WINDOWS = 'false'
$env:AZURE_CORE_LOGIN_EXPERIENCE_V2 = 'off'
& '<full-path-to-az.cmd>' login --tenant '<tenant-id>' --output none
```

Complete the sign-in and any MFA in the browser that opens, leaving the terminal
running until login finishes. These [environment-variable overrides](https://learn.microsoft.com/en-us/cli/azure/azure-cli-configuration)
apply to the current PowerShell process and its children; they do not change the
Windows sign-in account, its password, or tenant security settings. Azure CLI
refreshes its normal local sign-in cache. Portal login alone is still insufficient
for command-line signing.

## Build

Use a new output directory for each attempt. The script rejects an existing
installer filename or checksum before any downloads, build, staging or signing,
so a failed attempt cannot overwrite a previously released file. Each attempt
uses a new staging directory and never deletes earlier build artifacts.

```powershell
$signingBuild = @{
    Version = '0.1.3'
    OutputDirectory = '.\artifacts\release-0.1.3-attempt-1'
    Sign = $true
    SignToolPath = '<full-path-to-x64-signtool.exe>'
    DlibPath = '<full-path-to-x64-Azure.CodeSigning.Dlib.dll>'
    SigningMetadataPath = '<full-path-to-local-metadata.json>'
    ExpectedPublisher = '<exact-validated-certificate-publisher-name>'
}
.\scripts\Build-Installer.ps1 @signingBuild
```

`ExpectedPublisher` must exactly match the certificate's simple subject name
(normally its common name), including case. Use the validated legal publisher,
not an unverified product brand. No certificate thumbprint is pinned because
Artifact Signing rotates its short-lived leaf certificates.

For an unsigned local build, omit all signing parameters:

```powershell
.\scripts\Build-Installer.ps1 -OutputDirectory '.\artifacts\unsigned-test-1'
```

Passing signing parameters without `-Sign` is an error. `-Version` is passed to
both application builds. `-SkipBuild` checks the version of the existing DLB
outputs; it does not establish source freshness, so release builds should rebuild.

## Signing order and checks

1. Build and stage the applications; verify shared outputs and the original
   LibreHardwareMonitor runtime hash. Preserve all third-party bytes and PawnIO's
   existing publisher signature.
2. Sign only staged `DlbPrecision.Monitor.exe`, `DlbPrecision.Service.exe`,
   `DlbPrecision.Shared.dll` and `DlbPrecision.Sensors.dll`. Original build outputs
   remain untouched by signing. Generate the runtime hash manifest afterward.
3. In signed mode, Inno uses `SignTool=DlbArtifact` and `SignedUninstaller=yes`.
   Its `/S` command invokes the same PowerShell wrapper for the temporary
   uninstaller and final installer. The wrapper verifies each signature before
   returning success. Inno then embeds the signed uninstaller and removes its
   temporary file; there is no persistent signed cache to check afterward.
4. Verify the final setup again, then create its SHA256 sidecar. Publish those
   exact bytes together. Never calculate the release checksum before signing.

The wrapper uses SHA256 for the file and RFC 3161 timestamp digests and Microsoft's
`http://timestamp.acs.microsoft.com` timestamp service. Timestamping is required:
Artifact Signing leaf certificates have a three-day validity period. Every sign
and `signtool verify /pa /all /tw` command must exit zero; warnings fail the build.
Authenticode must also report Valid, provide a timestamp certificate, and match
the exact configured publisher. Existing signatures are verified and never
replaced or appended to.

`Sign-Artifact.ps1 -ValidateOnly` validates paths and metadata without contacting
Azure or signing. `-VerifyOnly` checks an existing artifact using SignTool and
Authenticode. Neither constitutes a live signing test.

## Release validation

After a live signed build, test installation, upgrade from the previous pilot,
reboot/startup and uninstall on Windows 11. Track completed checks and outstanding
pilot coverage in [validation](VALIDATION.md). Verify the installed four DLB files
and `unins*.exe` with the same expected publisher and required timestamp. Confirm
the bundled PawnIO hash still matches the vendor manifest. Update the release
notes and unsigned-pilot wording before publishing a signed release under a new
version; preserve old releases and their original checksums.

A valid publisher signature does not guarantee immediate SmartScreen reputation
or replace malware detection review. See [Microsoft's code-signing guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).

References: [SignTool verification and exit codes](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool),
[Inno SignTool](https://jrsoftware.org/ishelp/topic_setup_signtool.htm),
[Inno signed uninstaller](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm).
