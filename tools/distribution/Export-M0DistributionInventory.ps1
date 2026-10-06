[CmdletBinding()]
param(
    [string]$WindowsStageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$researchDirectory = Join-Path $repoRoot 'docs\research'
$jsonOutputPath = Join-Path $researchDirectory 'm0.4-distribution-inventory.json'
$markdownOutputPath = Join-Path $researchDirectory 'm0.4-distribution-inventory.md'
$projectAssetsPath = Join-Path $repoRoot 'Lorekeeper\obj\project.assets.json'
$pressProjectPath = Join-Path $repoRoot 'Lorekeeper.Press\Cargo.toml'
$semanticEditorDirectory = Join-Path $repoRoot 'tools\semantic-editor'
$semanticEditorLockPath = Join-Path $semanticEditorDirectory 'package-lock.json'
$distributionPolicyPath = Join-Path $repoRoot 'tools\distribution\DistributionAdmissionPolicy.json'
$distributionPolicyScriptPath = Join-Path $repoRoot 'tools\distribution\DistributionAdmissionPolicy.ps1'
$noticeManifestPath = Join-Path $repoRoot 'licenses/third-party/sources.json'
$nugetPackageRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'

. $distributionPolicyScriptPath

if ([string]::IsNullOrWhiteSpace($WindowsStageDirectory))
{
    $WindowsStageDirectory = Join-Path $repoRoot 'publish\win-x64\win-unpacked'
}

function Get-RepositoryRelativePath
{
    param([Parameter(Mandatory)][string]$Path)

    return [System.IO.Path]::GetRelativePath($repoRoot, [System.IO.Path]::GetFullPath($Path)).Replace('\', '/')
}

function Require-RepositoryPath
{
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $prefix = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Description must stay inside the repository: $fullPath"
    }

    return $fullPath
}

function New-InventoryEntry
{
    param(
        [Parameter(Mandatory)][string]$Kind,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][ValidateSet('NuGet', 'npm', 'Cargo', 'native-vendored', 'fonts', 'ICC', 'assets')][string]$DependencyCategory,
        [Parameter(Mandatory)][ValidateSet('satisfied', 'needs-inclusion', 'blocked', 'unresolved-provenance')][string]$Status,
        [Parameter(Mandatory)][string]$LicenseEvidence,
        [Parameter(Mandatory)][string]$AttributionAction,
        [Parameter(Mandatory)][string[]]$Channels,
        [Parameter(Mandatory)][string[]]$Sources,
        [Parameter()][AllowNull()][object]$Admission
    )

    $admissionData = if ($null -eq $Admission) {
        [pscustomobject][ordered]@{
            admissionStatus = 'unresolved'
            licenseExpression = $null
            selectedLicenseExpression = $null
            selectedOrBranches = @()
            restrictionClasses = @()
            diagnostics = @('License metadata is missing or is not an SPDX expression; this is unresolved provenance, not an automatic policy rejection.')
        }
    } else {
        $Admission
    }

    return [pscustomobject][ordered]@{
        kind = $Kind
        name = $Name
        version = $Version
        dependencyCategory = $DependencyCategory
        status = $Status
        licenseEvidence = $LicenseEvidence
        attributionAction = $AttributionAction
        channels = @($Channels | Sort-Object)
        sources = @($Sources | Sort-Object)
        admissionStatus = [string]$admissionData.admissionStatus
        licenseExpression = $admissionData.licenseExpression
        selectedLicenseExpression = $admissionData.selectedLicenseExpression
        selectedOrBranches = @($admissionData.selectedOrBranches)
        restrictionClasses = @($admissionData.restrictionClasses)
        policyDiagnostics = @($admissionData.diagnostics)
    }
}

function Get-NuspecLicenseEvidence
{
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$Version
    )

    $packageDirectory = Join-Path (Join-Path $nugetPackageRoot $PackageId.ToLowerInvariant()) $Version.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container))
    {
        return [pscustomobject]@{
            status = 'unresolved-provenance'
            expression = $null
            evidence = 'The restored NuGet package cache entry is unavailable on this machine.'
            action = 'Restore the exact package and record its nuspec license metadata before release review.'
        }
    }

    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspec)
    {
        return [pscustomobject]@{
            status = 'unresolved-provenance'
            expression = $null
            evidence = 'The restored NuGet package contains no nuspec file.'
            action = 'Obtain upstream license metadata before release review.'
        }
    }

    [xml]$nuspecXml = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $nuspecXml.package.metadata
    $licenseNode = $metadata.license
    $license = if ($null -ne $licenseNode) { [string]$licenseNode.'#text' } else { '' }
    $licenseUrl = [string]$metadata.licenseUrl
    if (-not [string]::IsNullOrWhiteSpace($license))
    {
        $licenseType = [string]$licenseNode.type
        return [pscustomobject]@{
            status = 'needs-inclusion'
            expression = if ([string]$licenseNode.type -ieq 'expression') { $license } else { $null }
            evidence = "NuGet nuspec license $licenseType '$license' ($($PackageId)/$Version)."
            action = 'Retain the applicable license text and add its required notice before public distribution.'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($licenseUrl))
    {
        return [pscustomobject]@{
            status = 'unresolved-provenance'
            expression = $null
            evidence = "NuGet nuspec license URL '$licenseUrl' ($($PackageId)/$Version)."
            action = 'Resolve the linked license text through authoritative metadata before public distribution.'
        }
    }

    return [pscustomobject]@{
        status = 'unresolved-provenance'
        expression = $null
        evidence = "NuGet nuspec contains no license expression, file, or URL ($($PackageId)/$Version)."
        action = 'Obtain authoritative upstream licensing terms before public distribution.'
    }
}

function Get-DirectoryAggregateHash
{
    param([Parameter(Mandatory)][string]$Directory)

    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName)
        {
            $relativePath = [System.IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
            $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $line = "$relativePath`t$($file.Length)`t$fileHash`n"
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($line)
            [void]$hasher.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
        }
        [void]$hasher.TransformFinalBlock([byte[]]::new(0), 0, 0)
        return [System.Convert]::ToHexStringLower($hasher.Hash)
    }
    finally
    {
        $hasher.Dispose()
    }
}

function Get-QuotedMarkdownCell
{
    param([AllowNull()][string]$Value)

    return (($Value ?? '').Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' '))
}

$windowsStageDirectory = Require-RepositoryPath -Path $WindowsStageDirectory -Description 'Windows stage directory'
$stageAvailable = Test-Path -LiteralPath $windowsStageDirectory -PathType Container
foreach ($requiredInput in @($projectAssetsPath, $pressProjectPath, $semanticEditorLockPath))
{
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf))
    {
        throw "Required local inventory input is missing: $requiredInput"
    }
}
$distributionPolicy = Get-DistributionAdmissionPolicy -Path $distributionPolicyPath
& (Join-Path $PSScriptRoot 'Export-ThirdPartyNotices.ps1') -CheckOnly
$noticeManifest = Get-Content -LiteralPath $noticeManifestPath -Raw | ConvertFrom-Json
$runtimeNoticeManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'licenses/runtime-notices/sources.json') -Raw | ConvertFrom-Json

$entries = [System.Collections.Generic.List[object]]::new()
$allChannels = @('free-windows', 'store-windows', 'direct-linux', 'direct-mac', 'mac-app-store')

# The first-party owner decision is separate from the third-party dependency gate.
$rootLicensePath = Join-Path $repoRoot 'LICENSE'
$firstPartyAdmission = [pscustomobject]@{ admissionStatus = 'admitted'; licenseExpression = 'PolyForm-Noncommercial-1.0.0 OR PolyForm-Internal-Use-1.0.0'; selectedLicenseExpression = 'Owner-approved first-party alternatives'; selectedOrBranches = @(); restrictionClasses = @('commercial-redistribution'); diagnostics = @('First-party owner decision; does not relax the third-party dependency gate.') }
$entries.Add((New-InventoryEntry -Kind 'application-source' -Name 'Lorekeeper source license' -Version 'owner-approved 2026-10-05' `
    -DependencyCategory 'assets' `
    -Status 'satisfied' `
    -LicenseEvidence 'Root LICENSE offers the unchanged official Noncommercial or Internal Use 1.0.0 terms. Commercial bookmaking and internal business use are permitted; commercial software distribution and paid external hosting are outside these grants.' `
    -AttributionAction 'Retain LICENSE and both full license texts with every package; first-party scope never replaces third-party licenses.' `
    -Channels $allChannels -Sources @('LICENSE', 'licenses/PolyForm-Noncommercial-1.0.0.txt', 'licenses/PolyForm-Internal-Use-1.0.0.txt') -Admission $firstPartyAdmission))
$entries.Add((New-InventoryEntry -Kind 'third-party-notices' -Name 'repository-wide third-party notice' -Version 'generated exact closure' `
    -DependencyCategory 'assets' `
    -Status 'satisfied' -LicenseEvidence 'Export-ThirdPartyNotices.ps1 -CheckOnly verified exact package identities and SHA-256 retained license/notice bytes.' `
    -AttributionAction 'Regenerate and review after dependency changes; ship the root notice, retained full terms, and adjacent native/editor/font notices.' `
    -Channels $allChannels -Sources @('THIRD-PARTY-NOTICES.txt', 'licenses/third-party/sources.json') -Admission ([pscustomobject]@{ admissionStatus='admitted'; licenseExpression=$null; selectedLicenseExpression='Exact retained notice manifest'; selectedOrBranches=@(); restrictionClasses=@(); diagnostics=@() })))

# Restored managed closure.
$assets = Get-Content -LiteralPath $projectAssetsPath -Raw | ConvertFrom-Json
foreach ($property in $assets.libraries.PSObject.Properties | Sort-Object Name)
{
    if ($property.Value.type -ne 'package')
    {
        continue
    }

    $separator = $property.Name.LastIndexOf('/')
    if ($separator -lt 1)
    {
        throw "Unexpected NuGet library key: $($property.Name)"
    }
    $packageId = $property.Name.Substring(0, $separator)
    $packageVersion = $property.Name.Substring($separator + 1)
    $license = Get-NuspecLicenseEvidence -PackageId $packageId -Version $packageVersion
    $admission = Get-DistributionMetadataDecision `
        -LicenseExpression $license.expression `
        -LicenseEvidence $license.evidence `
        -Policy $distributionPolicy `
        -DependencyCategory 'NuGet'
    $retainedDecision = Get-DistributionRetainedPackageDecision -PackageIdentity $property.Name -Policy $distributionPolicy -Manifest $noticeManifest -RepositoryRoot $repoRoot
    if ($null -ne $retainedDecision) { $admission = $retainedDecision }
    $retainedRecord = @($noticeManifest.packages | Where-Object { $_.package -ceq $property.Name })
    if ($retainedRecord.Count -eq 1) { $license.evidence += ' Full license and bundled transitive notices retained in licenses/third-party/sources.json for the exact package content hash.' }
    $entries.Add((New-InventoryEntry -Kind 'nuget-package' -Name $packageId -Version $packageVersion `
        -DependencyCategory 'NuGet' `
        -Status $(if ($admission.admissionStatus -eq 'admitted' -and $retainedRecord.Count -eq 1) { 'satisfied' } elseif ($admission.admissionStatus -eq 'admitted') { 'needs-inclusion' } elseif ($admission.admissionStatus -eq 'blocked') { 'blocked' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $license.evidence `
        -AttributionAction $(if ($admission.admissionStatus -eq 'admitted') { 'Ship retained full terms and required notices; exact-version exceptions keep their recorded linking/distribution scope.' } elseif ($admission.admissionStatus -eq 'blocked') { 'Remove or replace this dependency before distribution.' } else { $license.action }) `
        -Channels $allChannels -Sources @('Lorekeeper/obj/project.assets.json', "nuget:$packageId/$packageVersion") `
        -Admission $admission))
}

foreach ($runtime in $runtimeNoticeManifest.packages)
{
    $runtimeChannels = switch ($runtime.runtimeIdentifier) {
        'win-x64' { @('free-windows', 'store-windows') }
        'linux-x64' { @('direct-linux') }
        'osx-arm64' { @('direct-mac', 'mac-app-store') }
        default { throw 'Unknown retained runtime target.' }
    }
    $runtimeEvidence = "Exact SDK runtime-pack source $($runtime.package), SHA512 $($runtime.sha512), upstream commit $($runtime.repositoryCommit); full supplier license and available third-party notices retained with byte hashes."
    $entries.Add((New-InventoryEntry -Kind 'sdk-runtime-pack' -Name $runtime.packageId -Version $runtime.version `
        -DependencyCategory 'native-vendored' -Status 'satisfied' -LicenseEvidence $runtimeEvidence `
        -AttributionAction 'Retain licenses/runtime-notices/; verify actual packaged includedFrameworks match this exact version before target distribution.' `
        -Channels $runtimeChannels -Sources @('licenses/runtime-notices/sources.json', $runtime.evidence.path) `
        -Admission (Get-DistributionMetadataDecision -LicenseExpression 'MIT' -LicenseEvidence $runtimeEvidence -Policy $distributionPolicy -DependencyCategory 'native-vendored')))
}

# Locked Press dependency metadata is read without a network request.
$cargoMetadataText = & cargo metadata --manifest-path $pressProjectPath --locked --offline --format-version 1
if ($LASTEXITCODE -ne 0)
{
    throw 'cargo metadata --locked --offline failed; restore the locked Press dependencies locally before inventory generation.'
}
$cargoMetadata = ($cargoMetadataText -join [Environment]::NewLine) | ConvertFrom-Json
$pressRuntimeDirectory = Join-Path $windowsStageDirectory 'resources/bin/press-runtime'
$pressNoticePath = Join-Path $pressRuntimeDirectory 'THIRD-PARTY-NOTICES.txt'
$pressSbomPath = Join-Path $pressRuntimeDirectory 'sbom.json'
$pressIntegrityPath = Join-Path $pressRuntimeDirectory 'lorekeeper-press-runtime.json'
$pressNoticeText = $null
$pressSbom = $null
$lockedCargoChecksums = @{}
$cargoLockText = [IO.File]::ReadAllText((Join-Path $repoRoot 'Lorekeeper.Press/Cargo.lock')).Replace("`r`n", "`n")
foreach ($match in [regex]::Matches($cargoLockText, '(?ms)^\[\[package\]\]\s*(?<body>.*?)(?=^\[\[package\]\]|\z)'))
{
    $fields = @{}
    foreach ($name in @('name', 'version', 'source', 'checksum'))
    {
        $field = [regex]::Match($match.Groups['body'].Value, ('(?m)^{0} = "(?<value>[^"]*)"$' -f $name))
        $fields[$name] = $field.Groups['value'].Value
    }
    $lockedCargoChecksums["$($fields.name)`0$($fields.version)`0$($fields.source)"] = $fields.checksum
}
if ((Test-Path -LiteralPath $pressNoticePath -PathType Leaf) -and
    (Test-Path -LiteralPath $pressSbomPath -PathType Leaf) -and
    (Test-Path -LiteralPath $pressIntegrityPath -PathType Leaf))
{
    $pressIntegrity = Get-Content -LiteralPath $pressIntegrityPath -Raw | ConvertFrom-Json
    foreach ($path in @($pressNoticePath, $pressSbomPath))
    {
        $records = @($pressIntegrity.files | Where-Object relativePath -CEQ ([IO.Path]::GetFileName($path)))
        if ($records.Count -ne 1 -or $records[0].sha256 -cne (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
        {
            throw 'The staged Press notice or SBOM differs from its integrity manifest.'
        }
    }
    $pressNoticeText = [IO.File]::ReadAllText($pressNoticePath).Replace("`r`n", "`n")
    $pressSbom = Get-Content -LiteralPath $pressSbomPath -Raw | ConvertFrom-Json
}
foreach ($package in $cargoMetadata.packages | Where-Object { $_.name -ne 'lorekeeper-press' } | Sort-Object name, version)
{
    $hasLicense = -not [string]::IsNullOrWhiteSpace([string]$package.license)
    $cargoEvidence = if ($hasLicense) { "Cargo metadata license '$($package.license)'." } else { 'Cargo metadata contains no license field.' }
    $cargoAdmission = Get-DistributionMetadataDecision `
        -LicenseExpression ([string]$package.license) `
        -LicenseEvidence $cargoEvidence `
        -Policy $distributionPolicy `
        -DependencyCategory 'Cargo'
    $noticeCovered = $false
    if ($null -ne $pressSbom)
    {
        $records = @($pressSbom.packages | Where-Object { $_.name -ceq $package.name -and $_.version -ceq $package.version })
        $key = "$($package.name)`0$($package.version)`0$($package.source)"
        $noticeCovered = $records.Count -eq 1 -and $records[0].license -ceq $package.license -and
            $records[0].source -ceq $package.source -and $records[0].checksum -ceq $lockedCargoChecksums[$key] -and
            $pressNoticeText.Contains("$($package.name) $($package.version) - $($package.license)`n", [StringComparison]::Ordinal)
        $packageRoot = Split-Path $package.manifest_path -Parent
        $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -Match '^(LICENSE|COPYING|UNLICENSE|NOTICE)')
        if ($package.name -ceq 'hypher') { $licenseFiles += Get-Item -LiteralPath (Join-Path $packageRoot 'README.md') }
        foreach ($file in $licenseFiles)
        {
            if (-not $pressNoticeText.Contains([IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n"), [StringComparison]::Ordinal)) { $noticeCovered = $false }
        }
    }
    if ($noticeCovered) { $cargoEvidence += ' Exact current Cargo.lock checksum, package/source/license identity, full supplied license texts, and integrity-manifest hashes match the staged Press notice and SBOM.' }
    $entries.Add((New-InventoryEntry -Kind 'cargo-package' -Name ([string]$package.name) -Version ([string]$package.version) `
        -DependencyCategory 'Cargo' `
        -Status $(if ($cargoAdmission.admissionStatus -eq 'admitted' -and $noticeCovered) { 'satisfied' } elseif ($cargoAdmission.admissionStatus -eq 'admitted') { 'needs-inclusion' } elseif ($cargoAdmission.admissionStatus -eq 'blocked') { 'blocked' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $cargoEvidence `
        -AttributionAction $(if ($noticeCovered -and $cargoAdmission.admissionStatus -eq 'admitted') { 'Retain this exact generated Press notice and SBOM in each platform runtime; native target closure remains separate.' } elseif ($cargoAdmission.admissionStatus -eq 'admitted') { 'Current staged notice/SBOM coverage is absent or mismatched; regenerate from exact Cargo.lock before distribution.' } elseif ($cargoAdmission.admissionStatus -eq 'blocked') { 'Remove or replace this dependency before distribution.' } else { 'Obtain authoritative upstream licensing terms before public distribution.' }) `
        -Channels $allChannels -Sources @('Lorekeeper.Press/Cargo.lock', 'Lorekeeper.Press/Cargo.toml', 'publish/win-x64/win-unpacked/resources/bin/press-runtime/THIRD-PARTY-NOTICES.txt', 'publish/win-x64/win-unpacked/resources/bin/press-runtime/sbom.json') `
        -Admission $cargoAdmission))
}

# Semantic-editor dependencies are exact-lock inputs. Runtime MIT notice coverage is already shipped.
$semanticLock = Get-Content -LiteralPath $semanticEditorLockPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($property in $semanticLock['packages'].GetEnumerator() | Where-Object { $_.Key -like 'node_modules/*' } | Sort-Object Key)
{
    $lockEntry = $property.Value
    $packageName = $property.Key.Substring('node_modules/'.Length)
    $packageJsonPath = Join-Path $semanticEditorDirectory ($property.Key.Replace('/', '\') + '\package.json')
    $licenseText = ''
    $licenseEvidence = ''
    if (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)
    {
        $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
        $licenseProperty = $packageJson.PSObject.Properties['license']
        $rawLicense = if ($null -ne $licenseProperty) { $licenseProperty.Value } else { $null }
        if ($rawLicense -is [string])
        {
            $licenseText = [string]$rawLicense
        }
        elseif ($null -ne $rawLicense -and $null -ne $rawLicense.PSObject.Properties['type'] -and
            -not [string]::IsNullOrWhiteSpace([string]$rawLicense.type))
        {
            $licenseText = [string]$rawLicense.type
        }
        $licenseEvidence = if ([string]::IsNullOrWhiteSpace($licenseText)) {
            'Installed package metadata is available but has no machine-readable license field.'
        } else {
            "Installed package metadata reports license '$licenseText'."
        }
    }
    else
    {
        if ($packageName -like '@esbuild/*' -and [bool]$lockEntry['dev'] -and [bool]$lockEntry['optional'])
        {
            $esbuildMetadata = Get-Content -LiteralPath (Join-Path $semanticEditorDirectory 'node_modules/esbuild/package.json') -Raw | ConvertFrom-Json
            if ($esbuildMetadata.version -ne [string]$lockEntry['version'] -or $esbuildMetadata.license -ne 'MIT') { throw 'Optional esbuild platform metadata differs from its exact build-tool version.' }
            $licenseText = 'MIT'
            $licenseEvidence = 'Non-host optional esbuild platform package; exact lock version matches the installed MIT-licensed esbuild build tool. It is not shipped in the app runtime.'
        }
        else { $licenseEvidence = 'Installed package metadata is unavailable; restore the exact lock before release review.' }
    }
    $isBuildOnly = [bool]$lockEntry['dev']
    $isRuntimeMit = -not $isBuildOnly -and $licenseText -eq 'MIT'
    $npmAdmission = Get-DistributionMetadataDecision `
        -LicenseExpression $licenseText `
        -LicenseEvidence $licenseEvidence `
        -Policy $distributionPolicy `
        -DependencyCategory 'npm'
    $entries.Add((New-InventoryEntry -Kind 'npm-package' -Name $packageName -Version ([string]$lockEntry['version']) `
        -DependencyCategory 'npm' `
        -Status $(if ($isRuntimeMit) { 'satisfied' } elseif ($isBuildOnly -and $npmAdmission.admissionStatus -eq 'admitted') { 'satisfied' } elseif ($npmAdmission.admissionStatus -eq 'blocked') { 'blocked' } elseif ($npmAdmission.admissionStatus -eq 'admitted') { 'needs-inclusion' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $(if ($isRuntimeMit) { "MIT license in installed package metadata; runtime notice is shipped in tools/semantic-editor/THIRD_PARTY_NOTICES.md." } elseif ($isBuildOnly -and -not [string]::IsNullOrWhiteSpace($licenseText)) { "Build-only package license '$licenseText' in installed package metadata." } else { $licenseEvidence }) `
        -AttributionAction $(if ($isRuntimeMit) { 'Retain the checked-in semantic-editor notice with every shipped bundle.' } elseif ($isBuildOnly -and $npmAdmission.admissionStatus -eq 'admitted') { 'Keep out of the shipped runtime closure; reevaluate admission if its role changes.' } elseif ($npmAdmission.admissionStatus -eq 'blocked') { 'Remove or replace this dependency before distribution.' } elseif ([string]::IsNullOrWhiteSpace($licenseText)) { 'Restore the lock and obtain authoritative upstream licensing terms.' } else { 'Retain the selected license evidence and include the required notice before distribution.' }) `
        -Channels $allChannels -Sources @('tools/semantic-editor/package-lock.json', 'tools/semantic-editor/THIRD_PARTY_NOTICES.md') `
        -Admission $npmAdmission))
}

# Shipped browser, font, and Press assets.
foreach ($fontDirectory in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Lorekeeper\wwwroot\fonts') -Directory | Sort-Object Name)
{
    $oflPath = Join-Path $fontDirectory.FullName 'OFL.txt'
    $fontHasLicense = Test-Path -LiteralPath $oflPath -PathType Leaf
    $fontEvidence = if ($fontHasLicense) { 'Sourced and licensed in Lorekeeper/wwwroot/fonts/SOURCES.md with an adjacent OFL.txt.' } else { 'Missing adjacent OFL.txt.' }
    $fontAdmission = Get-DistributionMetadataDecision `
        -LicenseExpression $(if ($fontHasLicense) { 'OFL-1.1' } else { $null }) `
        -LicenseEvidence $fontEvidence `
        -Policy $distributionPolicy `
        -DependencyCategory 'fonts'
    $entries.Add((New-InventoryEntry -Kind 'bundled-font' -Name $fontDirectory.Name -Version 'Google Fonts source commit ec0464b978de222073645d6d3366f3fdf03376d8' `
        -DependencyCategory 'fonts' `
        -Status $(if ($fontHasLicense) { 'satisfied' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $fontEvidence `
        -AttributionAction $(if ($fontHasLicense) { 'Ship the unchanged OFL.txt with this font family.' } else { 'Restore the upstream OFL license before distribution.' }) `
        -Channels $allChannels -Sources @((Get-RepositoryRelativePath $fontDirectory.FullName), 'Lorekeeper/wwwroot/fonts/SOURCES.md') `
        -Admission $fontAdmission))
}
$bootstrapEvidence = 'Full Bootstrap 5.3.3 MIT text retained from its exact upstream tag in licenses/third-party/sources.json and shipped with the consolidated notice.'
$entries.Add((New-InventoryEntry -Kind 'shipped-web-asset' -Name 'Bootstrap' -Version '5.3.3' `
    -DependencyCategory 'assets' `
    -Status 'satisfied' -LicenseEvidence $bootstrapEvidence `
    -AttributionAction 'Ship the retained full Bootstrap MIT notice with every package.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/lib/bootstrap/dist/css/bootstrap.css') `
    -Admission (Get-DistributionMetadataDecision -LicenseExpression 'MIT' -LicenseEvidence $bootstrapEvidence -Policy $distributionPolicy -DependencyCategory 'assets')))
$visNetworkEvidence = 'Both Apache-2.0 and MIT license texts are retained beside the shipped distribution.'
$entries.Add((New-InventoryEntry -Kind 'shipped-web-asset' -Name 'vis-network' -Version 'checked-in distribution' `
    -DependencyCategory 'assets' `
    -Status 'satisfied' -LicenseEvidence 'Both Apache-2.0 and MIT license texts are retained beside the shipped distribution.' `
    -AttributionAction 'Retain both existing license texts in the shipped web asset closure.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/lib/vis-network/LICENSE-APACHE-2.0', 'Lorekeeper/wwwroot/lib/vis-network/LICENSE-MIT') `
    -Admission (Get-DistributionMetadataDecision -LicenseExpression 'Apache-2.0 AND MIT' -LicenseEvidence $visNetworkEvidence -Policy $distributionPolicy -DependencyCategory 'assets')))
$entries.Add((New-InventoryEntry -Kind 'branding-asset' -Name 'Lorekeeper branding and icons' -Version 'current' `
    -DependencyCategory 'assets' `
    -Status 'satisfied' -LicenseEvidence 'Maintainer-owned/generated source SVG and icon variants confirmed 2026-10-05 in branding/SOURCES.md.' `
    -AttributionAction 'Retain first-party ownership/provenance and the root source license.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/branding/SOURCES.md') -Admission $firstPartyAdmission))
$nativeRuntimeDirectory = Join-Path $windowsStageDirectory 'resources/bin/press-runtime'
$nativeExecutableName = if ($IsWindows) { 'lorekeeper-press.exe' } else { 'lorekeeper-press' }
$nativeRuntimeExecutable = Join-Path $nativeRuntimeDirectory $nativeExecutableName
$nativeRuntimeNotice = Join-Path $nativeRuntimeDirectory 'THIRD-PARTY-NOTICES.txt'
$nativeRuntimeSbom = Join-Path $nativeRuntimeDirectory 'sbom.json'
$nativeRuntimeEvidence = if ((Test-Path -LiteralPath $nativeRuntimeExecutable -PathType Leaf) -and
    (Test-Path -LiteralPath $nativeRuntimeNotice -PathType Leaf) -and
    (Test-Path -LiteralPath $nativeRuntimeSbom -PathType Leaf)) {
    'The staged Press executable, generated third-party notice, and SBOM are present; each transitive crate remains separately inventoried from Cargo metadata.'
} else {
    'The staged Press executable, generated third-party notice, or SBOM is missing from the local closure.'
}
$entries.Add((New-InventoryEntry -Kind 'native-runtime' -Name 'Lorekeeper Press native runtime' -Version 'current local Release stage' `
    -DependencyCategory 'native-vendored' `
    -Status 'unresolved-provenance' `
    -LicenseEvidence $nativeRuntimeEvidence `
    -AttributionAction 'Retain the generated notice and SBOM, then review the native closure for the target platform before distribution.' `
    -Channels @('free-windows', 'store-windows') -Sources @('eng/BuildPressRuntime.ps1', 'publish/win-x64/win-unpacked/resources/bin/press-runtime') `
    -Admission (Get-DistributionMetadataDecision -LicenseExpression $null -LicenseEvidence $nativeRuntimeEvidence -Policy $distributionPolicy -DependencyCategory 'native-vendored')))
$entries.Add((New-InventoryEntry -Kind 'press-asset' -Name 'CGATS21 CRPC1 ICC profile' -Version 'registry file' `
    -DependencyCategory 'ICC' `
    -Status 'satisfied' -LicenseEvidence 'Lorekeeper.Press/assets/profiles/SOURCE.md records the registry source, SHA-256, and unaltered distribution terms.' `
    -AttributionAction 'Retain the documented source record and distribute the unaltered profile only.' `
    -Channels $allChannels -Sources @('Lorekeeper.Press/assets/profiles/SOURCE.md', 'Lorekeeper.Press/assets/profiles/CGATS21_CRPC1.icc') `
    -Admission (Get-DistributionMetadataDecision -LicenseExpression $null -LicenseEvidence 'Lorekeeper.Press/assets/profiles/SOURCE.md records the registry source, SHA-256, and unaltered distribution terms.' -Policy $distributionPolicy -DependencyCategory 'ICC' -AuthoritativeNonSpdxEvidence)))

# The exact unsigned Windows staged closure is recorded without exposing machine paths.
$stageFiles = if ($stageAvailable) { @(Get-ChildItem -LiteralPath $windowsStageDirectory -Recurse -File) } else { @() }
$closureEvidenceFiles = @(
    $stageFiles |
        Where-Object { $_.Name -match '^(THIRD-PARTY-NOTICES|sbom|semantic-editor\.NOTICES|package(-lock)?\.json)' } |
        ForEach-Object { [System.IO.Path]::GetRelativePath($windowsStageDirectory, $_.FullName).Replace('\', '/') } |
        Sort-Object
)
$entries.Add((New-InventoryEntry -Kind 'windows-stage-closure' -Name 'unsigned Windows win-unpacked closure' -Version 'current local Release stage' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence $(if ($stageAvailable) { "Deterministic aggregate SHA-256 $(Get-DirectoryAggregateHash $windowsStageDirectory) over $($stageFiles.Count) staged files; retained evidence files: $($closureEvidenceFiles -join ', ')." } else { 'No current local Windows stage exists; no artifact closure or platform claim is made.' }) `
    -AttributionAction 'Compare the final Windows closure with the approved source license and third-party notice set before publishing.' `
    -Channels @('free-windows', 'store-windows') -Sources @('publish/win-x64/win-unpacked') `
    -Admission (Get-DistributionMetadataDecision -LicenseExpression $null -LicenseEvidence 'The staged closure is an aggregate; each package and asset is inventoried separately.' -Policy $distributionPolicy -DependencyCategory 'assets')))

$appImageNoticeManifest = Get-Content -LiteralPath (Join-Path $repoRoot 'licenses/appimage-runtime/sources.json') -Raw | ConvertFrom-Json
$appImageNoticeEvidence = @($appImageNoticeManifest.components.evidence) + @($appImageNoticeManifest.modifiedLibfuse.patch, $appImageNoticeManifest.modifiedLibfuse.notice)
foreach ($evidence in $appImageNoticeEvidence)
{
    $path = Require-RepositoryPath -Path (Join-Path $repoRoot $evidence.path) -Description 'AppImage notice evidence'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $evidence.sha256) { throw 'Retained AppImage notice/patch evidence is absent or changed.' }
}
$appImagePublication = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/appimage-publication.json') -Raw | ConvertFrom-Json
$appImageRuntimeExpression = 'MIT AND LGPL-2.1-only AND BSD-2-Clause AND BSD-3-Clause AND Zlib'
$appImageRuntimeAdmission = if ($appImagePublication.status -ceq 'approved')
{
    [pscustomobject]@{ admissionStatus='admitted'; licenseExpression=$appImageRuntimeExpression; selectedLicenseExpression=$appImageRuntimeExpression; selectedOrBranches=@(); restrictionClasses=@('copyleft'); diagnostics=@('Admitted only for the unchanged type2-runtime 20251108 binary while its approved LGPL corresponding-source and relink archive is published beside every AppImage.') }
}
else
{
    [pscustomobject]@{ admissionStatus='unresolved'; licenseExpression=$appImageRuntimeExpression; selectedLicenseExpression=$null; selectedOrBranches=@(); restrictionClasses=@('copyleft'); diagnostics=@('Public static LGPL source/relink clearance is not approved in eng/appimage-publication.json.') }
}
$entries.Add((New-InventoryEntry -Kind 'third-party-notices' -Name 'AppImage runtime full component notices' -Version 'type2-runtime 20251108; toolset 1.0.3' `
    -DependencyCategory 'native-vendored' -Status 'satisfied' `
    -LicenseEvidence 'Seven exact source/version records retain full original terms, musl component copyright/license blocks, SHA-256 notice hashes, and the modified-libfuse notice plus original supplier patch/date.' `
    -AttributionAction 'Ship licenses/appimage-runtime with the root notice and publish the approved corresponding-source archive beside every AppImage.' `
    -Channels @('direct-linux') -Sources @('eng/appimage-publication.json', 'licenses/appimage-runtime/sources.json', 'THIRD-PARTY-NOTICES.txt') `
    -Admission $appImageRuntimeAdmission))
# Account and publication prerequisites intentionally contain no credentials.
$entries.Add((New-InventoryEntry -Kind 'native-runtime' -Name 'AppImage embedded runtime and bundled native libraries' -Version 'type2-runtime 20251108; toolset 1.0.3' `
    -DependencyCategory 'native-vendored' -Status $(if ($appImagePublication.status -ceq 'approved') { 'satisfied' } else { 'unresolved-provenance' }) `
    -LicenseEvidence 'Exact runtime/source/toolset hashes and full component terms are retained. The curated selected toolset excludes optional legacy lib/x64 libraries. The corresponding-source archive supplies modified libfuse and runtime sources, build scripts, and a recorded modified-libfuse relink and AppImage repack. Supplier Alpine package revisions are inferred from branch state because its build logs expired; those components are permissive.' `
    -AttributionAction 'Verify the extracted target runtime and absence of excluded libraries, and publish the approved corresponding-source archive beside every AppImage.' `
    -Channels @('direct-linux') -Sources @('eng/appimage-publication.json', 'scripts/build-linux-release.ps1', 'licenses/appimage-runtime/sources.json', 'docs/evidence/public-sharing-audit.md') `
    -Admission $appImageRuntimeAdmission))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'GitHub public-repository clearance' -Version 'not started' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence 'The redacted all-ref and GitHub metadata audit is recorded separately; a final exact-ref recheck and owner visibility decision remain required.' `
    -AttributionAction 'Review docs/evidence/public-sharing-audit.md and its explicit limits before any later public visibility change.' `
    -Channels $allChannels -Sources @('docs/evidence/public-sharing-audit.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'Microsoft Partner Center identity, product, price, and Store signing' -Version 'owner input required' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence 'Free price is selected. Real Partner Center package identity and display name remain owner inputs; unsigned Store MSIX preparation does not require a personal production certificate.' `
    -AttributionAction 'Use tools/msix/store-identity.example.json and Build-WindowsMsix.ps1; submit only after separately authorized Partner Center access and real certification.' `
    -Channels @('store-windows') -Sources @('docs/v1-roadmap.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'Apple Developer identity, MAS profile, and real-device evidence' -Version 'owner input required' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence 'M0.6 remains account-gated; no Apple credentials, profile, or signed-device result exists.' `
    -AttributionAction 'Owner selects the signing host and supplies account/test-device access before M0.6 execution.' `
    -Channels @('direct-mac', 'mac-app-store') -Sources @('docs/v1-roadmap.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'macOS dependency and artifact closure' -Version 'not measured' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence 'This inventory examines the Windows staged closure only; native macOS closure belongs to M0.6.' `
    -AttributionAction 'Generate and review the signed native macOS closure only after M0.6 is unblocked.' `
    -Channels @('direct-mac', 'mac-app-store') -Sources @('scripts/build-macos-release.ps1', 'docs/v1-roadmap.md')))

$orderedEntries = @($entries | Sort-Object kind, name, version)
$summary = [ordered]@{}
foreach ($status in @('satisfied', 'needs-inclusion', 'blocked', 'unresolved-provenance'))
{
    $summary[$status] = @($orderedEntries | Where-Object { $_.status -eq $status }).Count
}
$admissionSummary = [ordered]@{}
foreach ($admissionStatus in @('admitted', 'unresolved', 'blocked'))
{
    $admissionSummary[$admissionStatus] = @($orderedEntries | Where-Object { $_.admissionStatus -eq $admissionStatus }).Count
}
$categorySummary = @(
    $distributionPolicy.dependencyCategories | Sort-Object id | ForEach-Object {
        $category = [string]$_.id
        [pscustomobject][ordered]@{
            dependencyCategory = $category
            entries = @($orderedEntries | Where-Object { $_.dependencyCategory -eq $category }).Count
            admitted = @($orderedEntries | Where-Object { $_.dependencyCategory -eq $category -and $_.admissionStatus -eq 'admitted' }).Count
            unresolved = @($orderedEntries | Where-Object { $_.dependencyCategory -eq $category -and $_.admissionStatus -eq 'unresolved' }).Count
            blocked = @($orderedEntries | Where-Object { $_.dependencyCategory -eq $category -and $_.admissionStatus -eq 'blocked' }).Count
        }
    }
)
$inventory = [pscustomobject][ordered]@{
    formatVersion = 1
    scope = 'Exact local dependency and retained notice inventory. First-party dual terms are selected; no publication, Store submission, visibility change, or target-platform acceptance is implied.'
    distributionPolicy = [pscustomobject][ordered]@{
        policyId = [string]$distributionPolicy.policyId
        schemaVersion = [int]$distributionPolicy.schemaVersion
        purpose = [string]$distributionPolicy.purpose
        prohibitedRestrictionClasses = @($distributionPolicy.prohibitedRestrictionClasses)
        gateStatus = if ($admissionSummary.blocked -gt 0) { 'blocked' } elseif ($admissionSummary.unresolved -gt 0) { 'unresolved' } else { 'admitted' }
        admissionSummary = [pscustomobject]$admissionSummary
    }
    channels = $allChannels
    summary = [pscustomobject]$summary
    categorySummary = $categorySummary
    entries = $orderedEntries
}
$json = $inventory | ConvertTo-Json -Depth 8

$markdown = [System.Text.StringBuilder]::new()
$markdownCodeTick = [char]0x60
[void]$markdown.AppendLine('# M0.4 local license and distribution inventory')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('Status: **Source-available dual terms and retained third-party notices are prepared. No repository visibility change, release publication, or Store submission has occurred.**')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('This deterministic report uses restored exact dependency metadata, locked Press/editor inputs, retained authoritative license evidence, and shipped web/font assets. A Windows stage is measured only when present. Platform closure and account prerequisites remain explicit; this report contains no credentials or machine paths.')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("The **$($distributionPolicy.policyId)** result is an engineering distribution gate, not legal advice. An ${markdownCodeTick}admitted${markdownCodeTick} expression is a recorded policy decision for the exact metadata observed here; it does not replace review of the complete license text, attribution, provenance, or target-specific closure. Missing or non-machine-readable metadata is **unresolved**, not an automatic rejection. No dependency is admitted when a known prohibited restriction or an unresolved required expression is the only available evidence.")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Status summary')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Status | Entries | Meaning |')
[void]$markdown.AppendLine('|---|---:|---|')
[void]$markdown.AppendLine("| Satisfied | $($summary['satisfied']) | Current retained evidence and notice handling are adequate for this engineering gate. |")
[void]$markdown.AppendLine("| Needs inclusion | $($summary['needs-inclusion']) | Evidence exists, but the required distributed notice is not yet consolidated. |")
[void]$markdown.AppendLine("| Blocked | $($summary['blocked']) | A known dependency expression violates the engineering distribution policy and must be removed or replaced. |")
[void]$markdown.AppendLine("| Unresolved provenance | $($summary['unresolved-provenance']) | Ownership, licensing, account, or native-closure evidence is absent. |")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Engineering distribution gate')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Admission | Entries | Meaning |')
[void]$markdown.AppendLine('|---|---:|---|')
[void]$markdown.AppendLine("| Admitted | $($admissionSummary['admitted']) | The exact SPDX expression has a selected policy-approved branch with no prohibited restriction class. |")
[void]$markdown.AppendLine("| Unresolved | $($admissionSummary['unresolved']) | Metadata, retained evidence, or an SPDX expression is missing or not recognized; investigate before distribution. |")
[void]$markdown.AppendLine("| Blocked | $($admissionSummary['blocked']) | A known expression has a prohibited restriction class, so this policy does not admit it. |")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('### Dependency categories')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Category | Entries | Admitted | Unresolved | Blocked |')
[void]$markdown.AppendLine('|---|---:|---:|---:|---:|')
foreach ($category in $categorySummary)
{
    [void]$markdown.AppendLine("| $(Get-QuotedMarkdownCell $category.dependencyCategory) | $($category.entries) | $($category.admitted) | $($category.unresolved) | $($category.blocked) |")
}
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Current blockers')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('- Exact first-party terms, Bootstrap, managed/native package notices, and branding provenance are retained. The restrictive first-party owner decision does not relax the permissive third-party gate.')
[void]$markdown.AppendLine('- libgit2 is admitted only as the unchanged compiled library linked into Lorekeeper under its exact retained unlimited linking exception; modifications or standalone redistribution require separate review.')
[void]$markdown.AppendLine('- The AppImage runtime statically links modified LGPL libfuse. It is admitted only for the unchanged type2-runtime 20251108 binary while eng/appimage-publication.json is approved and its corresponding-source/relink archive is published beside every AppImage.')
[void]$markdown.AppendLine('- Packaged platform closures, real Store identities/certification, and any Mac App Store work require their own recorded evidence. Absence is not silently treated as approval.')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Public-sharing audit')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('See `docs/evidence/public-sharing-audit.md` for the all-ref mirror, redacted scanner, GitHub metadata review, current findings, and limits. Keep detailed findings ignored and private. Recheck final refs before public visibility. Never automatically rewrite history, delete branches, change repository settings, or publish to resolve a finding.')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Inventory')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Category | Kind | Name | Version | Status | Admission | SPDX expression | Selected expression | Selected OR branches | Restrictions | License evidence | Required action | Channels | Sources |')
[void]$markdown.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($entry in $orderedEntries)
{
    $orBranches = if (@($entry.selectedOrBranches).Count -eq 0) { '' } else { ConvertTo-Json -InputObject @($entry.selectedOrBranches) -Compress -Depth 8 }
    [void]$markdown.AppendLine("| $(Get-QuotedMarkdownCell $entry.dependencyCategory) | $(Get-QuotedMarkdownCell $entry.kind) | $(Get-QuotedMarkdownCell $entry.name) | $(Get-QuotedMarkdownCell $entry.version) | $(Get-QuotedMarkdownCell $entry.status) | $(Get-QuotedMarkdownCell $entry.admissionStatus) | $(Get-QuotedMarkdownCell $entry.licenseExpression) | $(Get-QuotedMarkdownCell $entry.selectedLicenseExpression) | $(Get-QuotedMarkdownCell $orBranches) | $(Get-QuotedMarkdownCell (($entry.restrictionClasses) -join ', ')) | $(Get-QuotedMarkdownCell $entry.licenseEvidence) | $(Get-QuotedMarkdownCell $entry.attributionAction) | $(Get-QuotedMarkdownCell ($entry.channels -join ', ')) | $(Get-QuotedMarkdownCell ($entry.sources -join ', ')) |")
}

[System.IO.Directory]::CreateDirectory($researchDirectory) | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($jsonOutputPath, $json + [Environment]::NewLine, $utf8)
[System.IO.File]::WriteAllText($markdownOutputPath, $markdown.ToString(), $utf8)
Write-Host "Generated $(Get-RepositoryRelativePath $jsonOutputPath) and $(Get-RepositoryRelativePath $markdownOutputPath)."
