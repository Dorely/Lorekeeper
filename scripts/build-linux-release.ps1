[CmdletBinding()]
param(
    [string]$Version,
    [switch]$CheckOnly,
    [switch]$KeepUnpacked,
    [string]$SourceCommit,
    [string]$SourceTree,
    [string]$SourceArchiveSha256,
    [string]$SourceArchivePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux) -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64)
{
    throw 'Linux releases must be built natively on Linux x64; use build-linux-release-wsl.ps1 from Windows.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'Lorekeeper/Lorekeeper.csproj'
$stageDirectory = Join-Path $repoRoot 'publish/linux-x64-stage'
$outputDirectory = Join-Path $repoRoot 'publish/linux-x64'
$appImageToolsetDirectory = Join-Path $repoRoot 'publish/linux-appimage-tools'
$appImageManifestPath = Join-Path $repoRoot 'licenses/appimage-runtime/sources.json'
$appImageManifest = Get-Content -LiteralPath $appImageManifestPath -Raw | ConvertFrom-Json
$electronBuilderConfiguration = Get-Content -LiteralPath (Join-Path $repoRoot 'Lorekeeper/Properties/electron-builder.json') -Raw | ConvertFrom-Json
$appImageToolsetVersion = [string]$electronBuilderConfiguration.toolsets.appimage
$appImageToolsetSha256 = [string]$appImageManifest.toolset.sha256
$appImageToolsetUrl = [string]$appImageManifest.toolset.url
$appImageRuntimeSha256 = [string]$appImageManifest.runtime.sha256
$appImageRuntimeSourceCommit = [string]$appImageManifest.runtime.commit
$appImageRuntimeRelease = [string]$appImageManifest.runtime.release
if ($appImageManifest.formatVersion -ne 1 -or $appImageToolsetVersion -cne '1.0.3' -or
    [string]$appImageManifest.toolset.version -cne "appimage@$appImageToolsetVersion" -or
    $appImageToolsetSha256 -cnotmatch '^[a-f0-9]{64}$' -or $appImageRuntimeSha256 -cnotmatch '^[a-f0-9]{64}$' -or
    $appImageRuntimeSourceCommit -cnotmatch '^[a-f0-9]{40}$' -or $appImageRuntimeRelease -cnotmatch '^\d{8}$')
{
    throw 'The retained AppImage runtime/toolset identity is malformed or differs from the supported pinned configuration.'
}
$appImageArchiveName = "appimage-tools-runtime-$appImageRuntimeRelease.tar.gz"
if ($appImageToolsetUrl -cne "https://github.com/electron-userland/electron-builder-binaries/releases/download/appimage%40$appImageToolsetVersion/$appImageArchiveName" -or
    [string]$appImageManifest.runtime.officialAssetUrl -cne "https://github.com/AppImage/type2-runtime/releases/download/$appImageRuntimeRelease/runtime-x86_64")
{
    throw 'AppImage artifacts must use the official GitHub supplier host and exact selected release path.'
}
$runtimeComponents = @($appImageManifest.components | Where-Object component -CEQ 'AppImage type2 runtime')
$libfuseComponents = @($appImageManifest.components | Where-Object component -CEQ 'libfuse')
if ($runtimeComponents.Count -ne 1 -or $libfuseComponents.Count -ne 1 -or
    [string]$runtimeComponents[0].version -cne $appImageRuntimeRelease -or
    [string]$libfuseComponents[0].version -cne [string]$appImageManifest.modifiedLibfuse.version)
{
    throw 'The selected AppImage runtime or modified-libfuse version differs from its retained component notices.'
}
$semanticEditorDirectory = Join-Path $repoRoot 'tools/semantic-editor'
$semanticEditorBundle = Join-Path $repoRoot 'Lorekeeper/wwwroot/js/semantic-editor.bundle.js'
$semanticEditorNotice = Join-Path $repoRoot 'Lorekeeper/wwwroot/js/semantic-editor.NOTICES.txt'
$optionalLttngProviderVersion = '10.0.12'
$optionalLttngProviderSha256 = '8fc124b76a54a8f3c9ee941035ad1c716b99c845170331900984c069529660bc'
$optionalLttngSource = 'https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/runtime/src/coreclr/pal/src/misc/tracepointprovider.cpp#L109-L114'
$optionalLttngUnavailable = $false
. (Join-Path $repoRoot 'eng/ReleaseDependencyAudit.ps1')
. (Join-Path $repoRoot 'eng/ReleaseWorkflow.ps1')

$sourceVersion = Get-LorekeeperVersion -ProjectPath $projectPath
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')
{
    throw "Version '$Version' must be a release SemVer such as 1.0.0 or 1.0.0-beta.1."
}

function Invoke-CheckedCommand
{
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$WorkingDirectory = $repoRoot
    )

    Write-Host "`n> $FilePath $($Arguments -join ' ')" -ForegroundColor Cyan
    Push-Location $WorkingDirectory
    try
    {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Command '$FilePath' failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}

function Remove-GeneratedDirectory
{
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $publishPrefix = (Join-Path $repoRoot 'publish').TrimEnd('/') + '/'
    if (-not $fullPath.StartsWith($publishPrefix, [StringComparison]::Ordinal))
    {
        throw "Refusing to remove a path outside generated publish output: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
}

function Invoke-NpmAuditJson
{
    param([Parameter(Mandatory)][string]$WorkingDirectory, [switch]$OmitDev)

    $arguments = @('audit', '--json')
    if ($OmitDev) { $arguments += '--omit=dev' }
    $stderrPath = [IO.Path]::GetTempFileName()
    $previousPreference = $PSNativeCommandUseErrorActionPreference
    $PSNativeCommandUseErrorActionPreference = $false
    try
    {
        Push-Location $WorkingDirectory
        try
        {
            $auditJson = (& npm @arguments 2>$stderrPath) -join "`n"
            $exitCode = $LASTEXITCODE
        }
        finally { Pop-Location }
        if ($exitCode -notin @(0, 1))
        {
            throw "npm audit failed with exit code $exitCode. $([IO.File]::ReadAllText($stderrPath))"
        }
        $audit = $auditJson | ConvertFrom-Json
        if ($audit.PSObject.Properties['error'] -or
            -not $audit.PSObject.Properties['auditReportVersion'] -or $audit.auditReportVersion -ne 2 -or
            -not $audit.PSObject.Properties['vulnerabilities'])
        {
            throw 'npm audit did not return a valid version 2 vulnerability report.'
        }
        return $audit
    }
    finally
    {
        $PSNativeCommandUseErrorActionPreference = $previousPreference
        Remove-Item -LiteralPath $stderrPath -Force
    }
}

function Assert-LinuxElf
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ManagedRoot,
        [string]$CoreClrVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing native payload: $Path" }
    $stream = [IO.File]::OpenRead($Path)
    try
    {
        $header = [byte[]]::new(20)
        if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
            $header[0] -ne 0x7f -or $header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or
            $header[4] -ne 2 -or $header[5] -ne 1 -or $header[18] -ne 62 -or $header[19] -ne 0)
        {
            throw "Expected a Linux x86_64 ELF payload: $Path"
        }
    }
    finally { $stream.Dispose() }
    $dependencies = (& env LC_ALL=C ldd $Path 2>&1) -join "`n"
    $dependencyExitCode = $LASTEXITCODE
    if ($dependencyExitCode -ne 0) { throw "Native dependency inspection failed: $Path`n$dependencies" }
    if ($dependencies -match '\bnot found\b')
    {
        $missingLibraries = @([regex]::Matches($dependencies, '(?m)^\s*(?<library>\S+)\s+=>\s+not found\s*$'))
        # The exact supplier revision tolerates this optional provider's dlopen
        # failure. Ubuntu's LTTng 2.13 ABI cannot satisfy its 2.12 SONAME.
        if (-not [string]::IsNullOrWhiteSpace($ManagedRoot) -and
            [IO.Path]::GetFullPath($Path) -ceq [IO.Path]::GetFullPath((Join-Path $ManagedRoot 'libcoreclrtraceptprovider.so')) -and
            $CoreClrVersion -ceq $optionalLttngProviderVersion -and
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $optionalLttngProviderSha256 -and
            $missingLibraries.Count -eq 1 -and $missingLibraries[0].Groups['library'].Value -ceq 'liblttng-ust.so.0')
        {
            $script:optionalLttngUnavailable = $true
            Write-Warning 'The exact CoreCLR 10.0.12 optional LTTng provider cannot load liblttng-ust.so.0 on Ubuntu 24.04. LTTng tracing is unavailable; mandatory and unknown native dependencies remain fatal.'
            return
        }
        throw "The native payload has unresolved dependencies: $Path`n$dependencies"
    }
}

function Assert-PressClosure
{
    param([Parameter(Mandatory)][string]$ManagedRoot)

    $pressRoot = Join-Path $ManagedRoot 'press-runtime'
    $manifestPath = Join-Path $pressRoot 'lorekeeper-press-runtime.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'The Press runtime manifest is missing.' }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 3 -or $manifest.platform -cne 'linux' -or $manifest.architecture -cne 'x64')
    {
        throw 'The packaged Press runtime manifest is not a schema 3 Linux x64 closure.'
    }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (@(Get-ChildItem -LiteralPath $pressRoot -Recurse -Force |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0)
    {
        throw 'The Press runtime must not contain symbolic links.'
    }
    foreach ($file in @($manifest.files))
    {
        $relativePath = [string]$file.relativePath
        $fullPath = [IO.Path]::GetFullPath((Join-Path $pressRoot $relativePath))
        if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains('\') -or
            -not $fullPath.StartsWith($pressRoot + '/', [StringComparison]::Ordinal) -or
            -not $paths.Add($relativePath) -or -not (Test-Path -LiteralPath $fullPath -PathType Leaf))
        {
            throw "The Press manifest contains an invalid or missing path: $relativePath"
        }
        $item = Get-Item -LiteralPath $fullPath
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $item.Length -ne $file.byteLength -or
            (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$file.sha256)
        {
            throw "The Press manifest fingerprint failed: $relativePath"
        }
    }
    $actualPaths = @(Get-ChildItem -LiteralPath $pressRoot -Recurse -File |
        Where-Object FullName -ne $manifestPath |
        ForEach-Object { [IO.Path]::GetRelativePath($pressRoot, $_.FullName) })
    if ($actualPaths.Count -ne $paths.Count -or @($actualPaths | Where-Object { -not $paths.Contains($_) }).Count -ne 0)
    {
        throw 'The Press runtime contains files outside its manifest.'
    }
    foreach ($relativePath in @('lorekeeper-press', 'THIRD-PARTY-NOTICES.txt', 'sbom.json', 'profiles/CGATS21_CRPC1.icc',
        'profiles/SOURCE.md', 'fonts/lora/OFL.txt', 'fonts/nunito/OFL.txt', 'fonts/roboto-mono/OFL.txt'))
    {
        if (-not $paths.Contains($relativePath)) { throw "Missing Press runtime evidence: $relativePath" }
    }
    $pressExecutable = Join-Path $pressRoot 'lorekeeper-press'
    Assert-LinuxElf -Path $pressExecutable
    $description = (& $pressExecutable describe --json) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $description.protocolVersion -ne 15 -or
        [string]::IsNullOrWhiteSpace($description.rendererVersion) -or
        [string]::IsNullOrWhiteSpace($description.printArtifactProfileRegistryVersion) -or
        [string]::IsNullOrWhiteSpace($description.printArtifactProfileRegistrySha256) -or
        $description.rendererVersion -cne $manifest.description.rendererVersion -or
        $description.printArtifactProfileRegistryVersion -cne $manifest.description.printArtifactProfileRegistryVersion -or
        $description.printArtifactProfileRegistrySha256 -cne $manifest.description.printArtifactProfileRegistrySha256)
    {
        throw 'The packaged Press executable failed its capability contract.'
    }
    return $description
}

function Assert-DesktopClosure
{
    param([Parameter(Mandatory)][string]$DesktopRoot, [Parameter(Mandatory)][string]$BundleHash)

    $managedRoot = Join-Path $DesktopRoot 'resources/bin'
    Assert-ReleaseNoticeClosure -RepositoryRoot $repoRoot -ManagedRoot $managedRoot -DesktopRoot $DesktopRoot
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $managedRoot 'Lorekeeper.runtimeconfig.json') -Raw | ConvertFrom-Json
    $coreClrVersion = [string](@($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -CEQ 'Microsoft.NETCore.App')[0].version)
    if (-not (Test-Path -LiteralPath (Join-Path $DesktopRoot 'LICENSE.electron.txt') -PathType Leaf) -and
        -not (Test-Path -LiteralPath (Join-Path $DesktopRoot 'LICENSE') -PathType Leaf))
    {
        throw 'The packaged Electron license is missing.'
    }
    $packagedBundle = Join-Path $managedRoot 'wwwroot/js/semantic-editor.bundle.js'
    $packagedNotice = Join-Path $managedRoot 'wwwroot/js/semantic-editor.NOTICES.txt'
    if (-not (Test-Path -LiteralPath $packagedBundle -PathType Leaf) -or
        (Get-FileHash -LiteralPath $packagedBundle -Algorithm SHA256).Hash -cne $BundleHash -or
        (Get-FileHash -LiteralPath $packagedNotice -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $semanticEditorNotice -Algorithm SHA256).Hash)
    {
        throw 'The packaged semantic editor does not match the checked-in bundle and notices.'
    }
    $electronExecutables = @(Get-ChildItem -LiteralPath $DesktopRoot -File |
        Where-Object Name -in @('com.lorekeeper.app', 'Lorekeeper', 'lorekeeper'))
    if ($electronExecutables.Count -ne 1) { throw 'Expected exactly one packaged Lorekeeper Electron executable.' }
    Assert-LinuxElf -Path $electronExecutables[0].FullName
    & test -x (Join-Path $DesktopRoot 'AppRun')
    if ($LASTEXITCODE -ne 0) { throw 'The packaged AppRun entry point must be executable.' }
    Assert-LinuxElf -Path (Join-Path $DesktopRoot 'chrome-sandbox')
    Assert-LinuxElf -Path (Join-Path $managedRoot 'Lorekeeper')
    foreach ($pattern in @('vec0.so', 'libSkiaSharp.so', 'pdfium.so', 'libe_sqlite3.so', '*git2*.so*'))
    {
        if (@(Get-ChildItem -LiteralPath $managedRoot -Recurse -File -Filter $pattern).Count -eq 0)
        {
            throw "The managed payload is missing a required native dependency: $pattern"
        }
    }
    foreach ($library in @(Get-ChildItem -LiteralPath $DesktopRoot -Recurse -File |
        Where-Object Name -Match '\.so(?:\..*)?$'))
    {
        Assert-LinuxElf -Path $library.FullName -ManagedRoot $managedRoot -CoreClrVersion $coreClrVersion
    }
    return Assert-PressClosure -ManagedRoot $managedRoot
}

function Prepare-AppImageToolset
{
    # APPIMAGE_TOOLS_PATH is electron-builder's supported toolset override. Keep
    # the exact verified encoders/runtime, but omit its optional legacy desktop
    # libraries: Electron 43 uses the Ubuntu 24.04 dependencies declared by DEB.
    Remove-GeneratedDirectory -Path $appImageToolsetDirectory
    New-Item -ItemType Directory -Path $appImageToolsetDirectory | Out-Null
    $archivePath = Join-Path $appImageToolsetDirectory $appImageArchiveName
    Invoke-WebRequest -Uri $appImageToolsetUrl -OutFile $archivePath
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $appImageToolsetSha256)
    {
        throw 'The pinned AppImage 1.0.3 toolset archive fingerprint failed.'
    }
    Invoke-CheckedCommand -FilePath tar -Arguments @('--extract', '--gzip', '--file', $archivePath, '--directory', $appImageToolsetDirectory)
    $runtimePath = Join-Path $appImageToolsetDirectory 'runtimes/runtime-x64'
    if ((Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $appImageRuntimeSha256)
    {
        throw 'The pinned AppImage type2-runtime fingerprint failed.'
    }
    $compatibilityDirectory = Join-Path $appImageToolsetDirectory 'lib/x64'
    $compatibilityNames = @(Get-ChildItem -LiteralPath $compatibilityDirectory -File | ForEach-Object Name | Sort-Object)
    $expectedCompatibilityNames = @('libXss.so.1', 'libXss.so.1.0.0', 'libXtst.so.6', 'libXtst.so.6.1.0',
        'libappindicator3.so.1', 'libappindicator3.so.1.0.0', 'libgconf-2.so.4', 'libgconf-2.so.4.1.5',
        'libindicator3.so.7', 'libindicator3.so.7.0.0', 'libnotify.so.4', 'libnotify.so.4.0.0') | Sort-Object
    if (@(Compare-Object -ReferenceObject $expectedCompatibilityNames -DifferenceObject $compatibilityNames).Count -ne 0)
    {
        throw 'The verified AppImage toolset has an unexpected compatibility-library inventory.'
    }
    Remove-GeneratedDirectory -Path $compatibilityDirectory
    New-Item -ItemType Directory -Path $compatibilityDirectory | Out-Null
    return (Get-Item -LiteralPath $runtimePath).Length
}

function Assert-AppImageRuntime
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][int]$RuntimeLength)

    $stream = [IO.File]::OpenRead($Path)
    try
    {
        $runtimeBytes = [byte[]]::new($RuntimeLength)
        $stream.ReadExactly($runtimeBytes, 0, $runtimeBytes.Length)
        $runtimeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($runtimeBytes)).ToLowerInvariant()
        if ($runtimeHash -cne $appImageRuntimeSha256)
        {
            throw 'The generated AppImage does not contain the exact pinned static runtime.'
        }
    }
    finally { $stream.Dispose() }
}

$osRelease = @{}
foreach ($line in [IO.File]::ReadAllLines('/etc/os-release'))
{
    if ($line -match '^(?<key>[A-Z_]+)=(?<value>.*)$') { $osRelease[$Matches.key] = $Matches.value.Trim('"') }
}
if ($osRelease['ID'] -cne 'ubuntu' -or $osRelease['VERSION_ID'] -cne '24.04')
{
    throw 'The Linux release baseline is Ubuntu 24.04 x64. Use that native host or WSL2 distribution.'
}
foreach ($command in @('dotnet', 'pwsh', 'node', 'npm', 'cargo', 'rustc', 'cc', 'dpkg-deb', 'dpkg-query', 'env', 'ldd', 'tar', 'stat'))
{
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Missing Linux build command: $command" }
}
if ($PSVersionTable.PSVersion -lt [Version]'7.4') { throw 'PowerShell 7.4 or later is required on the Linux build host.' }
$dotnetVersion = (& dotnet --version).Trim()
$rustVersion = (& rustc --version).Trim()
$rustToolchainText = [IO.File]::ReadAllText((Join-Path $repoRoot 'Lorekeeper.Press/rust-toolchain.toml'))
$rustToolchainMatch = [regex]::Match($rustToolchainText, 'channel\s*=\s*"(?<version>\d+\.\d+\.\d+)"')
if (-not $rustToolchainMatch.Success -or $rustVersion -notmatch ('^rustc ' + [regex]::Escape($rustToolchainMatch.Groups['version'].Value) + '\b'))
{
    throw "The Linux Rust compiler must match the pinned Press toolchain; found '$rustVersion'."
}
$nodeVersionText = (& node --version).Trim().TrimStart('v')
$nodeVersion = $null
if ($dotnetVersion -notmatch '^10\.' -or
    -not [Version]::TryParse($nodeVersionText, [ref]$nodeVersion) -or $nodeVersion -lt [Version]'22.12.0')
{
    throw "Required .NET 10 SDK and Node.js >=22.12; found .NET $dotnetVersion, Node $nodeVersionText."
}
$runtimePackages = @($electronBuilderConfiguration.deb.depends)
if ($runtimePackages.Count -eq 0 -or @($runtimePackages | Where-Object { $_ -cnotmatch '^[a-z0-9][a-z0-9+.-]+$' }).Count -ne 0)
{
    throw 'The Ubuntu baseline must declare explicit runtime package names in deb.depends.'
}
foreach ($package in @('build-essential') + $runtimePackages)
{
    $status = (& dpkg-query -W '-f=${Status}' $package 2>$null) -join ''
    if ($LASTEXITCODE -ne 0 -or $status -cne 'install ok installed')
    {
        throw "Missing Ubuntu 24.04 build/runtime package: $package"
    }
}
$homeFilesystem = (& stat -f -c '%T' $env:HOME).Trim()
if ($LASTEXITCODE -ne 0 -or $homeFilesystem -notin @('ext2/ext3', 'btrfs', 'xfs'))
{
    throw 'The Linux build home must be on a native Linux filesystem, not a Windows-mounted directory.'
}
if ($CheckOnly)
{
    Write-Host 'Linux release prerequisites are ready (Ubuntu 24.04 x64; .NET, Node, Rust, packaging tools and native dependencies).' -ForegroundColor Green
    return
}
if ($Version -cne $sourceVersion) { throw "Version $Version differs from the committed project version $sourceVersion." }
$sourceFilesystem = (& stat -f -c '%T' $repoRoot).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceFilesystem -notin @('ext2/ext3', 'btrfs', 'xfs'))
{
    throw 'Build from source on a native Linux filesystem. The WSL wrapper exports the release commit to ext4.'
}
if ([string]::IsNullOrWhiteSpace($SourceArchivePath))
{
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Native checkout builds require git for source provenance.' }
    $SourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the source commit.' }
    $SourceTree = (& git -C $repoRoot rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0 -or @(& git -C $repoRoot status --porcelain).Count -ne 0)
    {
        throw 'Native release builds require a clean committed source tree.'
    }
    $SourceArchiveSha256 = $null
}
else
{
    if ($SourceCommit -notmatch '^[a-f0-9]{40,64}$' -or $SourceTree -notmatch '^[a-f0-9]{40,64}$' -or
        $SourceArchiveSha256 -notmatch '^[a-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $SourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $SourceArchiveSha256)
    {
        throw 'The exported source archive has incomplete or mismatched commit/tree/hash provenance.'
    }
}

Remove-GeneratedDirectory -Path $stageDirectory
Remove-GeneratedDirectory -Path $outputDirectory
$bundleHash = (Get-FileHash -LiteralPath $semanticEditorBundle -Algorithm SHA256).Hash
Invoke-CheckedCommand -FilePath npm -Arguments @('ci') -WorkingDirectory $semanticEditorDirectory
Invoke-CheckedCommand -FilePath npm -Arguments @('audit', '--audit-level=high') -WorkingDirectory $semanticEditorDirectory
Invoke-CheckedCommand -FilePath npm -Arguments @('run', 'build') -WorkingDirectory $semanticEditorDirectory
if ((Get-FileHash -LiteralPath $semanticEditorBundle -Algorithm SHA256).Hash -cne $bundleHash)
{
    throw 'The semantic editor rebuild differs from the committed bundle. Rebuild and commit it before releasing.'
}
Invoke-CheckedCommand -FilePath dotnet -Arguments @('restore', 'Lorekeeper.sln', '--force-evaluate',
    '-p:Configuration=Release', '-p:PublishProfile=linux-x64', '-p:SelfContained=true',
    '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:NuGetAuditLevel=low', '-p:TreatWarningsAsErrors=true')
Invoke-CheckedCommand -FilePath dotnet -Arguments @('build', 'Lorekeeper.sln', '-c', 'Release', '--no-restore',
    "-p:Version=$Version", '-p:LorekeeperDistributionChannel=Free')
Invoke-CheckedCommand -FilePath dotnet -Arguments @('test', 'Lorekeeper.Tests/Lorekeeper.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore')
Invoke-CheckedCommand -FilePath cargo -Arguments @('test', '--locked') -WorkingDirectory (Join-Path $repoRoot 'Lorekeeper.Press')
$appImageRuntimeLength = Prepare-AppImageToolset
$previousCi = $env:CI
$previousAppImageTools = $env:APPIMAGE_TOOLS_PATH
try
{
    $env:CI = 'false'
    $env:APPIMAGE_TOOLS_PATH = $appImageToolsetDirectory
    Invoke-CheckedCommand -FilePath dotnet -Arguments @('publish', $projectPath, '-c', 'Release', '--no-restore',
        '-p:PublishProfile=linux-x64', "-p:Version=$Version", '-p:LorekeeperDistributionChannel=Free')
}
finally
{
    if ($null -eq $previousCi) { Remove-Item Env:CI -ErrorAction SilentlyContinue }
    else { $env:CI = $previousCi }
    if ($null -eq $previousAppImageTools) { Remove-Item Env:APPIMAGE_TOOLS_PATH -ErrorAction SilentlyContinue }
    else { $env:APPIMAGE_TOOLS_PATH = $previousAppImageTools }
}

$manifest = Get-Content -Raw -LiteralPath (Join-Path $stageDirectory 'package.json') | ConvertFrom-Json
$lock = Get-Content -Raw -LiteralPath (Join-Path $stageDirectory 'package-lock.json') | ConvertFrom-Json -AsHashtable
$installedElectron = Get-Content -Raw -LiteralPath (Join-Path $stageDirectory 'node_modules/electron/package.json') | ConvertFrom-Json
if ($manifest.devDependencies.electron -cne $lock.packages.'node_modules/electron'.version -or
    $manifest.devDependencies.electron -cne $installedElectron.version)
{
    throw 'The generated Electron manifest, lock and installed version differ.'
}
$severityRank = @{ info = 0; low = 1; moderate = 2; high = 3; critical = 4 }
$productionAudit = Invoke-NpmAuditJson -WorkingDirectory $stageDirectory -OmitDev
$productionFindings = @(Get-ReleaseProductionAuditBlockingFindings -Audit $productionAudit -SeverityRank $severityRank -StageDirectory $stageDirectory)
if ($productionFindings.Count -gt 0) { throw 'The packaged Electron production dependencies have blocking security advisories.' }
$fullAudit = Invoke-NpmAuditJson -WorkingDirectory $stageDirectory
$electronFindings = @($fullAudit.vulnerabilities.PSObject.Properties | Where-Object {
    $_.Name -eq 'electron' -and $_.Value.isDirect -and $severityRank[$_.Value.severity] -ge $severityRank.moderate
})
if ($electronFindings.Count -gt 0) { throw 'The packaged Electron runtime has a blocking security advisory.' }

$pressDescription = Assert-DesktopClosure -DesktopRoot (Join-Path $outputDirectory 'linux-unpacked') -BundleHash $bundleHash
$appImagePath = Join-Path $outputDirectory "Lorekeeper-$Version-x86_64.AppImage"
$debPath = Join-Path $outputDirectory "Lorekeeper-$Version-amd64.deb"
foreach ($path in @($appImagePath, $debPath))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing expected Linux release artifact: $path" }
}
Assert-AppImageRuntime -Path $appImagePath -RuntimeLength $appImageRuntimeLength
$verificationRoot = Join-Path $outputDirectory 'package-verification'
New-Item -ItemType Directory -Path $verificationRoot | Out-Null
try
{
    Invoke-CheckedCommand -FilePath $appImagePath -Arguments @('--appimage-extract') -WorkingDirectory $verificationRoot
    $appImageRoot = Join-Path $verificationRoot 'squashfs-root'
    $appImageLibraryRoot = Join-Path $appImageRoot 'usr/lib'
    if ((Test-Path -LiteralPath $appImageLibraryRoot) -and
        @(Get-ChildItem -LiteralPath $appImageLibraryRoot -Recurse -File).Count -ne 0)
    {
        throw 'The AppImage must not include the toolset legacy compatibility libraries.'
    }
    $sourceAppRun = Join-Path $repoRoot 'eng/linux/AppRun.sh'
    $packagedAppRun = Join-Path $appImageRoot 'AppRun'
    if ((Get-FileHash -LiteralPath $packagedAppRun -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $sourceAppRun -Algorithm SHA256).Hash)
    {
        throw 'The AppImage did not retain the fail-closed source launcher. Refusing an upstream sandbox fallback.'
    }
    $desktopFiles = @(Get-ChildItem -LiteralPath $appImageRoot -File -Filter '*.desktop')
    if ($desktopFiles.Count -ne 1 -or [IO.File]::ReadAllText($desktopFiles[0].FullName) -match '--no-sandbox')
    {
        throw 'The AppImage desktop entry must not disable the Electron sandbox.'
    }
    $null = Assert-DesktopClosure -DesktopRoot $appImageRoot -BundleHash $bundleHash
    $debRoot = Join-Path $verificationRoot 'deb'
    Invoke-CheckedCommand -FilePath dpkg-deb -Arguments @('--extract', $debPath, $debRoot)
    $debArchitecture = (& dpkg-deb --field $debPath Architecture).Trim()
    if ($LASTEXITCODE -ne 0 -or $debArchitecture -cne 'amd64') { throw 'The DEB architecture must be amd64.' }
    $debDependencies = (& dpkg-deb --field $debPath Depends).Trim()
    if ($LASTEXITCODE -ne 0 -or
        @(Compare-Object -ReferenceObject ($runtimePackages | Sort-Object) -DifferenceObject @($debDependencies -split ',\s*' | Sort-Object)).Count -ne 0)
    {
        throw 'The DEB dependency metadata does not match the checked Ubuntu runtime inventory.'
    }
    $debResources = @(Get-ChildItem -LiteralPath $debRoot -Recurse -Directory -Filter resources |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/Lorekeeper') -PathType Leaf })
    if ($debResources.Count -ne 1) { throw 'The DEB does not contain exactly one Lorekeeper application closure.' }
    $null = Assert-DesktopClosure -DesktopRoot $debResources[0].Parent.FullName -BundleHash $bundleHash
}
finally { Remove-GeneratedDirectory -Path $verificationRoot }

$artifacts = @($appImagePath, $debPath | ForEach-Object {
    [ordered]@{ name = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$encoding = [Text.UTF8Encoding]::new($false)
$checksumLines = @($artifacts | ForEach-Object { '{0}  {1}' -f $_.sha256, $_.name })
[IO.File]::WriteAllText((Join-Path $outputDirectory 'SHA256SUMS.txt'), ($checksumLines -join "`n") + "`n", $encoding)
foreach ($artifact in $artifacts)
{
    [IO.File]::WriteAllText((Join-Path $outputDirectory ($artifact.name + '.sha256')), ('{0}  {1}' -f $artifact.sha256, $artifact.name) + "`n", $encoding)
}
$provenance = [ordered]@{
    schemaVersion = 1
    sourceCommit = $SourceCommit
    sourceTree = $SourceTree
    sourceArchiveSha256 = $SourceArchiveSha256
    sourceArchiveConfiguration = if ([string]::IsNullOrWhiteSpace($SourceArchivePath)) { $null } else {
        [ordered]@{ format = 'tar'; coreAutocrlf = $false; coreEol = 'lf' }
    }
    sourceVersion = $sourceVersion
    platform = 'linux'
    architecture = 'x64'
    distribution = 'Ubuntu 24.04'
    distributionChannel = 'Free'
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    dotnetSdk = $dotnetVersion
    node = $nodeVersionText
    rustc = (& rustc --version) -join ''
    powershell = $PSVersionTable.PSVersion.ToString()
    cargo = (& cargo --version) -join ''
    npm = (& npm --version) -join ''
    appImageToolset = [ordered]@{
        version = $appImageToolsetVersion
        archiveUrl = $appImageToolsetUrl
        archiveSha256 = $appImageToolsetSha256
        runtimeRelease = $appImageRuntimeRelease
        runtimeSourceCommit = $appImageRuntimeSourceCommit
        runtimeSha256 = $appImageRuntimeSha256
        noticeManifestSha256 = (Get-FileHash -LiteralPath $appImageManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        legacyCompatibilityLibrariesIncluded = $false
    }
    electron = $installedElectron.version
    semanticEditorSha256 = $bundleHash.ToLowerInvariant()
    optionalDiagnostics = @(
        if ($optionalLttngUnavailable)
        {
            [ordered]@{
                component = 'CoreCLR LTTng tracepoint provider'
                package = "Microsoft.NETCore.App.Runtime.linux-x64/$optionalLttngProviderVersion"
                path = 'resources/bin/libcoreclrtraceptprovider.so'
                sha256 = $optionalLttngProviderSha256
                unavailableDependency = 'liblttng-ust.so.0'
                available = $false
                integrationValidated = $false
                supplierSource = $optionalLttngSource
            }
        }
    )
    press = $pressDescription
    artifacts = $artifacts
}
[IO.File]::WriteAllText((Join-Path $outputDirectory 'release-provenance.json'), ($provenance | ConvertTo-Json -Depth 16) + "`n", $encoding)
if (-not $KeepUnpacked) { Remove-GeneratedDirectory -Path (Join-Path $outputDirectory 'linux-unpacked') }
Remove-GeneratedDirectory -Path $stageDirectory
Remove-GeneratedDirectory -Path $appImageToolsetDirectory
Write-Host "`nLinux release $Version (Ubuntu 24.04 x64) is ready in $outputDirectory" -ForegroundColor Green
