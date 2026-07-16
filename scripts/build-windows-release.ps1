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

$semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Version -notmatch $semVerPattern)
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

$nodeVersionText = (& node --version).Trim().TrimStart('v')
$nodeVersion = $null
if (-not [Version]::TryParse($nodeVersionText, [ref]$nodeVersion) -or $nodeVersion -lt [Version]'22.12.0')
{
    throw "Node.js 22.12 or later is required; found '$nodeVersionText'."
}

$npmCommand = (Get-Command 'npm.cmd').Source

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

function Invoke-NpmAuditJson
{
    param(
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [switch]$OmitDev
    )

    $arguments = @('audit', '--json')
    if ($OmitDev)
    {
        $arguments += '--omit=dev'
    }

    $stderrPath = [System.IO.Path]::GetTempFileName()
    $hasNativeErrorPreference = Test-Path variable:PSNativeCommandUseErrorActionPreference
    if ($hasNativeErrorPreference)
    {
        $previousNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $false
    }

    try
    {
        Push-Location $WorkingDirectory
        try
        {
            $auditJson = (& $npmCommand @arguments 2>$stderrPath) -join [Environment]::NewLine
            $exitCode = $LASTEXITCODE
        }
        finally
        {
            Pop-Location
        }

        $stderr = [System.IO.File]::ReadAllText($stderrPath).Trim()
        if ($exitCode -notin @(0, 1))
        {
            throw "npm audit failed with exit code $exitCode. $stderr"
        }
        if ([string]::IsNullOrWhiteSpace($auditJson))
        {
            throw "npm audit returned no JSON. $stderr"
        }

        try
        {
            $audit = $auditJson | ConvertFrom-Json
        }
        catch
        {
            throw "npm audit returned invalid JSON. $stderr"
        }

        $auditError = $audit.PSObject.Properties['error']
        if ($auditError)
        {
            $errorJson = $auditError.Value | ConvertTo-Json -Compress -Depth 5
            throw "npm audit failed: $errorJson"
        }

        $reportVersion = $audit.PSObject.Properties['auditReportVersion']
        $vulnerabilities = $audit.PSObject.Properties['vulnerabilities']
        if (-not $reportVersion -or $reportVersion.Value -ne 2 -or -not $vulnerabilities)
        {
            throw 'npm audit did not return a version 2 vulnerability report.'
        }

        return $audit
    }
    finally
    {
        if ($hasNativeErrorPreference)
        {
            $PSNativeCommandUseErrorActionPreference = $previousNativeErrorPreference
        }
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

Push-Location $repoRoot
try
{
    Remove-GeneratedDirectory $stageDirectory
    Remove-GeneratedDirectory $outputDirectory

    Invoke-CheckedCommand dotnet @(
        'restore',
        $solutionPath,
        '--force-evaluate',
        '-p:NuGetAudit=true',
        '-p:NuGetAuditMode=all',
        '-p:NuGetAuditLevel=low',
        '-p:TreatWarningsAsErrors=true'
    )

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

    $manifest = Get-Content -Raw (Join-Path $stageDirectory 'package.json') | ConvertFrom-Json
    $lockPath = Join-Path $stageDirectory 'package-lock.json'
    $installedElectronManifest = Get-Content -Raw (Join-Path $stageDirectory 'node_modules\electron\package.json') | ConvertFrom-Json
    $readLockedElectron = 'const lock=require(process.argv[1]);const entry=lock.packages?.[process.argv[2]];if(!entry?.version)process.exit(2);process.stdout.write(entry.version);'
    $lockedElectronOutput = & node -e $readLockedElectron $lockPath 'node_modules/electron'
    $lockedElectronExitCode = $LASTEXITCODE
    $lockedElectronVersion = ($lockedElectronOutput -join '').Trim()
    if ($lockedElectronExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($lockedElectronVersion))
    {
        throw 'Electron is missing from the release dependency lock.'
    }
    $manifestElectronVersion = [string]$manifest.devDependencies.electron
    $installedElectronVersion = [string]$installedElectronManifest.version
    if ($manifestElectronVersion -ne $lockedElectronVersion -or $lockedElectronVersion -ne $installedElectronVersion)
    {
        throw "Electron version mismatch: manifest=$manifestElectronVersion, lock=$lockedElectronVersion, installed=$installedElectronVersion."
    }

    $severityRank = @{
        info = 0
        low = 1
        moderate = 2
        high = 3
        critical = 4
    }

    $productionAudit = Invoke-NpmAuditJson -WorkingDirectory $stageDirectory -OmitDev
    $productionBlocking = @(
        $productionAudit.vulnerabilities.PSObject.Properties |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Finding = $_.Value } } |
            Where-Object { $severityRank[$_.Finding.severity] -ge $severityRank.high }
    )
    if ($productionBlocking.Count -gt 0)
    {
        $details = ($productionBlocking | ForEach-Object { "$($_.Name) ($($_.Finding.severity))" }) -join ', '
        throw "Production npm dependency audit failed: $details."
    }
    Write-Host 'Production npm dependency audit: no high-severity advisories.'

    $fullAudit = Invoke-NpmAuditJson -WorkingDirectory $stageDirectory
    $electronEntry = $fullAudit.vulnerabilities.PSObject.Properties['electron']
    $electronAdvisories = if ($electronEntry)
    {
        @(
            $electronEntry.Value.via |
                Where-Object {
                    if ($_ -is [string])
                    {
                        $false
                    }
                    else
                    {
                        $name = $_.PSObject.Properties['name']
                        $dependency = $_.PSObject.Properties['dependency']
                        ($name -and $name.Value -eq 'electron') -or
                            ($dependency -and $dependency.Value -eq 'electron')
                    }
                }
        )
    }
    else
    {
        @()
    }
    $electronBlocking = @(
        $electronAdvisories |
            Where-Object { $severityRank[$_.severity] -ge $severityRank.moderate }
    )
    if ($electronBlocking.Count -gt 0)
    {
        $details = ($electronBlocking | ForEach-Object { "$($_.url) ($($_.severity))" }) -join ', '
        throw "Packaged Electron runtime audit failed: $details."
    }
    Write-Host "Packaged Electron $installedElectronVersion audit: no moderate-or-higher advisories."

    $installerPath = Join-Path $outputDirectory "Lorekeeper-Setup-$Version-x64.exe"
    $portablePath = Join-Path $outputDirectory "Lorekeeper-Portable-$Version-x64.exe"
    $blockmapPath = "$installerPath.blockmap"
    $updateMetadataPath = Join-Path $outputDirectory 'latest.yml'
    $artifacts = @($installerPath, $portablePath, $blockmapPath, $updateMetadataPath)

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
    Write-Host "`nPublish these files together so installed apps can discover and verify v$Version."
}
finally
{
    Pop-Location
}
