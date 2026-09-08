param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int[]]$ProcessIds,
    [ValidateRange(10, 600)][int]$DurationSeconds = 60,
    [ValidateRange(0, 60)][int]$WarmupSeconds = 10,
    [string]$OutputPath = '',
    [string]$Scenario = 'Visible widget, 1 second refresh'
)
$ErrorActionPreference = 'Stop'
$logicalProcessors = [Environment]::ProcessorCount
if (@($ProcessIds | Select-Object -Unique).Count -ne $ProcessIds.Count) { throw 'Process IDs must be unique.' }
if ($OutputPath -and (Test-Path -LiteralPath $OutputPath)) { throw 'Choose a new output path; prior measurements are preserved.' }
$counterFilter = ($ProcessIds | ForEach-Object { 'IDProcess = ' + $_ }) -join ' OR '

function Read-ProcessCounters {
    $rows = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter $counterFilter)
    $result = @{}
    foreach ($processId in $ProcessIds) {
        $matches = @($rows | Where-Object IDProcess -eq $processId)
        if ($matches.Count -ne 1) { throw "Missing or ambiguous performance counters for process $processId." }
        $row = $matches[0]
        foreach ($required in 'Name','ElapsedTime','PercentProcessorTime','Timestamp_Sys100NS','WorkingSet','WorkingSetPrivate','PrivateBytes') {
            if ($null -eq $row.$required) { throw "Counter $required is unavailable for process $processId; never substitute zero." }
        }
        $result[$processId] = [pscustomobject]@{
            name = $row.Name
            birthMarker = [decimal]$row.ElapsedTime
            cpuTicks = [decimal]$row.PercentProcessorTime
            timestampTicks = [decimal]$row.Timestamp_Sys100NS
            workingSetMiB = $row.WorkingSet / 1MB
            privateWorkingSetMiB = $row.WorkingSetPrivate / 1MB
            privateMiB = $row.PrivateBytes / 1MB
        }
    }
    return $result
}

function Assert-SameProcess($Before, $After) {
    if ($Before.name -ne $After.name -or $Before.birthMarker -ne $After.birthMarker) {
        throw 'A measured process exited or its PID was reused.'
    }
}

$beforeWarmup = Read-ProcessCounters
if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
$first = Read-ProcessCounters
foreach ($processId in $ProcessIds) { Assert-SameProcess $beforeWarmup[$processId] $first[$processId] }
$previous = $first
$timer = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
    Start-Sleep -Milliseconds 1000
    $current = Read-ProcessCounters
    $details = foreach ($processId in $ProcessIds) {
        $before = $previous[$processId]
        $after = $current[$processId]
        Assert-SameProcess $first[$processId] $after
        $intervalTicks = $after.timestampTicks - $before.timestampTicks
        $cpuTicks = $after.cpuTicks - $before.cpuTicks
        if ($intervalTicks -le 0 -or $cpuTicks -lt 0) { throw 'Performance counters stopped advancing or regressed.' }
        # PERF_100NSEC_TIMER: use provider timestamps, subtracting in decimal to
        # preserve large 100-nanosecond timestamps before calculating percentages.
        $cpuPercent = [double](100 * $cpuTicks / $intervalTicks / $logicalProcessors)
        [pscustomobject]@{
            process = $after.name
            processId = $processId
            intervalSeconds = [double]($intervalTicks / 10000000)
            cpuPercent = $cpuPercent
            workingSetMiB = $after.workingSetMiB
            privateWorkingSetMiB = $after.privateWorkingSetMiB
            privateMiB = $after.privateMiB
        }
    }
    $samples.Add([pscustomobject]@{
        elapsedSeconds = $timer.Elapsed.TotalSeconds
        cpuPercent = ($details | Measure-Object cpuPercent -Sum).Sum
        workingSetMiB = ($details | Measure-Object workingSetMiB -Sum).Sum
        privateWorkingSetMiB = ($details | Measure-Object privateWorkingSetMiB -Sum).Sum
        privateMiB = ($details | Measure-Object privateMiB -Sum).Sum
        processes = @($details)
    })
    $previous = $current
}
$processSummaries = foreach ($processId in $ProcessIds) {
    $start = $first[$processId]
    $end = $previous[$processId]
    $processSamples = @($samples | ForEach-Object { $_.processes | Where-Object processId -eq $processId })
    [pscustomobject]@{
        process = $end.name
        processId = $processId
        counterDurationSeconds = [double](($end.timestampTicks - $start.timestampTicks) / 10000000)
        averageCpuPercent = [double](100 * ($end.cpuTicks - $start.cpuTicks) / ($end.timestampTicks - $start.timestampTicks) / $logicalProcessors)
        peakSampleCpuPercent = ($processSamples | Measure-Object cpuPercent -Maximum).Maximum
        averageWorkingSetMiB = ($processSamples | Measure-Object workingSetMiB -Average).Average
        averagePrivateWorkingSetMiB = ($processSamples | Measure-Object privateWorkingSetMiB -Average).Average
        averagePrivateMiB = ($processSamples | Measure-Object privateMiB -Average).Average
        cpuStartTicks = $start.cpuTicks
        cpuEndTicks = $end.cpuTicks
        startTimestampTicks = $start.timestampTicks
        endTimestampTicks = $end.timestampTicks
    }
}
$result = [ordered]@{
    measuredAtUtc = [DateTime]::UtcNow.ToString('o')
    scenario = $Scenario
    counterSource = 'Win32_PerfRawData_PerfProc_Process; PERF_100NSEC_TIMER'
    durationSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 2)
    warmupSeconds = $WarmupSeconds
    logicalProcessors = $logicalProcessors
    processes = @($processSummaries.process)
    sampleCount = $samples.Count
    averageCpuPercent = [Math]::Round(($processSummaries | Measure-Object averageCpuPercent -Sum).Sum, 4)
    peakSampleCpuPercent = [Math]::Round(($samples | Measure-Object cpuPercent -Maximum).Maximum, 4)
    averageWorkingSetMiB = [Math]::Round(($samples | Measure-Object workingSetMiB -Average).Average, 2)
    peakWorkingSetMiB = [Math]::Round(($samples | Measure-Object workingSetMiB -Maximum).Maximum, 2)
    averagePrivateWorkingSetMiB = [Math]::Round(($samples | Measure-Object privateWorkingSetMiB -Average).Average, 2)
    peakPrivateWorkingSetMiB = [Math]::Round(($samples | Measure-Object privateWorkingSetMiB -Maximum).Maximum, 2)
    averagePrivateMiB = [Math]::Round(($samples | Measure-Object privateMiB -Average).Average, 2)
    peakPrivateMiB = [Math]::Round(($samples | Measure-Object privateMiB -Maximum).Maximum, 2)
    processSummaries = @($processSummaries)
    notes = @(
        'CPU includes user and kernel time of all measured processes and is normalized across all logical processors. Average uses full-window counter deltas; roughly one-second sampling can miss shorter peaks.',
        'Private working set is private resident RAM. Working set includes shared pages and summing it can double-count them. Private bytes are committed private memory, not necessarily all resident RAM.',
        'Missing counters and changed process identities fail the measurement instead of being counted as zero.',
        'Only the named processes are measured; separate System/driver activity is not attributed to the application. This is not a gaming FPS or whole-system overhead test.'
    )
    samples = $samples
}
if ($OutputPath) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
$summary = [ordered]@{}
foreach ($key in $result.Keys) { if ($key -ne 'samples') { $summary[$key] = $result[$key] } }
$summary | ConvertTo-Json -Depth 5
