[CmdletBinding()]
param(
    # The updater whose rules customers' PCs are running: a released updater, from the stage-*\app folder of
    # the signed build whose setup SHA256 matches that release's published .sha256. Never the one being
    # released, and not a test build that happens to be installed.
    [string]$Updater = (Join-Path $env:ProgramFiles 'DLB Precision Monitor\DlbPrecision.Updater.exe'),
    [Parameter(Mandatory)][ValidatePattern('^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,5}$')][string]$ExpectVersion,
    # The version those PCs have installed; defaults to the updater file's own version.
    [string]$AssumeInstalled,
    # Omit for the live GitHub Latest release, which is what customers get.
    [string]$Feed,
    # Pre-flight before marking Latest: judge a published pre-release (given with -Feed) as if it were Latest.
    [switch]$AsIfLatest,
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
    $updaterInfo = (Get-Item -LiteralPath $Updater).VersionInfo
    Write-Host ("Rules from updater {0} (built from {1}, SHA256 {2}); installed version assumed {3}" -f $updaterInfo.FileVersion,
        $updaterInfo.ProductVersion, (Get-FileHash -Algorithm SHA256 -LiteralPath $Updater).Hash.ToLowerInvariant(), $AssumeInstalled)

    $updaterVersion = [Version]$updaterInfo.FileVersion
    if ($updaterVersion -ge [Version]($ExpectVersion + '.0')) {
        Write-Warning "This updater is already version $updaterVersion. Customers run older updaters; pass the previous release's DlbPrecision.Updater.exe (from its signed build's stage-*\app folder)."
    }
    if ($updaterVersion.Revision -gt 0) {
        Write-Warning "This updater is a test build ($updaterVersion), not a released one, so its rules may not be what customers run."
    }
    $fetched = $feedType.GetMethod('Fetch', $any).Invoke($null, [object[]]@([string]$source, 'DLB-release-check'))
    if ([string]$fetched.Status -ne 'Release') { throw "FAIL: the feed returned $($fetched.Status): $($fetched.Message)" }
    if ($fetched.Release.Draft) { throw "FAIL: $($fetched.Release.Tag) is a draft, which is never offered." }
    if ($fetched.Release.Prerelease -and -not $AsIfLatest) {
        throw "FAIL: $($fetched.Release.Tag) is a pre-release; installed copies only see the release marked Latest. For the pre-flight, add -AsIfLatest."
    }
    # Exactly the arguments the real channel uses: no file addresses and DLB's download prefix. Pre-releases
    # are allowed only for the -AsIfLatest pre-flight; the real channel never sees them.
    $decision = $offerType.GetMethod('Decide', $any).Invoke($null, [object[]]@($fetched.Release, [Version]$AssumeInstalled, [bool]$AsIfLatest, $false, $prefix))
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
