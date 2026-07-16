[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [ValidateSet('osx-arm64', 'osx-x64')]
    [string]$RuntimeIdentifier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsMacOS)
{
    throw 'macOS release packages must be built on macOS.'
}
$semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Version -notmatch $semVerPattern)
{
    throw "Version '$Version' must use SemVer form such as 0.2.0 or 0.2.0-beta.1."
}

$expectedArchitecture = if ($RuntimeIdentifier -eq 'osx-arm64') { 'Arm64' } else { 'X64' }
$actualArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($actualArchitecture -ne $expectedArchitecture)
{
    throw "$RuntimeIdentifier must be built on a native $expectedArchitecture runner; found $actualArchitecture."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repoRoot 'Lorekeeper.sln'
$projectPath = Join-Path $repoRoot 'Lorekeeper/Lorekeeper.csproj'
$stageDirectory = Join-Path $repoRoot "publish/$RuntimeIdentifier-stage"
$outputDirectory = Join-Path $repoRoot "publish/$RuntimeIdentifier"
$profileName = $RuntimeIdentifier
$artifactArchitecture = if ($RuntimeIdentifier -eq 'osx-arm64') { 'arm64' } else { 'x64' }
$machArchitecture = if ($RuntimeIdentifier -eq 'osx-arm64') { 'arm64' } else { 'x86_64' }
$dmgPath = Join-Path $outputDirectory "Lorekeeper-$Version-$artifactArchitecture.dmg"

foreach ($commandName in @('dotnet', 'node', 'npm', 'hdiutil', 'codesign', 'lipo', 'ditto'))
{
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue))
    {
        throw "Required build command '$commandName' was not found on PATH."
    }
}

$nodeVersionText = (& node --version).Trim().TrimStart('v')
$nodeVersion = $null
if (-not [Version]::TryParse($nodeVersionText, [ref]$nodeVersion) -or $nodeVersion -lt [Version]'22.12.0')
{
    throw "Node.js 22.12 or later is required; found '$nodeVersionText'."
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
        [System.StringComparison]::Ordinal))
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
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$WorkingDirectory = $repoRoot
    )

    Write-Host "`n> $FilePath $($Arguments -join ' ')" -ForegroundColor Cyan
    Push-Location $WorkingDirectory
    try
    {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0)
        {
            throw "Command '$FilePath' failed with exit code $LASTEXITCODE."
        }
    }
    finally
    {
        Pop-Location
    }
}

function Invoke-NpmAuditJson
{
    param(
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [switch]$OmitDev
    )

    $arguments = @('audit', '--json')
    if ($OmitDev) { $arguments += '--omit=dev' }
    $stderrPath = [System.IO.Path]::GetTempFileName()
    try
    {
        Push-Location $WorkingDirectory
        try
        {
            $auditJson = (& npm @arguments 2>$stderrPath) -join [Environment]::NewLine
            $exitCode = $LASTEXITCODE
        }
        finally
        {
            Pop-Location
        }

        $stderr = [System.IO.File]::ReadAllText($stderrPath).Trim()
        if ($exitCode -notin @(0, 1)) { throw "npm audit failed with exit code $exitCode. $stderr" }
        if ([string]::IsNullOrWhiteSpace($auditJson)) { throw "npm audit returned no JSON. $stderr" }
        $audit = $auditJson | ConvertFrom-Json
        if ($audit.PSObject.Properties['error'])
        {
            throw "npm audit failed: $($audit.error | ConvertTo-Json -Compress -Depth 5)"
        }
        if (-not $audit.PSObject.Properties['auditReportVersion'] -or
            $audit.auditReportVersion -ne 2 -or
            -not $audit.PSObject.Properties['vulnerabilities'])
        {
            throw 'npm audit did not return a version 2 vulnerability report.'
        }
        return $audit
    }
    finally
    {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

Push-Location $repoRoot
try
{
    Remove-GeneratedDirectory $stageDirectory
    Remove-GeneratedDirectory $outputDirectory

    Invoke-CheckedCommand dotnet @(
        'restore', $solutionPath, '--force-evaluate',
        "-p:RuntimeIdentifier=$RuntimeIdentifier",
        '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:NuGetAuditLevel=low',
        '-p:TreatWarningsAsErrors=true'
    )
    Invoke-CheckedCommand dotnet @(
        'build', $solutionPath, '-c', 'Release',
        "-p:RuntimeIdentifier=$RuntimeIdentifier", "-p:Version=$Version"
    )
    Invoke-CheckedCommand dotnet @(
        'publish', $projectPath, '-c', 'Release', "-p:PublishProfile=$profileName",
        "-p:RuntimeIdentifier=$RuntimeIdentifier", "-p:Version=$Version", '--no-restore'
    )

    $manifest = Get-Content -Raw (Join-Path $stageDirectory 'package.json') | ConvertFrom-Json
    $lockPath = Join-Path $stageDirectory 'package-lock.json'
    $installedElectronManifest = Get-Content -Raw (Join-Path $stageDirectory 'node_modules/electron/package.json') | ConvertFrom-Json
    $readLockedElectron = 'const lock=require(process.argv[1]);const entry=lock.packages?.[process.argv[2]];if(!entry?.version)process.exit(2);process.stdout.write(entry.version);'
    $lockedElectronVersion = ((& node -e $readLockedElectron $lockPath 'node_modules/electron') -join '').Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($lockedElectronVersion))
    {
        throw 'Electron is missing from the release dependency lock.'
    }
    if ([string]$manifest.devDependencies.electron -ne $lockedElectronVersion -or
        $lockedElectronVersion -ne [string]$installedElectronManifest.version)
    {
        throw 'Electron version mismatch between manifest, lock, and installed runtime.'
    }

    $severityRank = @{ info = 0; low = 1; moderate = 2; high = 3; critical = 4 }
    $productionAudit = Invoke-NpmAuditJson -WorkingDirectory $stageDirectory -OmitDev
    $productionBlocking = @(
        $productionAudit.vulnerabilities.PSObject.Properties |
            Where-Object { $severityRank[$_.Value.severity] -ge $severityRank.high }
    )
    if ($productionBlocking.Count -gt 0)
    {
        throw "Production npm dependency audit failed: $(($productionBlocking.Name) -join ', ')."
    }

    $fullAudit = Invoke-NpmAuditJson -WorkingDirectory $stageDirectory
    $electronEntry = $fullAudit.vulnerabilities.PSObject.Properties['electron']
    $electronBlocking = if ($electronEntry)
    {
        @($electronEntry.Value.via | Where-Object {
            $_ -isnot [string] -and $severityRank[$_.severity] -ge $severityRank.moderate
        })
    }
    else { @() }
    if ($electronBlocking.Count -gt 0)
    {
        throw 'Packaged Electron runtime audit found a moderate-or-higher advisory.'
    }

    if (-not (Test-Path -LiteralPath $dmgPath -PathType Leaf))
    {
        throw "Expected release artifact was not produced: $dmgPath"
    }
    Invoke-CheckedCommand hdiutil @('verify', $dmgPath)

    $unpackedApps = @(Get-ChildItem -LiteralPath $outputDirectory -Recurse -Filter 'Lorekeeper.app' -Directory)
    if ($unpackedApps.Count -ne 1)
    {
        throw "Expected one unpacked Lorekeeper.app, found $($unpackedApps.Count)."
    }
    $appPath = $unpackedApps[0].FullName
    Invoke-CheckedCommand codesign @('--verify', '--deep', '--strict', '--verbose=2', $appPath)
    $signatureDetails = (& codesign --display --verbose=4 $appPath 2>&1) -join [Environment]::NewLine
    if ($signatureDetails -notmatch '(?m)^Signature=adhoc$')
    {
        throw "Lorekeeper.app is not ad-hoc signed.`n$signatureDetails"
    }

    $electronExecutable = Join-Path $appPath 'Contents/MacOS/Lorekeeper'
    $dotnetExecutable = Join-Path $appPath "Contents/Resources/app/bin/$($manifest.executable)"
    foreach ($executable in @($electronExecutable, $dotnetExecutable))
    {
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf))
        {
            throw "Expected packaged executable was not found: $executable"
        }
        $architectures = ((& lipo -archs $executable) -join '').Trim()
        if ($LASTEXITCODE -ne 0 -or $architectures -ne $machArchitecture)
        {
            throw "Expected $executable to contain only $machArchitecture; found '$architectures'."
        }
    }

    $mountPoint = $null
    $tempInstallRoot = Join-Path ([System.IO.Path]::GetTempPath()) "Lorekeeper-release-$([Guid]::NewGuid().ToString('N'))"
    $installedApp = Join-Path $tempInstallRoot 'Lorekeeper.app'
    $process = $null
    try
    {
        $mountOutput = @(& hdiutil attach -nobrowse -readonly $dmgPath)
        if ($LASTEXITCODE -ne 0) { throw 'Could not mount the generated DMG.' }
        $mountPoint = (($mountOutput[-1] -split "`t")[-1]).Trim()
        if ([string]::IsNullOrWhiteSpace($mountPoint))
        {
            throw 'Could not determine the generated DMG mount point.'
        }

        $mountedApp = Get-ChildItem -LiteralPath $mountPoint -Filter 'Lorekeeper.app' -Directory | Select-Object -First 1
        if (-not $mountedApp) { throw 'The generated DMG does not contain Lorekeeper.app.' }
        New-Item -ItemType Directory -Path $tempInstallRoot | Out-Null
        Invoke-CheckedCommand ditto @($mountedApp.FullName, $installedApp)
        Invoke-CheckedCommand codesign @('--verify', '--deep', '--strict', '--verbose=2', $installedApp)

        $process = Start-Process -FilePath (Join-Path $installedApp 'Contents/MacOS/Lorekeeper') -PassThru
        $ready = $false
        for ($attempt = 0; $attempt -lt 90; $attempt++)
        {
            if ($process.HasExited) { throw "Lorekeeper exited during startup with code $($process.ExitCode)." }
            try
            {
                $response = Invoke-WebRequest -Uri 'http://localhost:1455/' -TimeoutSec 2
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            }
            catch { }
            Start-Sleep -Seconds 1
        }
        if (-not $ready) { throw 'Lorekeeper did not become ready at http://localhost:1455/.' }
    }
    finally
    {
        if ($process -and -not $process.HasExited)
        {
            & pkill -TERM -P $process.Id 2>$null
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        if ($mountPoint) { & hdiutil detach $mountPoint -force | Out-Null }
        if (Test-Path -LiteralPath $tempInstallRoot)
        {
            Remove-Item -LiteralPath $tempInstallRoot -Recurse -Force
        }
    }

    $hash = Get-FileHash -LiteralPath $dmgPath -Algorithm SHA256
    $checksumPath = Join-Path $outputDirectory 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllText(
        $checksumPath,
        ("{0}  {1}`n" -f $hash.Hash.ToLowerInvariant(), (Split-Path $dmgPath -Leaf)),
        [System.Text.UTF8Encoding]::new($false))

    foreach ($unpackedDirectory in @(Get-ChildItem -LiteralPath $outputDirectory -Directory | Where-Object Name -like 'mac*'))
    {
        Remove-GeneratedDirectory $unpackedDirectory.FullName
    }
    Remove-GeneratedDirectory $stageDirectory

    Write-Host "`nmacOS release $Version ($artifactArchitecture) is ready: $dmgPath" -ForegroundColor Green
}
finally
{
    Pop-Location
}
