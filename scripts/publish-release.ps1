[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$Notes,

    [string]$NotesFile,

    [switch]$Prerelease,

    [switch]$WindowsOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'The unified release must be orchestrated from Windows.'
}
$semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Version -notmatch $semVerPattern)
{
    throw "Version '$Version' must use SemVer form such as 0.2.0 or 0.2.0-beta.1."
}
if ($Notes -and $NotesFile)
{
    throw 'Use either -Notes or -NotesFile, not both.'
}

foreach ($commandName in @('git', 'gh', 'dotnet', 'node', 'npm.cmd'))
{
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue))
    {
        throw "Required release command '$commandName' was not found on PATH."
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRepository = 'Dorely/Lorekeeper-Releases'
$sourceRepository = 'Dorely/Lorekeeper'
$workflowName = 'build-macos-release.yml'
$windowsOutputDirectory = Join-Path $repoRoot 'publish\win-x64'
$releaseDirectory = Join-Path $repoRoot "publish\release-$Version"
$correlationId = [Guid]::NewGuid().ToString('N')
$macDownloadDirectory = Join-Path $repoRoot "publish\macos-action-$correlationId"
$macWorkflowArtifactNames = @(
    "macos-$correlationId-arm64",
    "macos-$correlationId-x64"
)
$buildScript = Join-Path $PSScriptRoot 'build-windows-release.ps1'
$macRunId = $null

if ($NotesFile)
{
    $NotesFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $NotesFile))
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf))
    {
        throw "Release notes file was not found: $NotesFile"
    }
}

function Remove-GeneratedDirectory
{
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $repoPrefix = $repoRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not ($fullPath + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
        $repoPrefix,
        [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to remove generated path outside the repository: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath)
    {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-Gh
{
    param([Parameter(Mandatory)][string[]]$Arguments)

    Write-Host "`n> gh $($Arguments -join ' ')" -ForegroundColor Cyan
    & gh @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "GitHub CLI failed with exit code $LASTEXITCODE."
    }
}

function Stop-MacRun
{
    if ($macRunId)
    {
        Write-Warning "Cancelling macOS workflow run $macRunId because the local release failed."
        & gh run cancel $macRunId --repo $sourceRepository 2>$null | Out-Null
    }
}

function Remove-MacRunArtifacts
{
    if (-not $macRunId)
    {
        return
    }

    $artifactIds = @(
        & gh api "repos/$sourceRepository/actions/runs/$macRunId/artifacts?per_page=100" `
            --paginate `
            --jq '.artifacts[].id'
    )
    if ($LASTEXITCODE -ne 0)
    {
        Write-Warning "The release was published, but the temporary artifacts for macOS workflow run $macRunId could not be listed. They will expire after one day."
        return
    }

    $artifactIds = @($artifactIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $deletedCount = 0
    foreach ($artifactId in $artifactIds)
    {
        & gh api --method DELETE "repos/$sourceRepository/actions/artifacts/$artifactId" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0)
        {
            Write-Warning "The release was published, but temporary Actions artifact $artifactId could not be deleted. It will expire after one day."
            continue
        }
        $deletedCount++
    }

    Write-Host "Deleted $deletedCount temporary macOS Actions artifact(s) from workflow run $macRunId."
}

Push-Location $repoRoot
try
{
    Invoke-Gh @('auth', 'status', '--hostname', 'github.com')

    $visibility = (& gh repo view $releaseRepository --json visibility --jq '.visibility').Trim()
    if ($LASTEXITCODE -ne 0 -or $visibility -ne 'PUBLIC')
    {
        throw "$releaseRepository must exist and be public; reported visibility was '$visibility'."
    }
    if (-not $WindowsOnly)
    {
        $actionsEnabled = (& gh api "repos/$sourceRepository/actions/permissions" --jq '.enabled').Trim()
        if ($LASTEXITCODE -ne 0 -or $actionsEnabled -ne 'true')
        {
            throw "GitHub Actions must be enabled for $sourceRepository."
        }
        $workflowState = (& gh api "repos/$sourceRepository/actions/workflows/$workflowName" --jq '.state').Trim()
        if ($LASTEXITCODE -ne 0 -or $workflowState -ne 'active')
        {
            throw "The $workflowName workflow must exist and be active on GitHub. Commit and push the release setup first."
        }
    }

    $dirtyFiles = @(& git status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the source worktree.' }
    if ($dirtyFiles.Count -gt 0)
    {
        throw 'The source worktree has uncommitted changes. Commit and push them before publishing a release.'
    }

    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'main')
    {
        throw "Releases must be published from the main branch; current branch is '$branch'."
    }

    & git fetch origin main --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not refresh origin/main.' }
    $sourceCommit = (& git rev-parse HEAD).Trim()
    $remoteCommit = (& git rev-parse origin/main).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit))
    {
        throw 'Could not resolve the source commit.'
    }
    if ($sourceCommit -ne $remoteCommit)
    {
        throw "Local HEAD ($sourceCommit) must exactly match origin/main ($remoteCommit)."
    }

    $tag = "v$Version"
    $releaseLookupOutputPath = [System.IO.Path]::GetTempFileName()
    $releaseLookupErrorPath = [System.IO.Path]::GetTempFileName()
    try
    {
        $releaseLookupProcess = Start-Process -FilePath (Get-Command gh).Source -ArgumentList @(
            'api', '--include', "repos/$releaseRepository/releases/tags/$tag"
        ) -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $releaseLookupOutputPath `
            -RedirectStandardError $releaseLookupErrorPath
        $releaseLookupExitCode = $releaseLookupProcess.ExitCode
        $releaseLookup = @([System.IO.File]::ReadAllLines($releaseLookupOutputPath))
        $releaseLookupError = [System.IO.File]::ReadAllText($releaseLookupErrorPath).Trim()
    }
    finally
    {
        Remove-Item -LiteralPath $releaseLookupOutputPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $releaseLookupErrorPath -Force -ErrorAction SilentlyContinue
    }
    if ($releaseLookupExitCode -eq 0)
    {
        throw "Release $tag already exists in $releaseRepository. Release versions are immutable; choose a newer version."
    }
    $releaseStatusLine = if ($releaseLookup.Count -gt 0) { [string]$releaseLookup[0] } else { '' }
    if (($releaseStatusLine -notmatch '^HTTP/\S+ 404 ') -and ($releaseLookupError -notmatch '\(HTTP 404\)'))
    {
        throw "Could not confirm that release $tag is unused. GitHub returned: $releaseStatusLine $releaseLookupError"
    }

    if (-not $WindowsOnly)
    {
        Remove-GeneratedDirectory $macDownloadDirectory
    }
    Remove-GeneratedDirectory $releaseDirectory

    if (-not $WindowsOnly)
    {
        $runTitle = "macOS $Version ($correlationId)"
        Invoke-Gh @(
            'workflow', 'run', $workflowName,
            '--repo', $sourceRepository,
            '--ref', 'main',
            '-f', "version=$Version",
            '-f', "source_commit=$sourceCommit",
            '-f', "correlation_id=$correlationId"
        )

        for ($attempt = 0; $attempt -lt 30 -and -not $macRunId; $attempt++)
        {
            $runsJson = & gh run list --repo $sourceRepository --workflow $workflowName --event workflow_dispatch --limit 30 `
                --json databaseId,displayTitle,createdAt
            if ($LASTEXITCODE -ne 0) { throw 'Could not list macOS workflow runs.' }
            $runs = ($runsJson -join [Environment]::NewLine) | ConvertFrom-Json
            $matchingRuns = @(
                $runs |
                    Where-Object { $_.displayTitle -eq $runTitle } |
                    Sort-Object createdAt -Descending
            )
            if ($matchingRuns.Count -gt 0)
            {
                $macRunId = [string]$matchingRuns[0].databaseId
                break
            }
            Start-Sleep -Seconds 2
        }
        if (-not $macRunId)
        {
            throw "Dispatched the macOS build but could not resolve its correlated workflow run for $correlationId."
        }
        Write-Host "macOS workflow run: https://github.com/$sourceRepository/actions/runs/$macRunId" -ForegroundColor Cyan
    }

    try
    {
        & $buildScript -Version $Version
        if ($LASTEXITCODE -ne 0) { throw "Windows release build failed with exit code $LASTEXITCODE." }
    }
    catch
    {
        Stop-MacRun
        throw
    }

    if (-not $WindowsOnly)
    {
        Invoke-Gh @('run', 'watch', $macRunId, '--repo', $sourceRepository, '--compact', '--exit-status')
        New-Item -ItemType Directory -Path $macDownloadDirectory | Out-Null
        foreach ($artifactName in $macWorkflowArtifactNames)
        {
            Invoke-Gh @(
                'run', 'download', $macRunId,
                '--repo', $sourceRepository,
                '--name', $artifactName,
                '--dir', $macDownloadDirectory
            )
        }
    }

    $windowsArtifactNames = @(
        "Lorekeeper-Setup-$Version-x64.exe",
        "Lorekeeper-Setup-$Version-x64.exe.blockmap",
        "Lorekeeper-Portable-$Version-x64.exe",
        'latest.yml'
    )
    $macArtifactNames = @()
    if (-not $WindowsOnly)
    {
        $macArtifactNames = @(
            "Lorekeeper-$Version-arm64.dmg",
            "Lorekeeper-$Version-x64.dmg"
        )
    }

    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    foreach ($artifactName in $windowsArtifactNames)
    {
        $sourcePath = Join-Path $windowsOutputDirectory $artifactName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
        {
            throw "Expected Windows release artifact was not produced: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $releaseDirectory $artifactName)
    }
    foreach ($artifactName in $macArtifactNames)
    {
        $sourcePath = Join-Path $macDownloadDirectory $artifactName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
        {
            throw "Expected macOS release artifact was not downloaded: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $releaseDirectory $artifactName)
    }

    $releaseArtifacts = @(Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name)
    if ($releaseArtifacts.Count -ne ($windowsArtifactNames.Count + $macArtifactNames.Count))
    {
        throw "Release staging contains an unexpected artifact count: $($releaseArtifacts.Count)."
    }
    $checksumLines = foreach ($artifact in $releaseArtifacts)
    {
        $hash = Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $artifact.Name
    }
    $checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllLines($checksumPath, $checksumLines, [System.Text.UTF8Encoding]::new($false))
    $artifactPaths = @(
        Get-ChildItem -LiteralPath $releaseDirectory -File |
            Sort-Object Name |
            Select-Object -ExpandProperty FullName
    )

    $releaseArguments = @(
        'release', 'create', $tag,
        '--repo', $releaseRepository,
        '--target', 'main',
        '--title', "Lorekeeper $Version",
        '--draft'
    )
    if ($NotesFile)
    {
        $releaseArguments += @('--notes-file', $NotesFile)
    }
    elseif ($Notes)
    {
        $releaseArguments += @('--notes', $Notes)
    }
    else
    {
        $sourceUrl = "https://github.com/$sourceRepository/commit/$sourceCommit"
        $platformDescription = if ($WindowsOnly) { 'Windows-only' } else { 'Windows and macOS' }
        $releaseArguments += @('--notes', "Automated Lorekeeper $platformDescription release built from [$sourceCommit]($sourceUrl).")
    }
    if ($Prerelease -or $Version.Contains('-'))
    {
        $releaseArguments += '--prerelease'
    }
    $releaseArguments += $artifactPaths
    Invoke-Gh $releaseArguments

    $publishArguments = @('release', 'edit', $tag, '--repo', $releaseRepository, '--draft=false')
    if (-not ($Prerelease -or $Version.Contains('-')))
    {
        $publishArguments += '--latest'
    }
    try
    {
        Invoke-Gh $publishArguments
    }
    catch
    {
        throw "Artifacts were uploaded, but $tag remains a draft. Inspect it before publishing manually."
    }

    if (-not $WindowsOnly)
    {
        Remove-MacRunArtifacts
        Remove-GeneratedDirectory $macDownloadDirectory
    }
    Write-Host "`nPublished https://github.com/$releaseRepository/releases/tag/$tag" -ForegroundColor Green
    Write-Host "Release staging retained at $releaseDirectory"
}
finally
{
    Pop-Location
}
