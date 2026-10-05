[CmdletBinding()]
param(
    [string]$Version,
    [string]$Distribution = 'Ubuntu',
    [string]$ToolchainRoot,
    [switch]$CheckOnly,
    [switch]$KeepUnpacked
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'This wrapper runs on Windows. On Ubuntu 24.04 x64, invoke build-linux-release.ps1 directly.'
}
foreach ($command in @('wsl.exe', 'git'))
{
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Missing release command: $command" }
}
if ([string]::IsNullOrWhiteSpace($Distribution) -or $Distribution -match '[\r\n]')
{
    throw 'Specify the installed WSL2 Ubuntu 24.04 distribution name.'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'Lorekeeper/Lorekeeper.csproj'
$outputDirectory = Join-Path $repoRoot 'publish/linux-x64'
. (Join-Path $repoRoot 'eng/ReleaseWorkflow.ps1')
$sourceVersion = Get-LorekeeperVersion -ProjectPath $projectPath
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }

function Invoke-WslCommand
{
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Capture)

    if ($Capture)
    {
        $result = @(& wsl.exe --distribution $Distribution --exec @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "WSL command '$($Arguments[0])' failed with exit code $LASTEXITCODE in '$Distribution'." }
        return ($result -join "`n").Trim()
    }
    & wsl.exe --distribution $Distribution --exec @Arguments
    if ($LASTEXITCODE -ne 0) { throw "WSL command '$($Arguments[0])' failed with exit code $LASTEXITCODE in '$Distribution'." }
}

function Invoke-WslBuildCommand
{
    param([Parameter(Mandatory)][string[]]$Arguments)

    Invoke-WslCommand -Arguments (@('env') + $buildEnvironment + $Arguments)
}

function Convert-ToWslPath
{
    param([Parameter(Mandatory)][string]$Path)
    return Invoke-WslCommand -Arguments @('wslpath', '-a', '-u', $Path) -Capture
}

function Remove-GeneratedDirectory
{
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedRoots = @((Join-Path $repoRoot '.artifacts/linux-release-wsl'), (Join-Path $repoRoot 'publish'))
    $allowed = $false
    foreach ($root in $allowedRoots)
    {
        $prefix = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $allowed = $true }
    }
    if (-not $allowed) { throw "Refusing to remove a path outside generated Linux release directories: $fullPath" }
    if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
}

$distributionList = (@(& wsl.exe --list --verbose) -join "`n").Replace([string][char]0, '')
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect installed WSL distributions.' }
$distributionPattern = '(?m)^\s*\*?\s*' + [regex]::Escape($Distribution) + '\s+\S+\s+2\s*$'
if ($distributionList -notmatch $distributionPattern)
{
    throw "'$Distribution' must be an installed WSL2 distribution (Ubuntu 24.04 x64)."
}
$nativeScriptPath = Convert-ToWslPath -Path (Join-Path $PSScriptRoot 'build-linux-release.ps1')
$wslHome = Invoke-WslCommand -Arguments @('printenv', 'HOME') -Capture
if ([string]::IsNullOrWhiteSpace($ToolchainRoot)) { $ToolchainRoot = "$wslHome/.cache/lorekeeper-toolchains/v1" }
if (-not $ToolchainRoot.StartsWith('/') -or $ToolchainRoot -match '[\r\n:]')
{
    throw 'ToolchainRoot must be an absolute Linux path without line breaks or PATH separators.'
}
$ToolchainRoot = $ToolchainRoot.TrimEnd('/')
$existingPath = Invoke-WslCommand -Arguments @('printenv', 'PATH') -Capture
$buildEnvironment = @(
    "PATH=$ToolchainRoot/powershell:$ToolchainRoot/dotnet:$ToolchainRoot/node/bin:$ToolchainRoot/rust/bin:$existingPath",
    "DOTNET_ROOT=$ToolchainRoot/dotnet",
    "DOTNET_CLI_HOME=$ToolchainRoot/dotnet-home",
    "NUGET_PACKAGES=$ToolchainRoot/nuget-cache",
    "CARGO_HOME=$ToolchainRoot/cargo-cache",
    'CARGO_BUILD_JOBS=2',
    "npm_config_cache=$ToolchainRoot/npm-cache"
)
Invoke-WslBuildCommand -Arguments @('pwsh', '-NoProfile', '-NonInteractive', '-File', $nativeScriptPath, '-Version', $Version, '-CheckOnly')
if ($CheckOnly)
{
    Write-Host "WSL2 '$Distribution' is ready for a local Linux release build." -ForegroundColor Green
    return
}
if ($Version -cne $sourceVersion) { throw "Version $Version differs from the committed project version $sourceVersion." }
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the release source commit.' }
$sourceTree = (& git -C $repoRoot rev-parse 'HEAD^{tree}').Trim()
if ($LASTEXITCODE -ne 0 -or @(& git -C $repoRoot status --porcelain).Count -ne 0)
{
    throw 'The local WSL release builder requires a clean committed source tree.'
}
$runId = [Guid]::NewGuid().ToString('N')
$hostRunRoot = Join-Path $repoRoot ".artifacts/linux-release-wsl/$runId"
$incomingDirectory = Join-Path $repoRoot "publish/linux-x64-incoming-$runId"
$sourceArchive = Join-Path $hostRunRoot 'source.tar'
$bootstrapPath = Join-Path $hostRunRoot 'build-isolated.ps1'
New-Item -ItemType Directory -Path $hostRunRoot -Force | Out-Null
New-Item -ItemType Directory -Path $incomingDirectory -Force | Out-Null

# The helper runs all Linux file operations within one PowerShell process. Only
# the immutable source archive and finished artifacts cross the Windows mount.
$bootstrap = @'
param(
    [Parameter(Mandatory)][string]$SourceArchive,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$SourceTree,
    [Parameter(Mandatory)][string]$SourceArchiveSha256,
    [switch]$KeepUnpacked
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $env:HOME '.cache/lorekeeper-releases'))
$runRoot = Join-Path $cacheRoot ([Guid]::NewGuid().ToString('N'))
if (-not $runRoot.StartsWith($cacheRoot + '/', [StringComparison]::Ordinal))
{
    throw 'The isolated Linux build path escaped its owner directory.'
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
try
{
    $filesystem = (& stat -f -c '%T' $runRoot).Trim()
    if ($LASTEXITCODE -ne 0 -or $filesystem -cne 'ext2/ext3')
    {
        throw 'The WSL release workspace must be on the distribution ext4 filesystem.'
    }
    $archivePath = Join-Path $runRoot 'source.tar'
    Copy-Item -LiteralPath $SourceArchive -Destination $archivePath
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceArchiveSha256)
    {
        throw 'The WSL source archive fingerprint differs from the Windows export.'
    }
    $sourceRoot = Join-Path $runRoot 'source'
    New-Item -ItemType Directory -Path $sourceRoot | Out-Null
    & tar --extract --file $archivePath --directory $sourceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not extract the committed release source archive.' }
    $arguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $sourceRoot 'scripts/build-linux-release.ps1'),
        '-Version', $Version, '-SourceCommit', $SourceCommit, '-SourceTree', $SourceTree,
        '-SourceArchiveSha256', $SourceArchiveSha256, '-SourceArchivePath', $archivePath)
    if ($KeepUnpacked) { $arguments += '-KeepUnpacked' }
    & pwsh @arguments
    if ($LASTEXITCODE -ne 0) { throw "The isolated Linux release build failed with exit code $LASTEXITCODE." }
    $resultRoot = Join-Path $sourceRoot 'publish/linux-x64'
    foreach ($item in @(Get-ChildItem -LiteralPath $resultRoot -Force))
    {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}
finally
{
    $resolvedRunRoot = [IO.Path]::GetFullPath($runRoot)
    if (-not $resolvedRunRoot.StartsWith($cacheRoot + '/', [StringComparison]::Ordinal))
    {
        throw 'Refusing to clean an isolated build outside its owner directory.'
    }
    if (Test-Path -LiteralPath $resolvedRunRoot) { Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force }
}
'@

try
{
    [IO.File]::WriteAllText($bootstrapPath, $bootstrap, [Text.UTF8Encoding]::new($false))
    & git -c core.autocrlf=false -c core.eol=lf -C $repoRoot archive --format=tar "--output=$sourceArchive" $sourceCommit
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the exact committed release source.' }
    $archiveHash = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $bootstrapArguments = @('pwsh', '-NoProfile', '-NonInteractive', '-File', (Convert-ToWslPath -Path $bootstrapPath),
        '-SourceArchive', (Convert-ToWslPath -Path $sourceArchive), '-Destination', (Convert-ToWslPath -Path $incomingDirectory),
        '-Version', $Version, '-SourceCommit', $sourceCommit, '-SourceTree', $sourceTree, '-SourceArchiveSha256', $archiveHash)
    if ($KeepUnpacked) { $bootstrapArguments += '-KeepUnpacked' }
    Invoke-WslBuildCommand -Arguments $bootstrapArguments
    if ((& git -C $repoRoot rev-parse HEAD).Trim() -cne $sourceCommit -or @(& git -C $repoRoot status --porcelain).Count -ne 0)
    {
        throw 'The Windows source tree changed during the isolated Linux build; refusing to accept its artifacts.'
    }
    $provenance = Get-Content -Raw -LiteralPath (Join-Path $incomingDirectory 'release-provenance.json') | ConvertFrom-Json
    if ($provenance.sourceCommit -cne $sourceCommit -or $provenance.sourceTree -cne $sourceTree -or
        $provenance.sourceArchiveSha256 -cne $archiveHash -or $provenance.sourceVersion -cne $sourceVersion -or
        $provenance.sourceArchiveConfiguration.format -cne 'tar' -or
        $provenance.sourceArchiveConfiguration.coreAutocrlf -ne $false -or
        $provenance.sourceArchiveConfiguration.coreEol -cne 'lf')
    {
        throw 'The copied Linux artifacts have mismatched source provenance.'
    }
    foreach ($artifact in @($provenance.artifacts))
    {
        if ([IO.Path]::GetFileName([string]$artifact.name) -cne [string]$artifact.name -or
            (Get-FileHash -LiteralPath (Join-Path $incomingDirectory $artifact.name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $artifact.sha256)
        {
            throw 'The copied Linux release artifact fingerprint failed.'
        }
    }
    Remove-GeneratedDirectory -Path $outputDirectory
    Move-Item -LiteralPath $incomingDirectory -Destination $outputDirectory
    Write-Host "`nLocal WSL Linux release $Version is ready in $outputDirectory (source $sourceCommit)." -ForegroundColor Green
}
finally
{
    Remove-GeneratedDirectory -Path $hostRunRoot
    Remove-GeneratedDirectory -Path $incomingDirectory
}
