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
if (-not (Test-Path -LiteralPath $windowsStageDirectory -PathType Container))
{
    throw "Build the unsigned Windows package first; stage directory is missing: $windowsStageDirectory"
}
foreach ($requiredInput in @($projectAssetsPath, $pressProjectPath, $semanticEditorLockPath))
{
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf))
    {
        throw "Required local inventory input is missing: $requiredInput"
    }
}
$distributionPolicy = Get-DistributionAdmissionPolicy -Path $distributionPolicyPath

$entries = [System.Collections.Generic.List[object]]::new()
$allChannels = @('free-windows', 'store-windows', 'direct-mac', 'mac-app-store')

# Application source and distribution ownership are intentionally unresolved until the owner selects terms.
$rootLicensePath = Join-Path $repoRoot 'LICENSE'
$entries.Add((New-InventoryEntry -Kind 'application-source' -Name 'Lorekeeper source license' -Version 'unselected' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' `
    -LicenseEvidence $(if (Test-Path -LiteralPath $rootLicensePath) { 'Repository LICENSE exists, but no owner-approved source-license decision is recorded.' } else { 'No repository-wide LICENSE file exists.' }) `
    -AttributionAction 'Owner must select and add the approved repository license before publication.' `
    -Channels $allChannels -Sources @('LICENSE')))
$entries.Add((New-InventoryEntry -Kind 'third-party-notices' -Name 'repository-wide third-party notice' -Version 'absent' `
    -DependencyCategory 'assets' `
    -Status 'needs-inclusion' -LicenseEvidence 'No repository-wide NOTICE or THIRD-PARTY-NOTICES file exists.' `
    -AttributionAction 'Create the consolidated notice from dependencies admitted by the engineering distribution policy.' `
    -Channels $allChannels -Sources @('NOTICE', 'THIRD-PARTY-NOTICES.txt')))

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
    $entries.Add((New-InventoryEntry -Kind 'nuget-package' -Name $packageId -Version $packageVersion `
        -DependencyCategory 'NuGet' `
        -Status $(if ($admission.admissionStatus -eq 'admitted') { 'needs-inclusion' } elseif ($admission.admissionStatus -eq 'blocked') { 'blocked' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $license.evidence `
        -AttributionAction $(if ($admission.admissionStatus -eq 'admitted') { 'Retain the selected license evidence and include the required notice before distribution.' } elseif ($admission.admissionStatus -eq 'blocked') { 'Remove or replace this dependency before distribution.' } else { $license.action }) `
        -Channels $allChannels -Sources @('Lorekeeper/obj/project.assets.json', "nuget:$packageId/$packageVersion") `
        -Admission $admission))
}

# Locked Press dependency metadata is read without a network request.
$cargoMetadataText = & cargo metadata --manifest-path $pressProjectPath --locked --offline --format-version 1
if ($LASTEXITCODE -ne 0)
{
    throw 'cargo metadata --locked --offline failed; restore the locked Press dependencies locally before inventory generation.'
}
$cargoMetadata = ($cargoMetadataText -join [Environment]::NewLine) | ConvertFrom-Json
foreach ($package in $cargoMetadata.packages | Where-Object { $_.name -ne 'lorekeeper-press' } | Sort-Object name, version)
{
    $hasLicense = -not [string]::IsNullOrWhiteSpace([string]$package.license)
    $cargoEvidence = if ($hasLicense) { "Cargo metadata license '$($package.license)'." } else { 'Cargo metadata contains no license field.' }
    $cargoAdmission = Get-DistributionMetadataDecision `
        -LicenseExpression ([string]$package.license) `
        -LicenseEvidence $cargoEvidence `
        -Policy $distributionPolicy `
        -DependencyCategory 'Cargo'
    $entries.Add((New-InventoryEntry -Kind 'cargo-package' -Name ([string]$package.name) -Version ([string]$package.version) `
        -DependencyCategory 'Cargo' `
        -Status $(if ($cargoAdmission.admissionStatus -eq 'admitted') { 'needs-inclusion' } elseif ($cargoAdmission.admissionStatus -eq 'blocked') { 'blocked' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $cargoEvidence `
        -AttributionAction $(if ($cargoAdmission.admissionStatus -eq 'admitted') { 'Retain the selected license evidence in the generated Press notice and SBOM.' } elseif ($cargoAdmission.admissionStatus -eq 'blocked') { 'Remove or replace this dependency before distribution.' } else { 'Obtain authoritative upstream licensing terms before public distribution.' }) `
        -Channels $allChannels -Sources @('Lorekeeper.Press/Cargo.lock', 'Lorekeeper.Press/Cargo.toml') `
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
        $licenseEvidence = 'Installed package metadata is unavailable; restore the exact lock before release review.'
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
$bootstrapEvidence = 'Shipped CSS/JS headers identify Bootstrap 5.3.3 as MIT, but no Bootstrap license file is retained beside the copied distribution.'
$entries.Add((New-InventoryEntry -Kind 'shipped-web-asset' -Name 'Bootstrap' -Version '5.3.3' `
    -DependencyCategory 'assets' `
    -Status 'needs-inclusion' -LicenseEvidence 'Shipped CSS/JS headers identify Bootstrap 5.3.3 as MIT, but no Bootstrap license file is retained beside the copied distribution.' `
    -AttributionAction 'Add the verified Bootstrap MIT notice to the approved consolidated distribution notice.' `
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
    -Status 'unresolved-provenance' -LicenseEvidence 'No recorded ownership or source provenance accompanies the bundled branding assets.' `
    -AttributionAction 'Record ownership or upstream license/provenance before public distribution.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/branding')))
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
$stageFiles = @(Get-ChildItem -LiteralPath $windowsStageDirectory -Recurse -File)
$closureEvidenceFiles = @(
    $stageFiles |
        Where-Object { $_.Name -match '^(THIRD-PARTY-NOTICES|sbom|semantic-editor\.NOTICES|package(-lock)?\.json)' } |
        ForEach-Object { [System.IO.Path]::GetRelativePath($windowsStageDirectory, $_.FullName).Replace('\', '/') } |
        Sort-Object
)
$entries.Add((New-InventoryEntry -Kind 'windows-stage-closure' -Name 'unsigned Windows win-unpacked closure' -Version 'current local Release stage' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence "Deterministic aggregate SHA-256 $(Get-DirectoryAggregateHash $windowsStageDirectory) over $($stageFiles.Count) staged files; retained evidence files: $($closureEvidenceFiles -join ', ')." `
    -AttributionAction 'Compare the final Windows closure with the approved source license and third-party notice set before publishing.' `
    -Channels @('free-windows', 'store-windows') -Sources @('publish/win-x64/win-unpacked') `
    -Admission (Get-DistributionMetadataDecision -LicenseExpression $null -LicenseEvidence 'The staged closure is an aggregate; each package and asset is inventoried separately.' -Policy $distributionPolicy -DependencyCategory 'assets')))

# Account and publication prerequisites intentionally contain no credentials.
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'GitHub public-repository clearance' -Version 'not started' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence 'Public repository visibility requires the future M6 tracked-file and history audit.' `
    -AttributionAction 'Complete the documented M6 audit and resolve findings before changing repository visibility.' `
    -Channels $allChannels -Sources @('docs/research/m0.4-distribution-inventory.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'Microsoft Partner Center identity, product, price, and Store signing' -Version 'owner input required' `
    -DependencyCategory 'assets' `
    -Status 'unresolved-provenance' -LicenseEvidence 'No publisher identity, Store product reservation, pricing decision, or production signing material is in scope.' `
    -AttributionAction 'Owner supplies the account decisions only when M7 Store submission is authorized.' `
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
    scope = 'M0.4 local inventory and engineering distribution gate only; no source-license adoption, publication, signing, Store submission, or history scan.'
    distributionPolicy = [pscustomobject][ordered]@{
        policyId = [string]$distributionPolicy.policyId
        schemaVersion = [int]$distributionPolicy.schemaVersion
        purpose = [string]$distributionPolicy.purpose
        prohibitedRestrictionClasses = @($distributionPolicy.prohibitedRestrictionClasses)
        gateStatus = if ($admissionSummary.blocked -gt 0) { 'blocked' } elseif ($admissionSummary.unresolved -gt 0) { 'unresolved' } else { 'admitted' }
        admissionSummary = [pscustomobject]$admissionSummary
    }
    channels = @('direct-mac', 'free-windows', 'mac-app-store', 'store-windows')
    summary = [pscustomobject]$summary
    categorySummary = $categorySummary
    entries = $orderedEntries
}
$json = $inventory | ConvertTo-Json -Depth 8

$markdown = [System.Text.StringBuilder]::new()
$markdownCodeTick = [char]0x60
[void]$markdown.AppendLine('# M0.4 local license and distribution inventory')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('Status: **Inventory only — no license has been adopted, no repository has been published, and no Store account or signing action has been taken.**')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('This deterministic report is generated from restored local dependency metadata, locked Press and semantic-editor inputs, shipped web/font assets, and the unsigned Windows `win-unpacked` closure. It contains no credentials, machine paths, author data, or network-derived content.')
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
[void]$markdown.AppendLine('- The repository has no selected source license or consolidated third-party notice.')
[void]$markdown.AppendLine('- Bootstrap attribution must be retained in the future consolidated notice.')
[void]$markdown.AppendLine('- Lorekeeper branding ownership/provenance is unrecorded.')
[void]$markdown.AppendLine('- The macOS closure and all Store identities remain future M0.6/M7 work.')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Future M6 public-history audit procedure')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('Before public visibility, create a disposable local mirror containing every remote ref and tag. Run approved secret, private-data, and redistribution-right scanners plus manual review against that mirror; retain detailed findings only in ignored local evidence. Resolve every finding before visibility changes. Do not automatically rewrite history, change repository visibility, or publish while an unresolved finding remains.')
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
