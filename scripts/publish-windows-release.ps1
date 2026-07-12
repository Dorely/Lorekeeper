[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$Notes,

    [string]$NotesFile,

    [switch]$Prerelease,

    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRepository = 'Dorely/Lorekeeper-Releases'
$sourceRepository = 'Dorely/Lorekeeper'
$outputDirectory = Join-Path $repoRoot 'publish\win-x64'
$buildScript = Join-Path $PSScriptRoot 'build-windows-release.ps1'

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')
{
    throw "Version '$Version' must use SemVer form such as 0.2.0 or 0.2.0-beta.1."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue))
{
    throw 'GitHub CLI is required. Install it from https://cli.github.com/ and run gh auth login.'
}

if ($Notes -and $NotesFile)
{
    throw 'Use either -Notes or -NotesFile, not both.'
}

if ($NotesFile)
{
    $NotesFile = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $NotesFile))
    if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf))
    {
        throw "Release notes file was not found: $NotesFile"
    }
}

Push-Location $repoRoot
try
{
    & gh auth status --hostname github.com
    if ($LASTEXITCODE -ne 0)
    {
        throw 'GitHub CLI is not authenticated. Run gh auth login first.'
    }

    $visibility = (& gh repo view $releaseRepository --json visibility --jq '.visibility').Trim()
    if ($LASTEXITCODE -ne 0 -or $visibility -ne 'PUBLIC')
    {
        throw "$releaseRepository must exist and be public; reported visibility was '$visibility'."
    }

    if (-not $AllowDirty)
    {
        $dirtyFiles = @(& git status --porcelain)
        if ($LASTEXITCODE -ne 0)
        {
            throw 'Could not inspect the source repository worktree.'
        }
        if ($dirtyFiles.Count -gt 0)
        {
            throw 'The source worktree has uncommitted changes. Commit them first, or use -AllowDirty intentionally.'
        }
    }

    $tag = "v$Version"
    & gh release view $tag --repo $releaseRepository *> $null
    if ($LASTEXITCODE -eq 0)
    {
        throw "Release $tag already exists in $releaseRepository. Release versions are immutable; choose a newer version."
    }

    & $buildScript -Version $Version

    $artifactNames = @(
        "Lorekeeper-Setup-$Version-x64.exe",
        "Lorekeeper-Setup-$Version-x64.exe.blockmap",
        "Lorekeeper-Portable-$Version-x64.exe",
        'latest.yml',
        'SHA256SUMS.txt'
    )
    $artifactPaths = @(
        $artifactNames | ForEach-Object {
            $path = Join-Path $outputDirectory $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf))
            {
                throw "Expected release artifact was not produced: $path"
            }
            $path
        }
    )

    $sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit))
    {
        throw 'Could not resolve the source commit.'
    }

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
        $releaseArguments += @('--notes', "Automated Lorekeeper Windows release built from [$sourceCommit]($sourceUrl).")
    }

    if ($Prerelease -or $Version.Contains('-'))
    {
        $releaseArguments += '--prerelease'
    }

    $releaseArguments += $artifactPaths
    & gh @releaseArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Could not create draft release $tag."
    }

    $publishArguments = @('release', 'edit', $tag, '--repo', $releaseRepository, '--draft=false')
    if (-not ($Prerelease -or $Version.Contains('-')))
    {
        $publishArguments += '--latest'
    }

    & gh @publishArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Artifacts were uploaded, but $tag remains a draft. Inspect it before publishing manually."
    }

    Write-Host "`nPublished https://github.com/$releaseRepository/releases/tag/$tag" -ForegroundColor Green
}
finally
{
    Pop-Location
}
