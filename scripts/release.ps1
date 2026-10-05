<#
.SYNOPSIS
Prepares and publishes a stable Lorekeeper release from main in one command.
.DESCRIPTION
Invocation authorizes a version commit, a normal push to main, native builds,
and publication to the main repository (also the old download repository for v1.0.0 only). Defaults to the next patch version,
or reuses an already prepared unpublished version. Never commits unrelated files.
.EXAMPLE
.\scripts\release.ps1 -CheckOnly
.EXAMPLE
.\scripts\release.ps1
.EXAMPLE
.\scripts\release.ps1 -Bump Minor -WindowsOnly
#>
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [ValidateSet('Patch', 'Minor', 'Major')]
    [string]$Bump = 'Patch',
    [switch]$WindowsOnly,
    [string]$LinuxDistribution = 'Ubuntu',
    [switch]$MakeSourcePublicAfterV1,
    [switch]$CheckOnly,
    [string]$Notes,
    [string]$NotesFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repoRoot 'eng/ReleaseWorkflow.ps1')
$projectRelativePath = 'Lorekeeper/Lorekeeper.csproj'
$projectPath = Join-Path $repoRoot $projectRelativePath
$sourceRepository = 'Dorely/Lorekeeper'
$repositories = @($sourceRepository)
$stablePattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'Run the release driver on Windows; Linux uses WSL2 and macOS uses its native Actions runner.'
}
if ($Version -and $PSBoundParameters.ContainsKey('Bump')) { throw 'Use -Version or -Bump, not both.' }
if ($Notes -and $NotesFile) { throw 'Use -Notes or -NotesFile, not both.' }
if ($NotesFile)
{
    $NotesFile = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine((Get-Location).Path, $NotesFile))
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "Missing notes file: $NotesFile" }
    $PSBoundParameters['NotesFile'] = $NotesFile
}

function Assert-CleanReleaseTree
{
    $changes = @(Invoke-ReleaseCommand git @('status', '--porcelain'))
    if ($changes.Count -gt 0)
    {
        throw "Commit or resolve existing changes before releasing. The driver will not stage unrelated work:`n$($changes -join "`n")"
    }
}

function Get-StableReleaseVersion
{
    param([Parameter(Mandatory)][string]$Repository)

    $tags = @(Invoke-ReleaseCommand gh @(
        'api', "repos/$Repository/releases?per_page=100", '--paginate',
        '--jq', '.[] | select(.draft == false and .prerelease == false) | .tag_name'
    ))
    $tagPattern = '^v' + $stablePattern.Substring(1)
    $versions = @($tags | Where-Object { $_ -cmatch $tagPattern } |
        ForEach-Object { [Version]$_.Substring(1) } | Sort-Object -Descending)
    if ($versions.Count -eq 0) { return [Version]'0.0.0' }
    return $versions[0]
}

$releaseLock = $null
$originalProjectBytes = $null
$writtenProjectHash = $null
$preparationHead = $null
Push-Location $repoRoot
try
{
    foreach ($command in @('git', 'gh', 'dotnet', 'node', 'npm.cmd', 'cargo', 'rustc'))
    {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Install required command: $command" }
    }
    if ((Invoke-ReleaseCommand git @('branch', '--show-current')) -ne 'main')
    {
        throw 'Check out main before running the release driver. It never switches branches or merges work branches.'
    }
    Assert-CleanReleaseTree
    $originUrls = @(Invoke-ReleaseCommand git @('remote', 'get-url', '--all', 'origin')) +
        @(Invoke-ReleaseCommand git @('remote', 'get-url', '--push', '--all', 'origin'))
    foreach ($url in $originUrls)
    {
        if ($url -notmatch '^(https://github\.com/|git@github\.com:|ssh://git@github\.com/)Dorely/Lorekeeper(?:\.git)?$')
        {
            throw 'origin must fetch and push only Dorely/Lorekeeper on github.com.'
        }
    }
    if (-not $CheckOnly)
    {
        Invoke-ReleaseCommand git @('var', 'GIT_AUTHOR_IDENT') | Out-Null
        Invoke-ReleaseCommand git @('var', 'GIT_COMMITTER_IDENT') | Out-Null
        $lockDirectory = Join-Path $repoRoot '.artifacts/release-driver'
        [System.IO.Directory]::CreateDirectory($lockDirectory) | Out-Null
        $releaseLock = [System.IO.File]::Open((Join-Path $lockDirectory 'run.lock'),
            [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    }

    Invoke-ReleaseCommand git @('fetch', 'origin', '--prune')
    $head = Invoke-ReleaseCommand git @('rev-parse', 'HEAD')
    $counts = (Invoke-ReleaseCommand git @('rev-list', '--left-right', '--count', 'HEAD...origin/main')) -split '\s+'
    if ([int]$counts[0] -gt 0 -and [int]$counts[1] -gt 0)
    {
        throw 'main and origin/main have diverged. Integrate the remote changes before releasing; history will not be rewritten.'
    }
    if ([int]$counts[1] -gt 0)
    {
        if ($CheckOnly) { throw 'main is behind origin/main. Run without -CheckOnly to fast-forward it, then inspect the resulting plan.' }
        Invoke-ReleaseCommand git @('merge', '--ff-only', 'origin/main')
        # Reload after fetching changed scripts rather than continuing with an old driver in memory.
        Write-Host 'main was fast-forwarded. Restarting the current release driver.'
        $releaseLock.Dispose()
        $releaseLock = $null
        & (Join-Path $repoRoot 'scripts/release.ps1') @PSBoundParameters
        return
    }

    Invoke-ReleaseCommand gh @('auth', 'status', '--hostname', 'github.com')
    foreach ($repository in $repositories)
    {
        $info = (Invoke-ReleaseCommand gh @('api', "repos/$repository") | Out-String) | ConvertFrom-Json
        if (-not $info.permissions.push) { throw "GitHub write access is required for $repository." }
    }
    if (-not $WindowsOnly)
    {
        $enabled = Invoke-ReleaseCommand gh @('api', "repos/$sourceRepository/actions/permissions", '--jq', '.enabled')
        $workflow = Invoke-ReleaseCommand gh @('api', "repos/$sourceRepository/actions/workflows/build-macos-release.yml", '--jq', '.state')
        if ($enabled -ne 'true' -or $workflow -ne 'active') { throw 'Enable the macOS release workflow before releasing.' }
        if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { throw 'Install WSL2 and the configured Linux distribution before releasing.' }
    }

    $sdk = [Version](Invoke-ReleaseCommand dotnet @('--version'))
    $requiredSdk = [Version]((Get-Content -Raw (Join-Path $repoRoot 'global.json') | ConvertFrom-Json).sdk.version)
    if ($sdk.Major -ne $requiredSdk.Major -or $sdk -lt $requiredSdk) { throw "Install the .NET SDK required by global.json ($requiredSdk)." }
    $nodeVersion = [Version]((Invoke-ReleaseCommand node @('--version')).TrimStart('v'))
    if ($nodeVersion -lt [Version]'22.12.0') { throw 'Node.js 22.12 or later is required.' }
    Push-Location (Join-Path $repoRoot 'Lorekeeper.Press')
    try { Invoke-ReleaseCommand rustc @('--version') }
    finally { Pop-Location }

    $sourceVersion = Get-StableReleaseVersion $sourceRepository
    $previousRepositories = @($sourceRepository)
    if ($sourceVersion -le [Version]'1.0.0')
    {
        $publicVersion = Get-StableReleaseVersion 'Dorely/Lorekeeper-Releases'
        if ($sourceVersion -ne $publicVersion)
        {
            throw "The pre-transition repositories disagree on the latest stable release ($sourceVersion / $publicVersion). Resolve incomplete publication before creating another release."
        }
        $previousRepositories += 'Dorely/Lorekeeper-Releases'
    }
    $currentVersion = Get-LorekeeperVersion $projectPath
    if ($currentVersion -notmatch $stablePattern) { throw 'The one-command driver requires a stable project version. Use the lower-level publisher for prereleases.' }
    if ([Version]$currentVersion -lt $sourceVersion) { throw 'The project version is older than the latest published release. Update main before releasing.' }
    if ($sourceVersion -gt [Version]'0.0.0')
    {
        $previousAssets = $null
        foreach ($repository in $previousRepositories)
        {
            $release = (Invoke-ReleaseCommand gh @('api', "repos/$repository/releases/tags/v$sourceVersion") |
                Out-String) | ConvertFrom-Json
            $assets = @($release.assets | Sort-Object name)
            if ($release.draft -or $release.prerelease -or $assets.Count -lt 5 -or
                @($assets | Where-Object { $_.state -ne 'uploaded' -or $_.size -le 0 -or -not $_.digest }).Count -gt 0)
            {
                throw "Latest release v$sourceVersion in $repository is incomplete. Inspect it before publishing another release."
            }
            $assetSignature = ($assets | ForEach-Object { "$($_.name) $($_.size) $($_.digest)" }) -join "`n"
            if ($null -ne $previousAssets -and $previousAssets -cne $assetSignature)
            {
                throw "The repositories have different assets for v$sourceVersion. Resolve the incomplete publication first."
            }
            $previousAssets = $assetSignature
        }
        $publishedHead = Invoke-ReleaseCommand gh @('api', "repos/$sourceRepository/commits/v$sourceVersion", '--jq', '.sha')
        Invoke-ReleaseCommand git @('merge-base', '--is-ancestor', $publishedHead, 'HEAD')
        if ($publishedHead -eq $head -and -not $Version -and -not $PSBoundParameters.ContainsKey('Bump'))
        {
            Write-Host "Current main is already published as v$sourceVersion. Nothing to release." -ForegroundColor Green
            return
        }
    }
    if (-not $Version)
    {
        if ([Version]$currentVersion -gt $sourceVersion -and -not $PSBoundParameters.ContainsKey('Bump'))
        {
            $Version = $currentVersion
        }
        else
        {
            $baseline = [Version]$currentVersion
            $Version = switch ($Bump)
            {
                'Major' { '{0}.0.0' -f ($baseline.Major + 1) }
                'Minor' { '{0}.{1}.0' -f $baseline.Major, ($baseline.Minor + 1) }
                'Patch' { '{0}.{1}.{2}' -f $baseline.Major, $baseline.Minor, ($baseline.Build + 1) }
            }
        }
    }
    if ([Version]$Version -le $sourceVersion -or [Version]$Version -lt [Version]$currentVersion)
    {
        throw "Version $Version must be newer than published $sourceVersion and cannot decrease project version $currentVersion."
    }
    $repositories = @(Get-LorekeeperReleaseRepositories $Version)
    if ($MakeSourcePublicAfterV1 -and $Version -cne '1.0.0')
    {
        throw '-MakeSourcePublicAfterV1 is only valid for the v1.0.0 launch.'
    }
    foreach ($repository in $repositories)
    {
        $info = (Invoke-ReleaseCommand gh @('api', "repos/$repository") | Out-String) | ConvertFrom-Json
        if (-not $info.permissions.push) { throw "GitHub write access is required for $repository." }
        if ($info.private)
        {
            if ($repository -ne $sourceRepository -or $Version -cne '1.0.0')
            {
                throw "$repository must be public for this release."
            }
            if (-not $CheckOnly -and -not $MakeSourcePublicAfterV1)
            {
                throw 'The private-main v1.0.0 launch requires -MakeSourcePublicAfterV1 to authorize making the source public after both releases are published.'
            }
            if ($MakeSourcePublicAfterV1 -and $info.permissions.admin -ne $true)
            {
                throw 'GitHub administrative permission on Dorely/Lorekeeper is required to make the source public after v1.0.0 publication.'
            }
            Write-Host 'v1 launch: publish both verified releases, make the source public, then verify anonymous downloads before announcement.'
        }
        Assert-ReleaseTagUnused -Repository $repository -Tag "v$Version"
    }
    if (@(Invoke-ReleaseCommand git @('tag', '--list', "v$Version")).Count -gt 0)
    {
        throw "Local tag v$Version already exists. Inspect it before releasing; tags will not be overwritten."
    }
    $platforms = if ($WindowsOnly) { 'Windows x64' } else { "Windows x64, Linux x64 (WSL2/$LinuxDistribution), and macOS arm64" }
    Write-Host "Release plan: $currentVersion -> $Version; $platforms; main -> $($repositories -join ', ')." -ForegroundColor Green
    Write-Host 'The driver will verify, commit only the project version if needed, verify again, push main, build, and publish.'
    if (-not $WindowsOnly)
    {
        $null = Assert-AppImagePublicationReady -RepositoryRoot $repoRoot -CheckOnly:$CheckOnly
        & (Join-Path $PSScriptRoot 'build-linux-release-wsl.ps1') -Distribution $LinuxDistribution -CheckOnly
    }
    if ($CheckOnly)
    {
        Invoke-ReleasePreflight $repoRoot
        Assert-CleanReleaseTree
        if ((Invoke-ReleaseCommand git @('rev-parse', 'HEAD')) -ne $head) { throw 'HEAD changed during preview verification.' }
        return
    }

    Assert-CleanReleaseTree
    $preparationHead = Invoke-ReleaseCommand git @('rev-parse', 'HEAD')
    if ($preparationHead -ne $head) { throw 'HEAD changed during release preflight. Rerun from the new state.' }
    if ($Version -ne $currentVersion)
    {
        $originalProjectBytes = [System.IO.File]::ReadAllBytes($projectPath)
        $projectText = [System.IO.File]::ReadAllText($projectPath)
        $updatedText = $projectText.Replace("<Version>$currentVersion</Version>", "<Version>$Version</Version>")
        [System.IO.File]::WriteAllText($projectPath, $updatedText, [System.Text.UTF8Encoding]::new($false))
        $writtenProjectHash = (Get-FileHash -LiteralPath $projectPath -Algorithm SHA256).Hash
        Invoke-ReleasePreflight $repoRoot
        Invoke-ReleaseCommand git @('diff', '--check')
        $changedFiles = @(Invoke-ReleaseCommand git @('diff', '--name-only', 'HEAD'))
        if ($changedFiles.Count -ne 1 -or $changedFiles[0] -ne $projectRelativePath -or
            @(Invoke-ReleaseCommand git @('diff', '--cached', '--name-only')).Count -gt 0 -or
            @(Invoke-ReleaseCommand git @('ls-files', '--others', '--exclude-standard')).Count -gt 0 -or
            (Invoke-ReleaseCommand git @('rev-parse', 'HEAD')) -ne $preparationHead -or
            (Get-FileHash -LiteralPath $projectPath -Algorithm SHA256).Hash -ne $writtenProjectHash)
        {
            throw 'The worktree changed during verification. Inspect it before releasing; no version commit was created.'
        }
        Invoke-ReleaseCommand git @('diff', '--', $projectRelativePath)
        Invoke-ReleaseCommand git @('add', '--', $projectRelativePath)
        Invoke-ReleaseCommand git @('commit', '-m', "Prepare Lorekeeper $Version release", '--', $projectRelativePath)
        $originalProjectBytes = $null
    }

    Assert-CleanReleaseTree
    $releaseHead = Invoke-ReleaseCommand git @('rev-parse', 'HEAD')
    Invoke-ReleasePreflight $repoRoot
    Assert-CleanReleaseTree
    if ((Invoke-ReleaseCommand git @('rev-parse', 'HEAD')) -ne $releaseHead) { throw 'HEAD changed during release verification.' }
    Invoke-ReleaseCommand git @('fetch', 'origin', '--prune')
    Invoke-ReleaseCommand git @('merge-base', '--is-ancestor', 'origin/main', 'HEAD')
    Invoke-ReleaseCommand git @('push', 'origin', 'HEAD:refs/heads/main')
    Invoke-ReleaseCommand git @('fetch', 'origin', '--prune')
    if ((Invoke-ReleaseCommand git @('rev-parse', 'origin/main')) -ne $releaseHead) { throw 'origin/main moved after the push. Rerun verification on its new state.' }
    $publishArguments = @{ Version = $Version; AllowDirectMainPush = $true }
    if ($WindowsOnly) { $publishArguments.WindowsOnly = $true }
    $publishArguments.LinuxDistribution = $LinuxDistribution
    if ($MakeSourcePublicAfterV1) { $publishArguments.MakeSourcePublicAfterV1 = $true }
    if ($Notes) { $publishArguments.Notes = $Notes }
    if ($NotesFile) { $publishArguments.NotesFile = $NotesFile }
    & (Join-Path $PSScriptRoot 'publish-release.ps1') @publishArguments
    Assert-CleanReleaseTree
    Write-Host "Release $Version completed: https://github.com/$sourceRepository/releases/tag/v$Version" -ForegroundColor Green
}
catch
{
    # Restore only our exact unstaged edit; preserve anything another process changed.
    if ($null -ne $originalProjectBytes -and $writtenProjectHash -and
        (Get-FileHash -LiteralPath $projectPath -Algorithm SHA256).Hash -eq $writtenProjectHash -and
        (Invoke-ReleaseCommand git @('rev-parse', 'HEAD')) -eq $preparationHead -and
        @(Invoke-ReleaseCommand git @('diff', '--cached', '--name-only')).Count -eq 0)
    {
        [System.IO.File]::WriteAllBytes($projectPath, $originalProjectBytes)
        Write-Warning 'Restored the uncommitted version edit after failure.'
    }
    Write-Warning 'Release stopped. Committed preparation is retained; rerunning reuses an unpublished project version. No force push or tag overwrite is performed.'
    throw
}
finally
{
    if ($releaseLock) { $releaseLock.Dispose() }
    Pop-Location
}
