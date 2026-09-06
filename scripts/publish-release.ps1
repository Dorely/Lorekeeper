[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int]$MergedPullRequest,

    [string]$Notes,

    [string]$NotesFile,

    [switch]$Prerelease,

    [switch]$WindowsOnly,

    [switch]$ConfirmOpenPullRequests,

    [switch]$AllowDirectMainPush
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
$sourceRepository = 'Dorely/Lorekeeper'
$publicReleaseRepository = 'Dorely/Lorekeeper-Releases'
$releaseRepositories = @($sourceRepository, $publicReleaseRepository)
$workflowName = 'build-macos-release.yml'
$windowsOutputDirectory = Join-Path $repoRoot 'publish\win-x64'
$releaseDirectory = Join-Path $repoRoot "publish\release-$Version"
$correlationId = [Guid]::NewGuid().ToString('N')
$macDownloadDirectory = Join-Path $repoRoot "publish\macos-action-$correlationId"
$macWorkflowArtifactNames = @(
    "macos-$correlationId-arm64"
)
$buildScript = Join-Path $PSScriptRoot 'build-windows-release.ps1'
$macRunId = $null
$createdReleaseRepositories = [System.Collections.Generic.List[string]]::new()

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

function Assert-GitHubApiResourceMissing
{
    param(
        [Parameter(Mandatory)][string]$ApiPath,
        [Parameter(Mandatory)][string]$ExistingMessage,
        [Parameter(Mandatory)][string]$LookupFailureMessage
    )

    $lookupOutputPath = [System.IO.Path]::GetTempFileName()
    $lookupErrorPath = [System.IO.Path]::GetTempFileName()
    try
    {
        $lookupProcess = Start-Process -FilePath (Get-Command gh).Source -ArgumentList @(
            'api', '--include', $ApiPath
        ) -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $lookupOutputPath `
            -RedirectStandardError $lookupErrorPath
        $lookupExitCode = $lookupProcess.ExitCode
        $lookupOutput = @([System.IO.File]::ReadAllLines($lookupOutputPath))
        $lookupError = [System.IO.File]::ReadAllText($lookupErrorPath).Trim()
    }
    finally
    {
        Remove-Item -LiteralPath $lookupOutputPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $lookupErrorPath -Force -ErrorAction SilentlyContinue
    }

    if ($lookupExitCode -eq 0)
    {
        throw $ExistingMessage
    }

    $statusLine = if ($lookupOutput.Count -gt 0) { [string]$lookupOutput[0] } else { '' }
    if (($statusLine -notmatch '^HTTP/\S+ 404 ') -and ($lookupError -notmatch '\(HTTP 404\)'))
    {
        throw "$LookupFailureMessage GitHub returned: $statusLine $lookupError"
    }
}

function Assert-ReleaseTagUnused
{
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag
    )

    Assert-GitHubApiResourceMissing `
        -ApiPath "repos/$Repository/releases/tags/$Tag" `
        -ExistingMessage "Release $Tag already exists in $Repository. Release versions are immutable; choose a newer version." `
        -LookupFailureMessage "Could not confirm that release $Tag is unused in $Repository."
    Assert-GitHubApiResourceMissing `
        -ApiPath "repos/$Repository/git/ref/tags/$Tag" `
        -ExistingMessage "Tag $Tag already exists in $Repository without a matching release. Refusing to reuse it; choose a newer version." `
        -LookupFailureMessage "Could not confirm that tag $Tag is unused in $Repository."
}

function Remove-CreatedReleases
{
    param([Parameter(Mandatory)][string]$Tag)

    foreach ($repository in @($createdReleaseRepositories))
    {
        & gh release view $Tag --repo $repository 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0)
        {
            continue
        }
        Write-Warning "Removing incomplete release $Tag from $repository."
        & gh release delete $Tag --repo $repository --yes --cleanup-tag 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0)
        {
            Write-Warning "Could not remove incomplete release $Tag from $repository; inspect it manually."
        }
    }
}

function Assert-ReleaseAssets
{
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string[]]$ArtifactPaths
    )

    $releaseJson = & gh release view $Tag --repo $Repository --json isDraft,assets
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not inspect draft release $Tag in $Repository."
    }
    $release = ($releaseJson -join [Environment]::NewLine) | ConvertFrom-Json
    if (-not $release.isDraft)
    {
        throw "Release $Tag in $Repository was published before dual-repository verification completed."
    }

    $expectedAssets = @($ArtifactPaths | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [pscustomobject]@{
            Name = $item.Name
            Size = $item.Length
            Digest = "sha256:$((Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash.ToLowerInvariant())"
        }
    })
    $actualNames = @($release.assets | ForEach-Object { [string]$_.name } | Sort-Object)
    $expectedNames = @($expectedAssets | ForEach-Object { $_.Name } | Sort-Object)
    if ($actualNames.Count -ne $expectedNames.Count -or
        @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames).Count -ne 0)
    {
        throw "Release $Tag in $Repository has an unexpected asset set. Expected: $($expectedNames -join ', '). Actual: $($actualNames -join ', ')."
    }

    foreach ($expected in $expectedAssets)
    {
        $actual = @($release.assets | Where-Object { $_.name -eq $expected.Name })
        if ($actual.Count -ne 1 -or
            [string]$actual[0].state -ne 'uploaded' -or
            [long]$actual[0].size -ne $expected.Size -or
            ([string]$actual[0].digest).ToLowerInvariant() -ne $expected.Digest)
        {
            throw "Release asset '$($expected.Name)' in $Repository does not match the staged file's uploaded state, length, and SHA-256 digest."
        }
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

    $visibility = (& gh repo view $publicReleaseRepository --json visibility --jq '.visibility').Trim()
    if ($LASTEXITCODE -ne 0 -or $visibility -ne 'PUBLIC')
    {
        throw "$publicReleaseRepository must exist and be public; reported visibility was '$visibility'."
    }
    & gh repo view $sourceRepository --json nameWithOwner | Out-Null
    if ($LASTEXITCODE -ne 0)
    {
        throw "$sourceRepository must be accessible to publish its matching release."
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
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch))
    {
        throw 'Releases must be published from a named release-orchestration branch.'
    }
    if ($branch -eq 'main')
    {
        throw 'Never publish while main is checked out. Create a fresh release-orchestration branch from origin/main.'
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

    $pullRequestJson = & gh pr view $MergedPullRequest --repo $sourceRepository `
        --json number,state,baseRefName,mergeCommit,title,url
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not inspect release-preparation pull request #$MergedPullRequest."
    }
    $pullRequest = ($pullRequestJson -join [Environment]::NewLine) | ConvertFrom-Json
    if ($pullRequest.state -ne 'MERGED' -or $pullRequest.baseRefName -ne 'main')
    {
        throw "Release-preparation pull request #$MergedPullRequest must be merged into main before a release is possible."
    }
    $pullRequestMergeCommit = [string]$pullRequest.mergeCommit.oid
    if ([string]::IsNullOrWhiteSpace($pullRequestMergeCommit))
    {
        throw "Could not determine the merge commit for release-preparation pull request #$MergedPullRequest."
    }
    if ($pullRequestMergeCommit -ne $sourceCommit -and -not $AllowDirectMainPush)
    {
        throw "Release-preparation pull request #$MergedPullRequest produced $pullRequestMergeCommit, but the release source is $sourceCommit. The merged release-preparation pull request must be the current origin/main commit, or rerun with -AllowDirectMainPush after explicitly confirming the direct main push should be released."
    }
    if ($pullRequestMergeCommit -ne $sourceCommit)
    {
        Write-Warning "Release source $sourceCommit includes direct main changes after release-preparation pull request #$MergedPullRequest merged at $pullRequestMergeCommit. Proceeding after explicit -AllowDirectMainPush confirmation."
    }

    $openPullRequestsJson = & gh pr list --repo $sourceRepository --state open `
        --base main --limit 1000 --json number,title,headRefName,url
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Could not inspect open pull requests targeting main.'
    }
    $openPullRequests = @((
        ($openPullRequestsJson -join [Environment]::NewLine) | ConvertFrom-Json
    ) | ForEach-Object { $_ })
    if ($openPullRequests.Count -gt 0)
    {
        $openPullRequestSummary = @(
            $openPullRequests |
                ForEach-Object { "#$($_.number) $($_.title) [$($_.headRefName)] $($_.url)" }
        ) -join [Environment]::NewLine
        if (-not $ConfirmOpenPullRequests)
        {
            throw "Open pull requests target main. Stop and obtain explicit confirmation before releasing, then rerun with -ConfirmOpenPullRequests if publication should proceed:$([Environment]::NewLine)$openPullRequestSummary"
        }
        Write-Warning "Proceeding after explicit confirmation with open pull requests targeting main:$([Environment]::NewLine)$openPullRequestSummary"
    }

    $tag = "v$Version"
    foreach ($repository in $releaseRepositories)
    {
        Assert-ReleaseTagUnused -Repository $repository -Tag $tag
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
            "Lorekeeper-$Version-arm64.dmg"
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

    $releaseNotesArguments = @()
    if ($NotesFile)
    {
        $releaseNotesArguments += @('--notes-file', $NotesFile)
    }
    elseif ($Notes)
    {
        $releaseNotesArguments += @('--notes', $Notes)
    }
    else
    {
        $sourceUrl = "https://github.com/$sourceRepository/commit/$sourceCommit"
        $platformDescription = if ($WindowsOnly) { 'Windows-only' } else { 'Windows and macOS' }
        $releaseNotesArguments += @('--notes', "Automated Lorekeeper $platformDescription release built from [$sourceCommit]($sourceUrl).")
    }
    $releaseTypeArguments = @()
    if ($Prerelease -or $Version.Contains('-'))
    {
        $releaseTypeArguments += '--prerelease'
    }

    try
    {
        foreach ($repository in $releaseRepositories)
        {
            $target = if ($repository -eq $sourceRepository) { $sourceCommit } else { 'main' }
            $releaseArguments = @(
                'release', 'create', $tag,
                '--repo', $repository,
                '--target', $target,
                '--title', "Lorekeeper $Version",
                '--draft'
            ) + $releaseNotesArguments + $releaseTypeArguments + $artifactPaths
            $createdReleaseRepositories.Add($repository)
            Invoke-Gh $releaseArguments
        }

        foreach ($repository in $releaseRepositories)
        {
            Assert-ReleaseAssets -Repository $repository -Tag $tag -ArtifactPaths $artifactPaths
        }

        foreach ($repository in $releaseRepositories)
        {
            $publishArguments = @('release', 'edit', $tag, '--repo', $repository, '--draft=false')
            if (-not ($Prerelease -or $Version.Contains('-')))
            {
                $publishArguments += '--latest'
            }
            Invoke-Gh $publishArguments
        }
    }
    catch
    {
        Remove-CreatedReleases -Tag $tag
        throw "Dual-repository publication failed and Lorekeeper attempted to remove every release and tag created for $tag. $($_.Exception.Message)"
    }

    if (-not $WindowsOnly)
    {
        Remove-MacRunArtifacts
        Remove-GeneratedDirectory $macDownloadDirectory
    }
    Write-Host "`nPublished matching releases:" -ForegroundColor Green
    foreach ($repository in $releaseRepositories)
    {
        Write-Host "  https://github.com/$repository/releases/tag/$tag"
    }
    Write-Host "Release staging retained at $releaseDirectory"
}
finally
{
    Pop-Location
}
