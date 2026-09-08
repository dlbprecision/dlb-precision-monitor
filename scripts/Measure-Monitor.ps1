param(
    [Parameter(Mandatory = $true)][int[]]$ProcessIds,
    [ValidateRange(10, 600)][int]$DurationSeconds = 60,
    [ValidateRange(0, 60)][int]$WarmupSeconds = 10,
    [string]$OutputPath = '',
    [string]$Scenario = 'Visible widget, 1 second refresh'
)
$ErrorActionPreference = 'Stop'
$logicalProcessors = [Environment]::ProcessorCount
$startProcesses = @{}
foreach ($processId in $ProcessIds) {
    $process = Get-Process -Id $processId
    $startProcesses[$processId] = $process.ProcessName
}
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
$previousCpu = @{}
foreach ($processId in $ProcessIds) {
    $previousCpu[$processId] = (Get-Process -Id $processId).TotalProcessorTime.TotalSeconds
}
$timer = [Diagnostics.Stopwatch]::StartNew()
$previousElapsed = 0.0
$samples = [Collections.Generic.List[object]]::new()
while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
    Start-Sleep -Milliseconds 1000
    $elapsed = $timer.Elapsed.TotalSeconds
    $interval = $elapsed - $previousElapsed
    $totalCpu = 0.0
    $totalWorking = 0L
    $totalPrivate = 0L
    $details = @()
    foreach ($processId in $ProcessIds) {
        $process = Get-Process -Id $processId
        if ($process.ProcessName -ne $startProcesses[$processId]) { throw 'A measured process exited or changed identity.' }
        $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
        $cpuPercent = 100 * ($cpuSeconds - $previousCpu[$processId]) / $interval / $logicalProcessors
        $previousCpu[$processId] = $cpuSeconds
        $totalCpu += $cpuPercent
        $totalWorking += $process.WorkingSet64
        $totalPrivate += $process.PrivateMemorySize64
        $details += [pscustomobject]@{
            process = $process.ProcessName
            cpuPercent = $cpuPercent
            workingSetMiB = $process.WorkingSet64 / 1MB
            privateMiB = $process.PrivateMemorySize64 / 1MB
        }
    }
    $samples.Add([pscustomobject]@{
        elapsedSeconds = $elapsed
        cpuPercent = $totalCpu
        workingSetMiB = $totalWorking / 1MB
        privateMiB = $totalPrivate / 1MB
        processes = $details
    })
    $previousElapsed = $elapsed
}
$result = [ordered]@{
    measuredAtUtc = [DateTime]::UtcNow.ToString('o')
    scenario = $Scenario
    durationSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
    warmupSeconds = $WarmupSeconds
    logicalProcessors = $logicalProcessors
    processes = @($startProcesses.Values)
    sampleCount = $samples.Count
    averageCpuPercent = [Math]::Round(($samples | Measure-Object cpuPercent -Average).Average, 4)
    peakSampleCpuPercent = [Math]::Round(($samples | Measure-Object cpuPercent -Maximum).Maximum, 4)
    averageWorkingSetMiB = [Math]::Round(($samples | Measure-Object workingSetMiB -Average).Average, 2)
    peakWorkingSetMiB = [Math]::Round(($samples | Measure-Object workingSetMiB -Maximum).Maximum, 2)
    averagePrivateMiB = [Math]::Round(($samples | Measure-Object privateMiB -Average).Average, 2)
    peakPrivateMiB = [Math]::Round(($samples | Measure-Object privateMiB -Maximum).Maximum, 2)
    notes = @('CPU percent is normalized across all logical processors; one-second sampling can miss shorter peaks.', 'Memory is the sum for every specified process; working sets may include shared pages.', 'Measurements apply only to the tested PC, state, permissions, and display mode.')
    samples = $samples
}
if ($OutputPath) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
$summary = [ordered]@{}
foreach ($key in $result.Keys) { if ($key -ne 'samples') { $summary[$key] = $result[$key] } }
$summary | ConvertTo-Json -Depth 4
