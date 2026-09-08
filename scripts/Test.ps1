param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $projects = @('tests/DlbPrecision.Tests/DlbPrecision.Tests.csproj', 'src/DlbPrecision.Probe/DlbPrecision.Probe.csproj', 'src/DlbPrecision.Monitor/DlbPrecision.Monitor.csproj')
    foreach ($project in $projects) {
        & dotnet build $project -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }
    $ipcArguments = @()
    if ($Integration) { $ipcArguments += '--integration' }
    & 'tests/DlbPrecision.Tests/bin/Release/net48/DlbPrecision.Tests.exe' @ipcArguments
    if ($LASTEXITCODE -ne 0) { throw 'Shared / service client checks failed.' }
    & 'src/DlbPrecision.Probe/bin/Release/net48/DlbPrecision.Probe.exe' --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Sensor selection checks failed.' }
    $ui = Start-Process -FilePath (Join-Path $projectRoot 'src/DlbPrecision.Monitor/bin/Release/net48/DlbPrecision.Monitor.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru -Wait
    if ($ui.ExitCode -ne 0) { throw 'Widget smoke checks failed.' }
} finally { Pop-Location }
