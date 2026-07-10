[CmdletBinding()]
param(
    [string]$Version,
    [switch]$KeepUnpacked
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'Windows release packages must be built on Windows.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repoRoot 'Lorekeeper.sln'
$projectPath = Join-Path $repoRoot 'Lorekeeper\Lorekeeper.csproj'
$stageDirectory = Join-Path $repoRoot 'publish\win-x64-stage'
$outputDirectory = Join-Path $repoRoot 'publish\win-x64'

if ([string]::IsNullOrWhiteSpace($Version))
{
    $projectText = [System.IO.File]::ReadAllText($projectPath)
    $versionMatch = [regex]::Match($projectText, '<Version>(?<version>[^<]+)</Version>')
    if (-not $versionMatch.Success)
    {
        throw "Could not read <Version> from $projectPath."
    }

    $Version = $versionMatch.Groups['version'].Value.Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')
{
    throw "Version '$Version' must use SemVer form such as 0.2.0 or 0.2.0-beta.1."
}

foreach ($commandName in @('dotnet', 'node', 'npm.cmd'))
{
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue))
    {
        throw "Required build command '$commandName' was not found on PATH."
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
        Write-Host "Cleaning $fullPath"
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Invoke-CheckedCommand
{
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "`n> $FilePath $($Arguments -join ' ')" -ForegroundColor Cyan
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repoRoot
try
{
    Remove-GeneratedDirectory $stageDirectory
    Remove-GeneratedDirectory $outputDirectory

    Invoke-CheckedCommand dotnet @(
        'build',
        $solutionPath,
        '-c', 'Release',
        "-p:Version=$Version"
    )

    Invoke-CheckedCommand dotnet @(
        'publish',
        $projectPath,
        '-c', 'Release',
        '-p:PublishProfile=win-x64',
        "-p:Version=$Version",
        '--no-restore'
    )

    $installerPath = Join-Path $outputDirectory "Lorekeeper-Setup-$Version-x64.exe"
    $portablePath = Join-Path $outputDirectory "Lorekeeper-Portable-$Version-x64.exe"
    $artifacts = @($installerPath, $portablePath)

    foreach ($artifact in $artifacts)
    {
        if (-not (Test-Path -LiteralPath $artifact -PathType Leaf))
        {
            throw "Expected release artifact was not produced: $artifact"
        }
    }

    $checksumLines = foreach ($artifact in $artifacts)
    {
        $hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), (Split-Path $artifact -Leaf)
    }

    $checksumPath = Join-Path $outputDirectory 'SHA256SUMS.txt'
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines($checksumPath, $checksumLines, $utf8WithoutBom)

    if (-not $KeepUnpacked)
    {
        Remove-GeneratedDirectory (Join-Path $outputDirectory 'win-unpacked')
    }

    Remove-GeneratedDirectory $stageDirectory

    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid)
    {
        Write-Warning 'The installer is not code-signed. Windows will show an unknown-publisher warning.'
    }

    Write-Host "`nWindows release $Version is ready:" -ForegroundColor Green
    foreach ($artifact in $artifacts)
    {
        $item = Get-Item -LiteralPath $artifact
        Write-Host ("  {0} ({1:N1} MB)" -f $item.FullName, ($item.Length / 1MB))
    }
    Write-Host "  $checksumPath"
    Write-Host "`nUpload the Setup executable and SHA256SUMS.txt to the GitHub Release for v$Version."
}
finally
{
    Pop-Location
}
