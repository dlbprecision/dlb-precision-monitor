[CmdletBinding()]
param(
    # The updater whose rules customers' PCs are running. Use the previous release's updater
    # (installed, or from its signed build's stage folder), not the one being released.
    [string]$Updater = (Join-Path $env:ProgramFiles 'DLB Precision Monitor\DlbPrecision.Updater.exe'),
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,5}$')][string]$ExpectVersion,
    # The version those PCs have installed; defaults to the updater file's own version.
    [string]$AssumeInstalled,
    # Omit for the live GitHub Latest release, which is what customers get.
    [string]$Feed,
    [switch]$SkipDownload
)
# Read-only release check. It runs a released updater's own offer rules against the live Latest
# release, then downloads the offered setup and checksum to a temporary folder and runs that
# updater's own verification on them. Nothing is installed or run.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not (Test-Path -LiteralPath $Updater)) { throw "Updater not found: $Updater" }
# Loaded from its bytes, so the installed file is never locked by this check.
$work = Join-Path ([IO.Path]::GetTempPath()) ('dlb-latest-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if (-not $AssumeInstalled) { $AssumeInstalled = (Get-Item -LiteralPath $Updater).VersionInfo.FileVersion }
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Updater))
    $any = [Reflection.BindingFlags]'Static,Public,NonPublic'
    $feedType = $assembly.GetType('DlbPrecision.Updater.ReleaseFeed', $true)
    $offerType = $assembly.GetType('DlbPrecision.Updater.UpdateOffer', $true)
    $verifierType = $assembly.GetType('DlbPrecision.Updater.PackageVerifier', $true)
    $source = if ($Feed) { $Feed } else { [string]$feedType.GetField('LatestUrl', $any).GetValue($null) }
    $prefix = [string]$feedType.GetField('DownloadPrefix', $any).GetValue($null)
    Write-Host ("Rules from updater {0}; installed version assumed {1}" -f (Get-Item -LiteralPath $Updater).VersionInfo.FileVersion, $AssumeInstalled)

    $fetched = $feedType.GetMethod('Fetch', $any).Invoke($null, [object[]]@([string]$source, 'DLB-release-check'))
    if ([string]$fetched.Status -ne 'Release') { throw "FAIL: the feed returned $($fetched.Status): $($fetched.Message)" }
    # Exactly the arguments the real channel uses: no pre-releases, no file addresses, DLB's download prefix.
    $decision = $offerType.GetMethod('Decide', $any).Invoke($null, [object[]]@($fetched.Release, [Version]$AssumeInstalled, $false, $false, $prefix))
    if ([string]$decision.Status -ne 'Available') { throw "FAIL: release $($fetched.Release.Tag) is $($decision.Status) for an installed $AssumeInstalled." }
    $offer = $decision.Offer
    if ($offer.VersionText -ne $ExpectVersion) { throw "FAIL: offered $($offer.VersionText), expected $ExpectVersion." }
    Write-Host "PASS offer: $($fetched.Release.Tag) is offered as $($offer.VersionText) from $($offer.Installer.DownloadUrl)"
    if ($SkipDownload) { return }

    $setup = Join-Path $work $offer.Installer.Name
    $checksum = Join-Path $work $offer.Checksum.Name
    Invoke-WebRequest -Uri $offer.Checksum.DownloadUrl -OutFile $checksum -UseBasicParsing
    Invoke-WebRequest -Uri $offer.Installer.DownloadUrl -OutFile $setup -UseBasicParsing
    if ((Get-Item -LiteralPath $setup).Length -ne $offer.Installer.Size) { throw 'FAIL: the downloaded setup size differs from the release listing.' }
    $parse = $assembly.GetType('DlbPrecision.Updater.ChecksumFile', $true).GetMethod('TryParse', $any)
    $parseArguments = [object[]]@([IO.File]::ReadAllText($checksum), [string]$offer.Installer.Name, $null)
    if (-not $parse.Invoke($null, $parseArguments)) { throw 'FAIL: the checksum file cannot be read by this updater.' }
    $stream = [IO.File]::Open($setup, 'Open', 'Read', 'Read')
    try {
        $verdict = $verifierType.GetMethod('Verify', $any).Invoke($null, [object[]]@($stream, [string]$setup, [string]$parseArguments[2], [string]$offer.VersionText))
    } finally { $stream.Dispose() }
    if (-not $verdict.Ok) { throw "FAIL verification: $($verdict.Reason)" }
    Write-Host "PASS verification: this updater accepts $($offer.Installer.Name) (SHA256 $($parseArguments[2]))."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
