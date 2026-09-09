[CmdletBinding()]
param([string]$IsccPath)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'installer\vendor\manifest.json') -Raw | ConvertFrom-Json
if (-not $IsccPath) {
    $IsccPath = Join-Path $repoRoot ('.tools\innosetup-' + $manifest.inno.version + '\ISCC.exe')
}
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'An existing trusted Inno compiler is required. Prepare the normal build tools first, or pass -IsccPath. This test never downloads or installs tools.'
}
$IsccPath = (Get-Item -LiteralPath $IsccPath).FullName
$helper = Join-Path $repoRoot 'installer\PawnIOPrerequisite.iss'
$harness = Join-Path $repoRoot 'tests\Installer\PawnIOPrerequisite.Tests.iss'
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) {
    throw 'Production PawnIO prerequisite helper is missing.'
}

# Unique output prevents stale PASS results and preserves every prior test artifact.
$outputPath = Join-Path $repoRoot ('artifacts\installer-tests\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputPath | Out-Null
$resultPath = Join-Path $outputPath 'results.txt'
$logPath = Join-Path $outputPath 'setup.log'
$harnessExe = Join-Path $outputPath 'PawnIOPrerequisite.Tests.exe'

& $IsccPath ('/DTestOutputDir=' + $outputPath) $harness
if ($LASTEXITCODE -ne 0) { throw 'PawnIO prerequisite test harness compilation failed.' }

# InitializeSetup uses only in-memory mock adapters, writes results, then returns
# False before installation. No payload/registry/service operations are included.
$arguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    ('/ResultFile="' + $resultPath + '"'), ('/LOG="' + $logPath + '"'))
$process = Start-Process -FilePath $harnessExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(60000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw "PawnIO prerequisite tests timed out. Logs: $outputPath"
}
$process.Refresh()
# Returning False from InitializeSetup intentionally produces exit code 1.
# https://jrsoftware.org/ishelp/topic_setupexitcodes.htm
if ($process.ExitCode -ne 1) {
    throw "Unexpected test harness exit code $($process.ExitCode). Logs: $outputPath"
}
if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
    throw "Test harness produced no result file. Logs: $outputPath"
}

$expectedCases = @(
    'healthy-preserved', 'newer-healthy-preserved', 'absent-installed',
    'partial-footprint-repaired', 'equal-three-part-version-repaired',
    'equal-four-part-version-repaired', 'older-version-repaired',
    'unparseable-version-repaired', 'newer-patch-version-blocked',
    'newer-minor-version-blocked', 'newer-major-version-blocked',
    'payload-verification-failed', 'payload-extraction-failed',
    'vendor-launch-failed', 'vendor-error-code-blocked',
    'success-without-ready-driver-blocked', 'vendor-reboot-ready',
    'vendor-reboot-not-ready', 'existing-reboot-preserved-healthy',
    'existing-reboot-preserved-install', 'repeated-preparation-preserves-reboot',
    'stopped-existing-driver-started', 'stopped-newer-driver-started'
)
$lines = @(Get-Content -LiteralPath $resultPath)
$expectedLines = @('DLB_PAWNIO_TEST_RESULTS_V1') +
    @($expectedCases | ForEach-Object { 'PASS|' + $_ }) +
    @(('TOTAL|{0}' -f $expectedCases.Count), ('PASSED|{0}' -f $expectedCases.Count), 'FAILED|0')
if (($lines.Count -ne $expectedLines.Count) -or
    (($lines -join "`n") -cne ($expectedLines -join "`n"))) {
    throw "PawnIO prerequisite tests failed or returned incomplete results.`n$($lines -join "`n")`nLogs: $outputPath"
}
Write-Host ('Passed {0} PawnIO prerequisite scenarios using the production helper and mocked operations.' -f $expectedCases.Count)
Write-Host ('Results: ' + $resultPath)
