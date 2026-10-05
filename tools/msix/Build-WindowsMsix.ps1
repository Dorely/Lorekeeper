[CmdletBinding()]
param(
    [string]$IdentityPath,
    [switch]$LocalValidation,
    [switch]$CheckOnly,
    [string]$WindowsUnpackedDirectory,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'Windows MSIX packaging must run on Windows.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $repoRoot '.artifacts\msix'
$manifestTemplatePath = Join-Path $PSScriptRoot 'Package.appxmanifest.xml'
$brandingIconPath = Join-Path $repoRoot 'Lorekeeper\wwwroot\branding\icon-512.png'
. (Join-Path $repoRoot 'eng/ReleaseWorkflow.ps1')

if ($LocalValidation -and $IdentityPath)
{
    throw 'Local validation uses its own test identity. Do not supply a Store identity.'
}
if (-not $LocalValidation -and [string]::IsNullOrWhiteSpace($IdentityPath))
{
    throw 'Store packaging requires -IdentityPath with the exact reserved Partner Center identity. Use -LocalValidation for a signed local test package.'
}
$identity = if ($LocalValidation)
{
    [pscustomobject]@{
        packageIdentityName = 'Lorekeeper.LocalValidation'
        packageIdentityPublisher = 'CN=Lorekeeper Local MSIX Test'
        publisherDisplayName = 'Lorekeeper local test'
        displayName = 'Lorekeeper local validation'
    }
}
else
{
    Get-Content -LiteralPath $IdentityPath -Raw | ConvertFrom-Json
}
foreach ($field in @('packageIdentityName', 'packageIdentityPublisher', 'publisherDisplayName', 'displayName'))
{
    if (-not $identity.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$identity.$field))
    {
        throw "Store identity field '$field' must be copied from the reserved product in Partner Center."
    }
}
if ([string]$identity.packageIdentityName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$' -or
    [string]$identity.packageIdentityPublisher -notmatch '^CN=')
{
    throw 'The package identity name/publisher is malformed. Use the exact Partner Center values.'
}
if (-not $LocalValidation -and ([string]$identity.packageIdentityName -match 'LocalValidation|LocalFeasibility' -or
    [string]$identity.packageIdentityPublisher -eq 'CN=Lorekeeper Local MSIX Test'))
{
    throw 'A local test identity cannot be used for Store output.'
}

if ([string]::IsNullOrWhiteSpace($WindowsUnpackedDirectory))
{
    $WindowsUnpackedDirectory = Join-Path $repoRoot 'publish\win-x64\win-unpacked'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $OutputDirectory = Join-Path $artifactRoot $(if ($LocalValidation) { 'local-validation' } else { 'store' })
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
            throw "Command '$Tool' exceeded the three-minute MSIX timeout."
        }

        Write-Host "Waiting for MSIX command after $($waitedMilliseconds / 1000) seconds: $Tool" -ForegroundColor Yellow
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

function ConvertTo-MsixVersion
{
    param([Parameter(Mandatory)][string]$Version)

    $coreMatch = [regex]::Match($Version, '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$')
    if (-not $coreMatch.Success)
    {
        throw "Version '$Version' must be stable three-part SemVer for Store packaging."
    }

    $parts = @('major', 'minor', 'patch') | ForEach-Object { [uint64]$coreMatch.Groups[$_].Value }
    if ($parts[0] -gt 65534 -or $parts[1] -gt 65535 -or $parts[2] -gt 65535)
    {
        throw "Version '$Version' exceeds the permanent (major+1).minor.patch.0 Store mapping."
    }

    return "$($parts[0] + 1).$($parts[1]).$($parts[2]).0"
}

function Export-PackageLogo
{
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][int]$Size)

    $source = [System.Drawing.Image]::FromFile($brandingIconPath)
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try
    {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, $Size, $Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally
    {
        $graphics.Dispose()
        $bitmap.Dispose()
        $source.Dispose()
    }
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

$windowsUnpackedDirectory = Get-RepositoryPath -Path $WindowsUnpackedDirectory -Description 'Windows win-unpacked input' -Root (Join-Path $repoRoot 'publish')
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
    throw "MSIX requires exactly one Store distribution-channel metadata value; found '$($storeMetadata -join ', ')'."
}

$managedRoot = Join-Path $windowsUnpackedDirectory 'resources\bin'
Assert-ReleaseNoticeClosure -RepositoryRoot $repoRoot -ManagedRoot $managedRoot -DesktopRoot $windowsUnpackedDirectory
$sourceVersion = Get-LorekeeperVersion (Join-Path $repoRoot 'Lorekeeper\Lorekeeper.csproj')
$assembly = [System.Reflection.Assembly]::LoadFrom((Join-Path $managedRoot 'Lorekeeper.dll'))
$assemblyVersion = $assembly.GetCustomAttributesData() |
    Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute' } |
    ForEach-Object { ([string]$_.ConstructorArguments[0].Value).Split('+')[0] }
if (@($assemblyVersion).Count -ne 1 -or $assemblyVersion -ne $sourceVersion)
{
    throw 'The unpacked application version must match the current source version.'
}

$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$msixVersion = ConvertTo-MsixVersion $sourceVersion
$packageRoot = Join-Path $outputDirectory 'package-root'
$appDirectory = Join-Path $packageRoot 'app'
$assetsDirectory = Join-Path $packageRoot 'Assets'
$packageFlavor = if ($LocalValidation) { 'LocalValidation' } else { 'Store' }
$packagePath = Join-Path $outputDirectory "Lorekeeper-$packageFlavor-$sourceVersion-x64.msix"
$unpackDirectory = Join-Path $outputDirectory 'unpacked'
$certificate = $null
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Image]::FromFile($brandingIconPath)
try
{
    if ($icon.Width -ne $icon.Height -or $icon.Width -lt 150)
    {
        throw 'The package branding input must be a square PNG of at least 150 pixels.'
    }
}
finally { $icon.Dispose() }
if ($LocalValidation) { Find-WindowsSdkTool 'signtool.exe' | Out-Null }
if ($CheckOnly)
{
    Write-Host "MSIX plan: source $sourceVersion -> package $msixVersion; $packageFlavor; $packagePath" -ForegroundColor Green
    Write-Host 'Input, Store metadata, source version, notices, branding, SDK tools, identity, and contained output paths passed inspection. No package or certificate was created.'
    return
}

Remove-GeneratedDirectory $outputDirectory
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

try
{
    Get-ChildItem -LiteralPath $windowsUnpackedDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $appDirectory -Recurse -Force
    }
    foreach ($asset in @(
        @{ Name = 'StoreLogo.png'; Size = 50 },
        @{ Name = 'Square44x44Logo.png'; Size = 44 },
        @{ Name = 'Square150x150Logo.png'; Size = 150 }))
    {
        Export-PackageLogo -Path (Join-Path $assetsDirectory $asset.Name) -Size $asset.Size
    }

    $manifest = [xml][System.IO.File]::ReadAllText($manifestTemplatePath)
    $namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $manifestIdentity = $manifest.SelectSingleNode('/p:Package/p:Identity', $namespaces)
    $manifestIdentity.SetAttribute('Name', [string]$identity.packageIdentityName)
    $manifestIdentity.SetAttribute('Publisher', [string]$identity.packageIdentityPublisher)
    $manifestIdentity.SetAttribute('Version', $msixVersion)
    $manifest.SelectSingleNode('/p:Package/p:Properties/p:DisplayName', $namespaces).InnerText = [string]$identity.displayName
    $manifest.SelectSingleNode('/p:Package/p:Properties/p:PublisherDisplayName', $namespaces).InnerText = [string]$identity.publisherDisplayName
    $visuals = $manifest.SelectSingleNode('/p:Package/p:Applications/p:Application/uap:VisualElements', $namespaces)
    $visuals.SetAttribute('DisplayName', [string]$identity.displayName)
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create((Join-Path $packageRoot 'AppxManifest.xml'), $settings)
    try { $manifest.Save($writer) }
    finally { $writer.Dispose() }

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

    Assert-ReleaseNoticeClosure -RepositoryRoot $repoRoot -ManagedRoot (Join-Path $unpackDirectory 'app/resources/bin') -DesktopRoot (Join-Path $unpackDirectory 'app')
    $signatureResult = 'Unsigned Store upload artifact; Microsoft signs accepted packages. No certificate operation occurred.'
    if ($LocalValidation)
    {
        $signTool = Find-WindowsSdkTool 'signtool.exe'
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -KeyUsage DigitalSignature `
            -Subject 'CN=Lorekeeper Local MSIX Test' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -FriendlyName 'Lorekeeper local MSIX validation (ephemeral)'
        Invoke-CheckedTool $signTool @('sign', '/fd', 'SHA256', '/sha1', $certificate.Thumbprint, '/s', 'My', $packagePath)
        Test-EmbeddedMsixSignature -PackagePath $packagePath -ExpectedThumbprint $certificate.Thumbprint
        $signatureResult = 'Embedded CMS integrity and ephemeral current-user signer passed; installation and trust-chain acceptance remain unperformed.'
    }

    $report = [pscustomobject][ordered]@{
        formatVersion = 2
        scope = 'Windows full-trust MSIX package preparation only. No installation, Store submission, provider call, or GitHub publication occurred.'
        package = [pscustomobject][ordered]@{
            relativePath = [System.IO.Path]::GetRelativePath($repoRoot, $packagePath).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            manifestVersion = $msixVersion
            sourceVersion = $sourceVersion
            identityName = [string]$identity.packageIdentityName
            distributionChannel = $storeMetadata[0]
            fullTrust = $true
        }
        validation = [pscustomobject][ordered]@{
            makeAppxSemanticValidation = 'passed during pack without /nv'
            requiredContent = @('AppxManifest.xml', 'Assets/*.png', 'app/Lorekeeper.exe', 'app/resources/bin/Lorekeeper.dll', 'app/resources/bin/press-runtime/lorekeeper-press.exe')
            signature = $signatureResult
            dataPreservation = 'Manifest excludes LocalAppData/Lorekeeper virtualization on Windows 11 and disables filesystem virtualization on Windows 10; installed update/uninstall evidence remains required.'
            installed = $false
        }
    }
    $reportPath = Join-Path $outputDirectory 'msix-package.json'
    [System.IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 5) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Prepared $packageFlavor MSIX package: $packagePath" -ForegroundColor Green
    Write-Host 'The package was not installed. Installation needs a separately authorized disposable test profile.' -ForegroundColor Yellow
}
finally
{
    if ($certificate)
    {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey -Force -ErrorAction Stop
    }
}
