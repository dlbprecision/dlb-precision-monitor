[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.1.0',
    [string]$IsccPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'installer\vendor\manifest.json') -Raw | ConvertFrom-Json
$toolsRoot = Join-Path $repoRoot '.tools'
$buildRoot = Join-Path $repoRoot 'artifacts\installer'
$stageRoot = Join-Path $buildRoot 'stage'
$appStage = Join-Path $stageRoot 'app'
$licenseStage = Join-Path $appStage 'licenses'
$provenanceStage = Join-Path $appStage 'provenance'
$sourceStage = Join-Path $appStage 'third-party-source'
New-Item -ItemType Directory -Force $toolsRoot, $buildRoot | Out-Null

function Get-VerifiedDownload {
    param($Spec, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Destination)) {
        Write-Host ('Downloading ' + $Spec.file + ' from its official release...')
        Invoke-WebRequest -Uri $Spec.url -OutFile $Destination -UseBasicParsing
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Destination).Hash
    if ($actualHash -ne $Spec.sha256) { throw "Pinned SHA256 mismatch for $Destination. Do not run it." }
    $signature = Get-AuthenticodeSignature -LiteralPath $Destination
    if ($signature.Status -ne 'Valid') { throw "Authenticode validation failed for $Destination ($($signature.Status))." }
    if ($signature.SignerCertificate.Thumbprint -ne $Spec.signerThumbprint) {
        throw "Unexpected signing certificate for $Destination. Review the manifest against the official release before updating it."
    }
    Write-Host ('Verified SHA256 and publisher signature: ' + $Spec.file)
}

$pawnPath = Join-Path $repoRoot ('installer\vendor\' + $manifest.pawnio.file)
Get-VerifiedDownload $manifest.pawnio $pawnPath
$modulesSource = Join-Path $repoRoot ('installer\vendor\' + $manifest.pawnioModulesSource.file)
if (-not (Test-Path -LiteralPath $modulesSource)) {
    Invoke-WebRequest -Uri $manifest.pawnioModulesSource.url -OutFile $modulesSource -UseBasicParsing
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $modulesSource).Hash -ne $manifest.pawnioModulesSource.sha256) {
    throw 'Pinned PawnIO module source archive hash mismatch.'
}

if (-not $IsccPath) {
    $compilerHome = Join-Path $toolsRoot ('innosetup-' + $manifest.inno.version)
    $IsccPath = Join-Path $compilerHome 'ISCC.exe'
    $compilerDownload = Join-Path $toolsRoot $manifest.inno.file
    Get-VerifiedDownload $manifest.inno $compilerDownload
    if (-not (Test-Path -LiteralPath $IsccPath)) {
        # Official portable mode: workspace files only, no installed product, no elevation.
        Write-Host 'Extracting the official Inno Setup compiler in portable mode...'
        $compilerProcess = Start-Process -FilePath $compilerDownload -ArgumentList @(
            '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1',
            ('/DIR="' + $compilerHome + '"'), '/NOICONS'
        ) -WindowStyle Hidden -Wait -PassThru
        if ($compilerProcess.ExitCode -ne 0) { throw "Portable compiler preparation failed: $($compilerProcess.ExitCode)" }
    }
}
if (-not (Test-Path -LiteralPath $IsccPath)) { throw "Inno compiler missing: $IsccPath" }

$projects = @(
    @{ Name = 'Monitor'; Path = 'src\DlbPrecision.Monitor\DlbPrecision.Monitor.csproj' },
    @{ Name = 'Service'; Path = 'src\DlbPrecision.Service\DlbPrecision.Service.csproj' }
)

foreach ($project in $projects) {
    $projectPath = Join-Path $repoRoot $project.Path
    if (-not (Test-Path -LiteralPath $projectPath)) { throw "Project not found: $projectPath" }
    $project.Output = Join-Path (Split-Path -Parent $projectPath) 'bin\Release\net48'
    $project.Assets = Join-Path (Split-Path -Parent $projectPath) 'obj\project.assets.json'
    if (-not $SkipBuild) {
        & dotnet build $projectPath --configuration Release --nologo -p:PlatformTarget=x64
        if ($LASTEXITCODE -ne 0) { throw "$($project.Name) build failed." }
    }
}

# Delete only this script's generated stage after checking the resolved absolute target.
$resolvedStage = [IO.Path]::GetFullPath($stageRoot)
$allowedStageParent = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\') + '\'
if (-not $resolvedStage.StartsWith($allowedStageParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a stage outside the installer build directory: $resolvedStage"
}
if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
New-Item -ItemType Directory -Force $appStage, $licenseStage, $provenanceStage, $sourceStage | Out-Null
Copy-Item -LiteralPath $modulesSource -Destination $sourceStage

$packages = @{}
foreach ($project in $projects) {
    foreach ($file in Get-ChildItem -LiteralPath $project.Output -File) {
        if ($file.Extension -notin '.exe', '.dll', '.config', '.json') { continue }
        $target = Join-Path $appStage $file.Name
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Conflicting shared build output: $($file.Name)"
            }
        } else { Copy-Item -LiteralPath $file.FullName -Destination $target }
    }
    $assets = Get-Content -LiteralPath $project.Assets -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        # Build-only reference assemblies are intentionally not distributed.
        if ($library.Name -like 'Microsoft.NETFramework.ReferenceAssemblies*') { continue }
        $packageDirectory = $null
        foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $packageRoot $library.Value.path
            if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
        }
        if (-not $packageDirectory) { throw "Missing restored package: $($library.Name)" }
        $packages[$library.Name] = @{ Directory = $packageDirectory; Sha512 = $library.Value.sha512 }
    }
}
foreach ($required in 'DlbPrecision.Monitor.exe','DlbPrecision.Service.exe','DlbPrecision.Shared.dll','LibreHardwareMonitorLib.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $appStage $required))) { throw "Missing staged runtime file: $required" }
}
if (-not $packages.ContainsKey('LibreHardwareMonitorLib/0.9.6')) {
    throw 'Review the LHM version, notices and corresponding PawnIO module sources before updating the installer.'
}
$lhmRuntime = Join-Path $packages['LibreHardwareMonitorLib/0.9.6'].Directory 'runtimes\win-x64\lib\net472\LibreHardwareMonitorLib.dll'
if ((Get-FileHash -LiteralPath (Join-Path $appStage 'LibreHardwareMonitorLib.dll')).Hash -ne
    (Get-FileHash -LiteralPath $lhmRuntime).Hash) {
    throw 'The staged sensor DLL does not match the restored x64 runtime. Do not distribute a reference assembly.'
}

$spdxSources = @{
    'MIT' = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/MIT.txt'
    'BSD-3-Clause' = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/BSD-3-Clause.txt'
    'Apache-2.0' = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/Apache-2.0.txt'
    'MPL-2.0' = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/MPL-2.0.txt'
    'LGPL-2.1-only' = 'https://raw.githubusercontent.com/spdx/license-list-data/v3.27.0/text/LGPL-2.1-only.txt'
}
$provenance = @()
foreach ($name in ($packages.Keys | Sort-Object)) {
    $package = $packages[$name]
    $safeName = $name.Replace('/', '-')
    $packageNotices = Join-Path $licenseStage $safeName
    New-Item -ItemType Directory -Force $packageNotices | Out-Null
    $nuspecFile = Get-ChildItem -LiteralPath $package.Directory -Filter *.nuspec -File | Select-Object -First 1
    if (-not $nuspecFile) { throw "No NuGet metadata for $name" }
    [xml]$nuspec = Get-Content -LiteralPath $nuspecFile.FullName -Raw
    $metadata = $nuspec.package.metadata
    $licenseElement = $metadata.SelectSingleNode('*[local-name()="license"]')
    $licenseValue = if ($licenseElement) { $licenseElement.InnerText } else { '' }
    $licenseType = if ($licenseElement) { $licenseElement.GetAttribute('type') } else { '' }
    Copy-Item -LiteralPath $nuspecFile.FullName -Destination $packageNotices
    $noticeFiles = @(Get-ChildItem -LiteralPath $package.Directory -File -Recurse | Where-Object {
        $_.Name -match '^(licen[sc]e|notice|copying|copyright)([.\-_].*)?$'
    })
    foreach ($notice in $noticeFiles) {
        $relative = $notice.FullName.Substring($package.Directory.Length).TrimStart('\','/')
        $noticeTarget = Join-Path $packageNotices $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $noticeTarget) | Out-Null
        Copy-Item -LiteralPath $notice.FullName -Destination $noticeTarget
    }
    if ($licenseType -eq 'file') {
        $specificLicense = Join-Path $package.Directory $licenseValue
        if (-not (Test-Path -LiteralPath $specificLicense)) { throw "Declared license file absent for $name" }
        Copy-Item -LiteralPath $specificLicense -Destination (Join-Path $packageNotices 'DECLARED-LICENSE.txt')
    } elseif ($licenseType -eq 'expression' -and $spdxSources.ContainsKey($licenseValue)) {
        $licenseCache = Join-Path $repoRoot ('installer\licenses\' + $licenseValue + '.txt')
        if (-not (Test-Path -LiteralPath $licenseCache)) {
            Invoke-WebRequest -Uri $spdxSources[$licenseValue] -OutFile $licenseCache -UseBasicParsing
        }
        Copy-Item -LiteralPath $licenseCache -Destination $packageNotices
    } elseif ($noticeFiles.Count -eq 0) {
        throw "Review and include the upstream license for $name before distributing (declared: '$licenseValue')."
    }
    $provenance += [ordered]@{
        package = $name; nugetSha512 = $package.Sha512; licenseType = $licenseType; license = $licenseValue
        authors = [string]$metadata.authors; copyright = [string]$metadata.copyright
        projectUrl = [string]$metadata.projectUrl; repository = [string]$metadata.repository.url
        commit = [string]$metadata.repository.commit
        source = 'https://www.nuget.org/packages/' + $name
        sourceCode = if ($metadata.repository.url -and $metadata.repository.commit) {
            ([string]$metadata.repository.url).Replace('git://', 'https://') + '/tree/' + [string]$metadata.repository.commit
        } else { [string]$metadata.projectUrl }
    }
}
Copy-Item -Path (Join-Path $repoRoot 'installer\licenses\*') -Destination $licenseStage -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\vendor\manifest.json') -Destination (Join-Path $provenanceStage 'vendor-manifest.json')
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $provenanceStage 'nuget-packages.json') -Encoding UTF8
$sourceNotice = @'
Third-party source and attribution

LibreHardwareMonitorLib is used unmodified under Mozilla Public License 2.0.
Exact package versions, source repository and commit are listed beside this file
in nuget-packages.json. Corresponding source is available without charge from:
https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
For release 0.9.6: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6
The library contains additional licensed code and signed PawnIO 0.1.6 modules.
Complete module source is bundled in ../third-party-source. Full upstream license
and notice files accompany this distribution. Other unmodified MPL libraries'
corresponding source is available at the exact sourceCode URL/commit recorded in
nuget-packages.json. DLB application source is separate; DLB does not claim
ownership of third-party components.

PawnIO's official unmodified installer is redistributed under its included
redistribution permission, retaining its original signature. It is not the
unsigned/unrestricted edition and is never installed with unrestricted flags.

The setup executable is built with Inno Setup. Upstream license, copyright and
web addresses are preserved. See the licenses folder and vendor-manifest.json.
'@
Set-Content -LiteralPath (Join-Path $provenanceStage 'SOURCE-NOTICE.txt') -Value $sourceNotice -Encoding UTF8
$runtimeHashes = Get-ChildItem -LiteralPath $appStage -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
}
Set-Content -LiteralPath (Join-Path $provenanceStage 'runtime-files.sha256') -Value $runtimeHashes -Encoding ASCII

# Refuse unsigned/obsolete fallback sensor drivers from dependency output.
$unsafePayload = Get-ChildItem -LiteralPath $appStage -Recurse -File | Where-Object { $_.Name -match 'WinRing0|OpenLibSys' }
if ($unsafePayload) { throw 'Unexpected legacy sensor-driver payload; review dependencies before packaging.' }

& $IsccPath ('/DStageDir=' + $stageRoot) ('/DOutputDir=' + $buildRoot) ('/DAppVersion=' + $Version) (Join-Path $repoRoot 'installer\DlbPrecision.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setupFile = Join-Path $buildRoot ('DLB-Precision-Monitor-' + $Version + '-Setup.exe')
$setupHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $setupFile).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($setupFile + '.sha256') -Value ($setupHash + '  ' + [IO.Path]::GetFileName($setupFile)) -Encoding ASCII
Write-Host ('Built: ' + $setupFile)
Write-Host ('Size: {0:N2} MiB; SHA256: {1}' -f ((Get-Item -LiteralPath $setupFile).Length / 1MB), $setupHash)
Write-Host 'The DLB pilot setup is unsigned. No driver/service or DLB application has been installed by this build.'
