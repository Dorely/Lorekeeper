[CmdletBinding()]
param(
    [string]$WindowsUnpackedDirectory,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'M0.5 MSIX feasibility packaging must run on Windows.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $repoRoot '.artifacts\m0.5-msix'
$manifestTemplatePath = Join-Path $PSScriptRoot 'Package.appxmanifest.xml'
$brandingIconPath = Join-Path $repoRoot 'Lorekeeper\wwwroot\branding\icon-512.png'

if ([string]::IsNullOrWhiteSpace($WindowsUnpackedDirectory))
{
    $WindowsUnpackedDirectory = Join-Path $repoRoot 'publish\win-x64\win-unpacked'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = Join-Path $artifactRoot 'current'
}

function Get-RepositoryPath
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Root
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $Root.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Description must stay inside ${Root}: $fullPath"
    }

    return $fullPath
}

function Remove-GeneratedDirectory
{
    param([Parameter(Mandatory)][string]$Path)

    $validatedPath = Get-RepositoryPath -Path $Path -Description 'Generated MSIX output' -Root $artifactRoot
    if (Test-Path -LiteralPath $validatedPath)
    {
        Remove-Item -LiteralPath $validatedPath -Recurse -Force -ErrorAction Stop
    }
}

function Find-WindowsSdkTool
{
    param([Parameter(Mandatory)][string]$Name)

    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path -LiteralPath $sdkRoot -PathType Container))
    {
        throw "The Windows SDK bin directory was not found: $sdkRoot"
    }

    $tool = Get-ChildItem -LiteralPath $sdkRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ($null -eq $tool)
    {
        throw "Windows SDK tool '$Name' was not found below $sdkRoot."
    }

    return $tool
}

function Invoke-CheckedTool
{
    param(
        [Parameter(Mandatory)][string]$Tool,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "> $Tool $($Arguments -join ' ')" -ForegroundColor Cyan
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo.FileName = $Tool
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments)
    {
        [void]$process.StartInfo.ArgumentList.Add($argument)
    }

    [void]$process.Start()
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $waitedMilliseconds = 0
    while (-not $process.WaitForExit(60000))
    {
        $waitedMilliseconds += 60000
        if ($waitedMilliseconds -ge 180000)
        {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Command '$Tool' exceeded the three-minute local feasibility timeout."
        }

        Write-Host "Waiting for local feasibility command after $($waitedMilliseconds / 1000) seconds: $Tool" -ForegroundColor Yellow
    }
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0)
    {
        throw "Command '$Tool' failed with exit code $($process.ExitCode). Output: $standardOutput $standardError"
    }
}

function Test-EmbeddedMsixSignature
{
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$ExpectedThumbprint
    )

    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try
    {
        $entry = $archive.GetEntry('AppxSignature.p7x')
        if ($null -eq $entry)
        {
            throw 'The signed MSIX is missing AppxSignature.p7x.'
        }

        $buffer = [System.IO.MemoryStream]::new()
        $entryStream = $entry.Open()
        try
        {
            $entryStream.CopyTo($buffer)
        }
        finally
        {
            $entryStream.Dispose()
        }
    }
    finally
    {
        $archive.Dispose()
    }

    $signatureBytes = $buffer.ToArray()
    if ($signatureBytes.Length -le 4 -or [System.Text.Encoding]::ASCII.GetString($signatureBytes, 0, 4) -ne 'PKCX')
    {
        throw 'AppxSignature.p7x does not have the required PKCX signature wrapper.'
    }

    $signedCms = [System.Security.Cryptography.Pkcs.SignedCms]::new()
    $signedCms.Decode([byte[]]$signatureBytes[4..($signatureBytes.Length - 1)])
    $signedCms.CheckSignature($true)
    if ($signedCms.SignerInfos.Count -ne 1)
    {
        throw "The signed MSIX must contain exactly one signer; found $($signedCms.SignerInfos.Count)."
    }

    $signer = $signedCms.SignerInfos[0].Certificate
    if ($null -eq $signer -or $signer.Thumbprint -ne $ExpectedThumbprint)
    {
        throw 'The signed MSIX signer does not match the ephemeral current-user certificate.'
    }
}

function Get-MsixVersion
{
    param([Parameter(Mandatory)][string]$ProjectPath)

    $projectText = [System.IO.File]::ReadAllText($ProjectPath)
    $match = [regex]::Match($projectText, '<Version>(?<version>[^<]+)</Version>')
    if (-not $match.Success)
    {
        throw "Could not read <Version> from $ProjectPath."
    }

    $version = $match.Groups['version'].Value.Trim()
    $coreMatch = [regex]::Match($version, '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:[-+].*)?$')
    if (-not $coreMatch.Success)
    {
        throw "Version '$version' cannot be converted to the required four-part MSIX version."
    }

    $parts = @('major', 'minor', 'patch') | ForEach-Object { [uint32]$coreMatch.Groups[$_].Value }
    if ($parts | Where-Object { $_ -gt 65535 })
    {
        throw "Version '$version' contains an MSIX version component above 65535."
    }

    return "$($parts[0]).$($parts[1]).$($parts[2]).0"
}

function Get-StoreChannelMetadata
{
    param([Parameter(Mandatory)][string]$AssemblyPath)

    try
    {
        $assembly = [System.Reflection.Assembly]::LoadFrom($AssemblyPath)
        $values = @(
            $assembly.GetCustomAttributesData() |
                Where-Object {
                    $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' -and
                        $_.ConstructorArguments.Count -eq 2 -and
                        [string]$_.ConstructorArguments[0].Value -eq 'LorekeeperDistributionChannel'
                } |
                ForEach-Object { [string]$_.ConstructorArguments[1].Value }
        )
        return $values
    }
    catch
    {
        throw "Could not inspect distribution-channel metadata in $AssemblyPath. $($_.Exception.Message)"
    }
}

$windowsUnpackedDirectory = Get-RepositoryPath -Path $WindowsUnpackedDirectory -Description 'Windows win-unpacked input' -Root $repoRoot
$outputDirectory = Get-RepositoryPath -Path $OutputDirectory -Description 'MSIX output' -Root $artifactRoot
if (-not (Test-Path -LiteralPath $windowsUnpackedDirectory -PathType Container))
{
    throw "Build the Store-channel Windows Release package first; input is missing: $windowsUnpackedDirectory"
}
foreach ($requiredInput in @(
    $manifestTemplatePath,
    $brandingIconPath,
    (Join-Path $windowsUnpackedDirectory 'Lorekeeper.exe'),
    (Join-Path $windowsUnpackedDirectory 'resources\bin\Lorekeeper.dll'),
    (Join-Path $windowsUnpackedDirectory 'resources\bin\press-runtime\lorekeeper-press.exe')))
{
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf))
    {
        throw "Required MSIX input is missing: $requiredInput"
    }
}

$storeMetadata = @(Get-StoreChannelMetadata (Join-Path $windowsUnpackedDirectory 'resources\bin\Lorekeeper.dll'))
if ($storeMetadata.Count -ne 1 -or $storeMetadata[0] -ne 'Store')
{
    throw "MSIX feasibility requires exactly one Store distribution-channel metadata value; found '$($storeMetadata -join ', ')'."
}

$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$signTool = Find-WindowsSdkTool 'signtool.exe'
$certUtil = (Get-Command certutil.exe -ErrorAction Stop).Source
$msixVersion = Get-MsixVersion (Join-Path $repoRoot 'Lorekeeper\Lorekeeper.csproj')
$packageRoot = Join-Path $outputDirectory 'package-root'
$appDirectory = Join-Path $packageRoot 'app'
$assetsDirectory = Join-Path $packageRoot 'Assets'
$packagePath = Join-Path $outputDirectory "Lorekeeper-LocalFeasibility-$msixVersion-x64.msix"
$unpackDirectory = Join-Path $outputDirectory 'unpacked'
$certificate = $null

Remove-GeneratedDirectory $outputDirectory
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

try
{
    Get-ChildItem -LiteralPath $windowsUnpackedDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $appDirectory -Recurse -Force
    }
    foreach ($assetName in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png'))
    {
        Copy-Item -LiteralPath $brandingIconPath -Destination (Join-Path $assetsDirectory $assetName) -Force
    }

    $manifest = [System.IO.File]::ReadAllText($manifestTemplatePath).Replace('__PACKAGE_VERSION__', $msixVersion)
    [System.IO.File]::WriteAllText((Join-Path $packageRoot 'AppxManifest.xml'), $manifest, [System.Text.UTF8Encoding]::new($false))

    Invoke-CheckedTool $makeAppx @('pack', '/o', '/h', 'SHA256', '/d', $packageRoot, '/p', $packagePath)
    Invoke-CheckedTool $makeAppx @('unpack', '/o', '/p', $packagePath, '/d', $unpackDirectory)

    foreach ($requiredPackageFile in @(
        'AppxManifest.xml',
        'Assets/StoreLogo.png',
        'Assets/Square44x44Logo.png',
        'Assets/Square150x150Logo.png',
        'app/Lorekeeper.exe',
        'app/resources/bin/Lorekeeper.dll',
        'app/resources/bin/press-runtime/lorekeeper-press.exe'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $unpackDirectory $requiredPackageFile) -PathType Leaf))
        {
            throw "MSIX validation did not retain required package content: $requiredPackageFile"
        }
    }

    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -KeyUsage DigitalSignature `
        -Subject 'CN=Lorekeeper Local MSIX Test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -FriendlyName 'Lorekeeper local MSIX feasibility (ephemeral)'
    Invoke-CheckedTool $signTool @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My', $packagePath)
    Test-EmbeddedMsixSignature -PackagePath $packagePath -ExpectedThumbprint $certificate.Thumbprint

    $report = [pscustomobject][ordered]@{
        formatVersion = 1
        scope = 'Local Windows full-trust MSIX feasibility only. No package installation, Store submission, provider call, GitHub query, or production signing occurred.'
        package = [pscustomobject][ordered]@{
            relativePath = [System.IO.Path]::GetRelativePath($repoRoot, $packagePath).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            manifestVersion = $msixVersion
            distributionChannel = $storeMetadata[0]
            fullTrust = $true
        }
        validation = [pscustomobject][ordered]@{
            makeAppxSemanticValidation = 'passed during pack without /nv'
            requiredContent = @('AppxManifest.xml', 'Assets/*.png', 'app/Lorekeeper.exe', 'app/resources/bin/Lorekeeper.dll', 'app/resources/bin/press-runtime/lorekeeper-press.exe')
            signature = 'passed embedded-CMS integrity and exact ephemeral current-user signer validation; Windows trust-chain and installation validation require a separately authorized test profile'
            installed = $false
        }
    }
    $reportPath = Join-Path $outputDirectory 'm0.5-msix-feasibility.json'
    [System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Validated local MSIX feasibility package: $packagePath" -ForegroundColor Green
    Write-Host 'The package was not installed. Installation needs a separately authorized disposable test profile.' -ForegroundColor Yellow
}
finally
{
    if ($certificate)
    {
        & $certUtil -user -delstore My $certificate.Thumbprint | Out-Null
    }
}
