[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [Parameter(Mandatory)]
    [string]$FixtureDirectory,

    [Parameter(Mandatory)]
    [string]$ResultsDirectory,

    [ValidateRange(1, 10000)]
    [int]$SampleIntervalMilliseconds = 250,

    [ValidateRange(0, 100)]
    [int]$WarmupSamples = 5,

    [ValidateRange(1, 1000)]
    [int]$MeasuredSamples = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SampleIntervalMilliseconds -ne 250)
{
    throw 'M0.3 captures process memory every 250 ms; do not change SampleIntervalMilliseconds.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$fixtureGenerator = Join-Path $PSScriptRoot 'Lorekeeper.PerformanceFixtures.csproj'

function Resolve-PerformanceArtifactDirectory
{
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [switch]$RequireExisting
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($RequireExisting -and -not (Test-Path -LiteralPath $fullPath -PathType Container))
    {
        throw "Expected an existing directory: $fullPath"
    }

    $cursor = $fullPath
    while ($true)
    {
        $name = [System.IO.Path]::GetFileName($cursor)
        if ($name -ieq '.artifacts')
        {
            $relative = [System.IO.Path]::GetRelativePath($cursor, $fullPath)
            if ($relative -ieq 'performance' -or $relative.StartsWith("performance$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase))
            {
                return $fullPath
            }
            break
        }

        $parent = [System.IO.Directory]::GetParent($cursor)
        if ($null -eq $parent)
        {
            break
        }
        $cursor = $parent.FullName
    }

    throw "M0.3 paths are allowed only under .artifacts/performance: $fullPath"
}

function Get-ProcessTreeIds
{
    param([Parameter(Mandatory)][int]$RootProcessId)

    $processes = Get-CimInstance Win32_Process | ForEach-Object {
        [pscustomobject]@{
            ProcessId = [int]$_.ProcessId
            ParentProcessId = [int]$_.ParentProcessId
        }
    }
    $childrenByParent = @{}
    foreach ($process in $processes)
    {
        if (-not $childrenByParent.ContainsKey($process.ParentProcessId))
        {
            $childrenByParent[$process.ParentProcessId] = [System.Collections.Generic.List[int]]::new()
        }
        $childrenByParent[$process.ParentProcessId].Add($process.ProcessId)
    }

    $ids = [System.Collections.Generic.List[int]]::new()
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    while ($pending.Count -gt 0)
    {
        $processId = $pending.Dequeue()
        $ids.Add($processId)
        if ($childrenByParent.ContainsKey($processId))
        {
            foreach ($childId in $childrenByParent[$processId])
            {
                $pending.Enqueue($childId)
            }
        }
    }

    return $ids
}

function Get-MemorySnapshot
{
    param([Parameter(Mandatory)][int]$RootProcessId)

    $members = foreach ($processId in Get-ProcessTreeIds $RootProcessId)
    {
        try
        {
            $process = Get-Process -Id $processId -ErrorAction Stop
            [pscustomobject]@{
                processId = $process.Id
                processName = $process.ProcessName
                workingSetBytes = $process.WorkingSet64
                privateMemoryBytes = $process.PrivateMemorySize64
            }
        }
        catch [Microsoft.PowerShell.Commands.ProcessCommandException]
        {
            # The process ended between the CIM tree snapshot and Get-Process.
        }
    }

    [pscustomobject]@{
        capturedAtUtc = [DateTime]::UtcNow.ToString('O')
        processes = @($members)
        workingSetBytes = @($members | Measure-Object -Property workingSetBytes -Sum).Sum
        privateMemoryBytes = @($members | Measure-Object -Property privateMemoryBytes -Sum).Sum
    }
}

function Get-NearestRankP95
{
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Events)

    if ($Events.Count -eq 0)
    {
        return $null
    }

    $values = @($Events | ForEach-Object { [double]$_.durationMilliseconds } | Sort-Object)
    return $values[[Math]::Ceiling($values.Count * 0.95) - 1]
}

function Write-Json
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value
    )

    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

$packageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
$fixtureDirectory = Resolve-PerformanceArtifactDirectory $FixtureDirectory -RequireExisting
$resultsDirectory = Resolve-PerformanceArtifactDirectory $ResultsDirectory
$fixtureManifest = Join-Path $fixtureDirectory 'm0.3-fixture-manifest.json'
$executable = Join-Path $packageDirectory 'Lorekeeper.exe'
if (-not (Test-Path -LiteralPath $fixtureGenerator -PathType Leaf))
{
    throw "Fixture generator is missing: $fixtureGenerator"
}
if (-not (Test-Path -LiteralPath $fixtureManifest -PathType Leaf))
{
    throw "Fixture manifest is missing: $fixtureManifest"
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf))
{
    throw "Packaged Release executable is missing: $executable"
}
if (Test-Path -LiteralPath $resultsDirectory)
{
    throw "Results directory already exists; choose a new ignored .artifacts/performance directory: $resultsDirectory"
}

& dotnet run --project $fixtureGenerator -- --output $fixtureDirectory --verify
if ($LASTEXITCODE -ne 0)
{
    throw "Fixture manifest validation failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$runtimeDirectory = Join-Path $resultsDirectory 'runtime'
$historyDirectory = Join-Path $resultsDirectory 'history'
$electronUserDataDirectory = Join-Path $runtimeDirectory 'electron-user-data'
New-Item -ItemType Directory -Path $runtimeDirectory, $historyDirectory, $electronUserDataDirectory -Force | Out-Null
$tracePath = Join-Path $resultsDirectory 'raw-trace.jsonl'
$databasePath = Join-Path $runtimeDirectory 'lorekeeper.db'

$packageFiles = Get-ChildItem -LiteralPath $packageDirectory -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            relativePath = [System.IO.Path]::GetRelativePath($packageDirectory, $_.FullName).Replace('\', '/')
            byteLength = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
$computerSystem = Get-CimInstance Win32_ComputerSystem
$processors = @(Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name)
$operatingSystem = Get-CimInstance Win32_OperatingSystem
$buildInputs = [pscustomobject]@{
    capturedAtUtc = [DateTime]::UtcNow.ToString('O')
    gitRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
    packageDirectory = $packageDirectory
    executableVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
    files = @($packageFiles)
}
$hostConfiguration = [pscustomobject]@{
    computerName = $env:COMPUTERNAME
    operatingSystem = $operatingSystem.Caption
    operatingSystemVersion = $operatingSystem.Version
    operatingSystemBuild = $operatingSystem.BuildNumber
    processors = $processors
    logicalProcessors = $computerSystem.NumberOfLogicalProcessors
    physicalMemoryBytes = [int64]$computerSystem.TotalPhysicalMemory
    dotnet = (& dotnet --info)
}
Write-Json (Join-Path $resultsDirectory 'release-package-inputs.json') $buildInputs
Write-Json (Join-Path $resultsDirectory 'host-configuration.json') $hostConfiguration

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.WorkingDirectory = $packageDirectory
$startInfo.UseShellExecute = $false
$startInfo.ArgumentList.Add("--user-data-dir=$electronUserDataDirectory")
$startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
$startInfo.Environment['Desktop__UsePerUserDataDirectory'] = 'false'
$startInfo.Environment['ConnectionStrings__DefaultConnection'] = "Data Source=$databasePath"
$startInfo.Environment['VersionHistory__HistoryRoot'] = $historyDirectory
$startInfo.Environment['Diagnostics__PerformanceTracePath'] = $tracePath
$startInfo.Environment['Diagnostics__PerformanceTraceLimitPerMetric'] = ($WarmupSamples + $MeasuredSamples).ToString([System.Globalization.CultureInfo]::InvariantCulture)
$startInfo.Environment['Logging__LogLevel__Default'] = 'Error'
$startInfo.Environment['Logging__LogLevel__Microsoft'] = 'Error'

$process = [System.Diagnostics.Process]::Start($startInfo)
if ($null -eq $process)
{
    throw 'The packaged Electron application did not start.'
}

Write-Host "M0.3 measurement app started as PID $($process.Id). Use only the production import UI to load fixtures, perform five warm-ups and then $MeasuredSamples measured samples for each metric, then close the Electron app." -ForegroundColor Cyan
$memorySamples = [System.Collections.Generic.List[object]]::new()
try
{
    while (-not $process.HasExited)
    {
        $memorySamples.Add((Get-MemorySnapshot $process.Id))
        Start-Sleep -Milliseconds $SampleIntervalMilliseconds
        $process.Refresh()
    }
}
finally
{
    Write-Json (Join-Path $resultsDirectory 'process-memory-250ms.json') @($memorySamples)
}

if (-not (Test-Path -LiteralPath $tracePath -PathType Leaf))
{
    throw 'The diagnostic trace was not written. Confirm the packaged app received the isolated trace path.'
}

$events = @(Get-Content -LiteralPath $tracePath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
    $_ | ConvertFrom-Json
})
$expectedMetrics = @(
    'input-to-visible-frame',
    'save-acknowledgment',
    'undo-to-visible-frame',
    'redo-to-visible-frame',
    'editor-ready-after-navigation'
)
$metricSummaries = foreach ($metric in $expectedMetrics)
{
    $allEvents = @($events | Where-Object { $_.metric -eq $metric })
    $measuredEvents = @($allEvents | Select-Object -Skip $WarmupSamples -First $MeasuredSamples)
    [pscustomobject]@{
        metric = $metric
        warmupSamplesDiscarded = [Math]::Min($WarmupSamples, $allEvents.Count)
        recordedSamples = $allEvents.Count
        measuredSamples = $measuredEvents.Count
        p95MillisecondsNearestRank = Get-NearestRankP95 -Events $measuredEvents
        expectedMeasuredSamples = $MeasuredSamples
        complete = $allEvents.Count -eq ($WarmupSamples + $MeasuredSamples)
    }
}
$summary = [pscustomobject]@{
    capturedAtUtc = [DateTime]::UtcNow.ToString('O')
    fixtureManifestSha256 = (Get-FileHash -LiteralPath $fixtureManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    tracePath = $tracePath
    memorySampleIntervalMilliseconds = $SampleIntervalMilliseconds
    warmupSamples = $WarmupSamples
    expectedMeasuredSamples = $MeasuredSamples
    metrics = @($metricSummaries)
}
Write-Json (Join-Path $resultsDirectory 'measurement-summary.json') $summary

if (@($metricSummaries | Where-Object { -not $_.complete }).Count -gt 0)
{
    throw 'M0.3 measurement did not capture exactly the requested post-warmup sample count for every metric. Raw artifacts were retained for diagnosis.'
}
