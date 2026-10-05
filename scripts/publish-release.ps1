[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$Notes,

    [string]$NotesFile,

    [switch]$Prerelease,

    [switch]$WindowsOnly,

    [string]$LinuxDistribution = 'Ubuntu',

    [switch]$MakeSourcePublicAfterV1,

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
if ($MakeSourcePublicAfterV1 -and $Version -cne '1.0.0')
{
    throw '-MakeSourcePublicAfterV1 is only valid for the v1.0.0 launch.'
}
if ($Version -ceq '1.0.0' -and $Prerelease)
{
    throw 'The v1.0.0 bridge must be a stable release.'
}

foreach ($commandName in @('git', 'gh', 'dotnet', 'node', 'npm.cmd'))
{
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue))
    {
        throw "Required release command '$commandName' was not found on PATH."
    }
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'eng/ReleaseWorkflow.ps1')
if ((Get-LorekeeperVersion (Join-Path $repoRoot 'Lorekeeper/Lorekeeper.csproj')) -ne $Version)
{
    throw 'The requested release version must match Lorekeeper.csproj. Use scripts/release.ps1 to prepare it.'
}
$sourceRepository = 'Dorely/Lorekeeper'
$releaseRepositories = @(Get-LorekeeperReleaseRepositories $Version)
$workflowName = 'build-macos-release.yml'
$windowsOutputDirectory = Join-Path $repoRoot 'publish\win-x64'
$linuxOutputDirectory = Join-Path $repoRoot 'publish\linux-x64'
$releaseDirectory = Join-Path $repoRoot "publish\release-$Version"
$correlationId = [Guid]::NewGuid().ToString('N')
$macDownloadDirectory = Join-Path $repoRoot "publish\macos-action-$correlationId"
$macWorkflowArtifactNames = @(
    "macos-$correlationId-arm64"
)
$buildScript = Join-Path $PSScriptRoot 'build-windows-release.ps1'
$linuxBuildScript = Join-Path $PSScriptRoot 'build-linux-release-wsl.ps1'
$macRunId = $null
$generatedNotesPath = $null
$createdReleaseRepositories = [System.Collections.Generic.List[string]]::new()
$sourceWasPrivate = $false

if ($NotesFile)
{
    $NotesFile = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).Path, $NotesFile))
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


function Remove-CreatedReleases
{
    param([Parameter(Mandatory)][string]$Tag)

    foreach ($repository in @($createdReleaseRepositories))
    {
        $releaseState = & gh release view $Tag --repo $repository --json isDraft 2>$null
        if ($LASTEXITCODE -ne 0)
        {
            continue
        }
        if (-not (($releaseState -join [Environment]::NewLine) | ConvertFrom-Json).isDraft)
        {
            Write-Warning "Retaining published release $Tag in $repository. Resolve the launch failure before announcing or retrying."
            continue
        }
        Write-Warning "Removing incomplete draft $Tag from $repository."
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
        throw "Release $Tag in $Repository was published before staged-asset verification completed."
    }

    Assert-ReleaseArtifactMetadata -Release $release -ArtifactPaths $ArtifactPaths
}

function Assert-ReleaseArtifactMetadata
{
    param(
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string[]]$ArtifactPaths
    )

    $expectedAssets = @($ArtifactPaths | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [pscustomobject]@{
            Name = $item.Name
            Size = $item.Length
            Digest = "sha256:$((Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash.ToLowerInvariant())"
        }
    })
    $actualNames = @($Release.assets | ForEach-Object { [string]$_.name } | Sort-Object)
    $expectedNames = @($expectedAssets | ForEach-Object { $_.Name } | Sort-Object)
    if ($actualNames.Count -ne $expectedNames.Count -or
        @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames).Count -ne 0)
    {
        throw "Release has an unexpected asset set. Expected: $($expectedNames -join ', '). Actual: $($actualNames -join ', ')."
    }

    foreach ($expected in $expectedAssets)
    {
        $actual = @($Release.assets | Where-Object { $_.name -eq $expected.Name })
        if ($actual.Count -ne 1 -or
            [string]$actual[0].state -ne 'uploaded' -or
            [long]$actual[0].size -ne $expected.Size -or
            ([string]$actual[0].digest).ToLowerInvariant() -ne $expected.Digest)
        {
            throw "Release asset '$($expected.Name)' does not match the staged file's uploaded state, length, and SHA-256 digest."
        }
    }
}

function Assert-AnonymousLatestRelease
{
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string[]]$ArtifactPaths
    )

    # This client carries no GitHub token, cookies, or current-user credentials.
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseDefaultCredentials = $false
    $handler.UseCookies = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Lorekeeper-Release/1.0')
    try
    {
        $release = $null
        for ($attempt = 1; $attempt -le 3; $attempt++)
        {
            try
            {
                $json = $client.GetStringAsync("https://api.github.com/repos/$Repository/releases/latest").GetAwaiter().GetResult()
                $candidate = $json | ConvertFrom-Json
                if ($candidate.tag_name -cne $Tag -or $candidate.draft -or $candidate.prerelease)
                {
                    throw "Anonymous latest release is not the stable $Tag release."
                }
                $release = $candidate
                break
            }
            catch
            {
                if ($attempt -eq 3) { throw }
                Write-Host "Waiting for anonymous release visibility in $Repository (attempt $attempt)."
                Start-Sleep -Seconds 10
            }
        }
        Assert-ReleaseArtifactMetadata -Release $release -ArtifactPaths $ArtifactPaths
        foreach ($artifactPath in $ArtifactPaths)
        {
            $artifactName = [System.IO.Path]::GetFileName($artifactPath)
            $asset = @($release.assets | Where-Object { $_.name -ceq $artifactName })[0]
            $expectedPrefix = "https://github.com/$Repository/releases/download/$Tag/"
            if (-not ([string]$asset.browser_download_url).StartsWith($expectedPrefix, [StringComparison]::Ordinal))
            {
                throw "The anonymous asset URL is outside the published release: $artifactName"
            }
            Write-Host "Verifying anonymous download: $Repository/$artifactName"
            $cancellation = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(30))
            try
            {
                $response = $client.GetAsync([string]$asset.browser_download_url,
                    [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancellation.Token).GetAwaiter().GetResult()
                try
                {
                    [void]$response.EnsureSuccessStatusCode()
                    $stream = $response.Content.ReadAsStreamAsync($cancellation.Token).GetAwaiter().GetResult()
                    $sha256 = [System.Security.Cryptography.SHA256]::Create()
                    try
                    {
                        $hashBytes = $sha256.ComputeHashAsync($stream, $cancellation.Token).GetAwaiter().GetResult()
                        $downloadHash = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
                    }
                    finally
                    {
                        $sha256.Dispose()
                        $stream.Dispose()
                    }
                    if ($downloadHash -cne (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant())
                    {
                        throw "Anonymous download SHA-256 does not match the staged artifact: $artifactName"
                    }
                }
                finally { $response.Dispose() }
            }
            finally { $cancellation.Dispose() }
        }
    }
    finally { $client.Dispose() }
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

    foreach ($repository in $releaseRepositories)
    {
        $repositoryJson = @(& gh api "repos/$repository")
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect repository metadata for $repository." }
        $repositoryInfo = ($repositoryJson -join [Environment]::NewLine) | ConvertFrom-Json
        $visibility = ([string]$repositoryInfo.visibility).ToUpperInvariant()
        if ($visibility -ne 'PUBLIC')
        {
            if ($repository -ne $sourceRepository -or
                $Version -cne '1.0.0' -or $visibility -ne 'PRIVATE')
            {
                throw "$repository must exist and be public; reported visibility was '$visibility'."
            }
            if (-not $MakeSourcePublicAfterV1)
            {
                throw 'The private-main v1.0.0 launch requires -MakeSourcePublicAfterV1.'
            }
            if ($repositoryInfo.permissions.admin -ne $true)
            {
                throw 'GitHub administrative permission on Dorely/Lorekeeper is required to make the source public after v1.0.0 publication.'
            }
            $sourceWasPrivate = $true
        }
    }
    if (-not $WindowsOnly)
    {
        if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue))
        {
            throw 'WSL2 is required for the native Linux release build.'
        }
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
        throw 'Releases must be published from main.'
    }
    foreach ($url in @(& git remote get-url --all origin) + @(& git remote get-url --push --all origin))
    {
        if ($LASTEXITCODE -ne 0 -or $url -notmatch '^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)Dorely/Lorekeeper(?:\.git)?$')
        {
            throw 'origin must fetch and push only Dorely/Lorekeeper on github.com.'
        }
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

    if (-not $AllowDirectMainPush) { throw 'Direct publication requires -AllowDirectMainPush.' }
    Write-Host "Publishing explicitly authorized source $sourceCommit from origin/main."

    $tag = "v$Version"
    foreach ($repository in $releaseRepositories)
    {
        Assert-ReleaseTagUnused -Repository $repository -Tag $tag
    }
    Invoke-ReleasePreflight $repoRoot
    if (@(& git status --porcelain).Count -gt 0 -or (& git rev-parse HEAD).Trim() -ne $sourceCommit)
    {
        throw 'The source changed during release preflight.'
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

    & $buildScript -Version $Version
    if (-not $WindowsOnly)
    {
        & $linuxBuildScript -Version $Version -Distribution $LinuxDistribution
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
    $linuxArtifactNames = @()
    if (-not $WindowsOnly)
    {
        $macArtifactNames = @(
            "Lorekeeper-$Version-arm64.dmg"
        )
        $linuxArtifactNames = @(
            "Lorekeeper-$Version-x86_64.AppImage",
            "Lorekeeper-$Version-x86_64.AppImage.sha256",
            "Lorekeeper-$Version-amd64.deb",
            "Lorekeeper-$Version-amd64.deb.sha256",
            'release-provenance.json'
        )
        $provenance = Get-Content -LiteralPath (Join-Path $linuxOutputDirectory 'release-provenance.json') -Raw | ConvertFrom-Json
        $sourceTree = (& git rev-parse 'HEAD^{tree}').Trim()
        if ($LASTEXITCODE -ne 0 -or [string]$provenance.sourceCommit -ne $sourceCommit -or
            [string]$provenance.sourceTree -ne $sourceTree -or
            [string]$provenance.sourceVersion -ne $Version -or
            [string]$provenance.architecture -ne 'x64' -or
            [string]$provenance.platform -ne 'linux' -or
            [string]$provenance.sourceArchiveSha256 -notmatch '^[a-f0-9]{64}$')
        {
            throw 'The WSL Linux artifact provenance does not match the release source commit.'
        }
        foreach ($artifactName in @("Lorekeeper-$Version-x86_64.AppImage", "Lorekeeper-$Version-amd64.deb"))
        {
            $artifactPath = Join-Path $linuxOutputDirectory $artifactName
            $checksumText = [System.IO.File]::ReadAllText("$artifactPath.sha256").Trim()
            $expectedHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($checksumText -cne "$expectedHash  $artifactName")
            {
                throw "The Linux artifact checksum does not match $artifactName."
            }
        }
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
    foreach ($artifactName in $linuxArtifactNames)
    {
        $sourcePath = Join-Path $linuxOutputDirectory $artifactName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
        {
            throw "Expected Linux release artifact was not produced: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $releaseDirectory $artifactName)
    }

    $releaseArtifacts = @(Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name)
    if ($releaseArtifacts.Count -ne ($windowsArtifactNames.Count + $macArtifactNames.Count + $linuxArtifactNames.Count))
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

    $notesText = if ($NotesFile) { [System.IO.File]::ReadAllText($NotesFile) } elseif ($Notes) { $Notes } else
    {
        $sourceUrl = "https://github.com/$sourceRepository/commit/$sourceCommit"
        $platformDescription = if ($WindowsOnly) { 'Windows-only' } else { 'Windows, Linux, and macOS' }
        "Lorekeeper $platformDescription release built from [$sourceCommit]($sourceUrl)."
    }
    if ($Version -ceq '1.0.0')
    {
        $notesText += "`n`nThis is the final release mirrored in Lorekeeper-Releases. Future downloads and update checks use [Dorely/Lorekeeper](https://github.com/Dorely/Lorekeeper/releases)."
    }
    $generatedNotesPath = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($generatedNotesPath, $notesText, [System.Text.UTF8Encoding]::new($false))
    $releaseNotesArguments = @('--notes-file', $generatedNotesPath)
    $releaseTypeArguments = @()
    if ($Prerelease -or $Version.Contains('-'))
    {
        $releaseTypeArguments += '--prerelease'
    }

    try
    {
        & git fetch origin --prune --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Could not refresh origin before publication.' }
        $finalHead = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve HEAD before publication.' }
        $finalRemoteHead = (& git rev-parse origin/main).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve origin/main before publication.' }
        $finalChanges = @(& git status --porcelain)
        if ($LASTEXITCODE -ne 0 -or $finalChanges.Count -gt 0 -or
            $finalHead -ne $sourceCommit -or $finalRemoteHead -ne $sourceCommit)
        {
            throw 'Release source changed during packaging. Verify the new source before publishing.'
        }
        foreach ($repository in $releaseRepositories)
        {
            Assert-ReleaseTagUnused -Repository $repository -Tag $tag
            $target = if ($repository -eq $sourceRepository) { $sourceCommit } else { 'main' }
            $releaseArguments = @(
                'release', 'create', $tag,
                '--repo', $repository,
                '--target', $target,
                '--title', "Lorekeeper $Version",
                '--draft'
            ) + $releaseNotesArguments + $releaseTypeArguments + $artifactPaths
            Invoke-Gh $releaseArguments
            $createdReleaseRepositories.Add($repository)
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
        throw "Release publication failed. Lorekeeper attempted to remove only drafts created in this run; finalized releases are retained. Inspect $tag in $($releaseRepositories -join ', ') before retrying or announcing. $($_.Exception.Message)"
    }

    if ($Version -ceq '1.0.0')
    {
        try
        {
            if ($sourceWasPrivate)
            {
                Invoke-Gh @('repo', 'edit', $sourceRepository, '--visibility', 'public', '--accept-visibility-change-consequences')
            }
            foreach ($repository in $releaseRepositories)
            {
                Assert-AnonymousLatestRelease -Repository $repository -Tag $tag -ArtifactPaths $artifactPaths
            }
        }
        catch
        {
            throw "The v1.0.0 releases were published and are retained. Public cutover or anonymous download verification failed; inspect visibility and downloads before announcing. $($_.Exception.Message)"
        }
    }

    if (-not $WindowsOnly)
    {
        Remove-MacRunArtifacts
        Remove-GeneratedDirectory $macDownloadDirectory
    }
    Write-Host "`nPublished release:" -ForegroundColor Green
    foreach ($repository in $releaseRepositories)
    {
        Write-Host "  https://github.com/$repository/releases/tag/$tag"
    }
    Write-Host "Release staging retained at $releaseDirectory"
}
catch
{
    Stop-MacRun
    throw
}
finally
{
    if ($generatedNotesPath) { Remove-Item -LiteralPath $generatedNotesPath -Force -ErrorAction SilentlyContinue }
    Pop-Location
}
