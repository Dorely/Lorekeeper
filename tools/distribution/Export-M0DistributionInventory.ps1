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
$nugetPackageRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'

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
        [Parameter(Mandatory)][ValidateSet('satisfied', 'needs-inclusion', 'legal-review', 'unresolved-provenance')][string]$Status,
        [Parameter(Mandatory)][string]$LicenseEvidence,
        [Parameter(Mandatory)][string]$AttributionAction,
        [Parameter(Mandatory)][string[]]$Channels,
        [Parameter(Mandatory)][string[]]$Sources
    )

    return [pscustomobject][ordered]@{
        kind = $Kind
        name = $Name
        version = $Version
        status = $Status
        licenseEvidence = $LicenseEvidence
        attributionAction = $AttributionAction
        channels = @($Channels | Sort-Object)
        sources = @($Sources | Sort-Object)
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
            evidence = 'The restored NuGet package cache entry is unavailable on this machine.'
            action = 'Restore the exact package and record its nuspec license metadata before release review.'
        }
    }

    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspec)
    {
        return [pscustomobject]@{
            status = 'unresolved-provenance'
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
            status = 'legal-review'
            evidence = "NuGet nuspec license $licenseType '$license' ($($PackageId)/$Version)."
            action = 'Confirm redistribution obligations and add the approved notice before public distribution.'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($licenseUrl))
    {
        return [pscustomobject]@{
            status = 'legal-review'
            evidence = "NuGet nuspec license URL '$licenseUrl' ($($PackageId)/$Version)."
            action = 'Resolve the linked license text and record the approved notice before public distribution.'
        }
    }

    return [pscustomobject]@{
        status = 'unresolved-provenance'
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

$entries = [System.Collections.Generic.List[object]]::new()
$allChannels = @('free-windows', 'store-windows', 'direct-mac', 'mac-app-store')

# Application source and distribution ownership are intentionally unresolved until the owner selects terms.
$rootLicensePath = Join-Path $repoRoot 'LICENSE'
$entries.Add((New-InventoryEntry -Kind 'application-source' -Name 'Lorekeeper source license' -Version 'unselected' `
    -Status $(if (Test-Path -LiteralPath $rootLicensePath) { 'legal-review' } else { 'unresolved-provenance' }) `
    -LicenseEvidence $(if (Test-Path -LiteralPath $rootLicensePath) { 'Repository LICENSE exists and requires legal review.' } else { 'No repository-wide LICENSE file exists.' }) `
    -AttributionAction 'Owner must select and add the approved repository license before publication.' `
    -Channels $allChannels -Sources @('LICENSE')))
$entries.Add((New-InventoryEntry -Kind 'third-party-notices' -Name 'repository-wide third-party notice' -Version 'absent' `
    -Status 'needs-inclusion' -LicenseEvidence 'No repository-wide NOTICE or THIRD-PARTY-NOTICES file exists.' `
    -AttributionAction 'Create the approved consolidated notice after the legal review resolves each dependency obligation.' `
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
    $entries.Add((New-InventoryEntry -Kind 'nuget-package' -Name $packageId -Version $packageVersion `
        -Status $license.status -LicenseEvidence $license.evidence -AttributionAction $license.action `
        -Channels $allChannels -Sources @('Lorekeeper/obj/project.assets.json', "nuget:$packageId/$packageVersion")))
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
    $entries.Add((New-InventoryEntry -Kind 'cargo-package' -Name ([string]$package.name) -Version ([string]$package.version) `
        -Status $(if ($hasLicense) { 'legal-review' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $(if ($hasLicense) { "Cargo metadata license '$($package.license)'." } else { 'Cargo metadata contains no license field.' }) `
        -AttributionAction $(if ($hasLicense) { 'Confirm redistribution obligations and add the approved notice before public distribution.' } else { 'Obtain authoritative upstream licensing terms before public distribution.' }) `
        -Channels $allChannels -Sources @('Lorekeeper.Press/Cargo.lock', 'Lorekeeper.Press/Cargo.toml')))
}

# Semantic-editor dependencies are exact-lock inputs. Runtime MIT notice coverage is already shipped.
$semanticLock = Get-Content -LiteralPath $semanticEditorLockPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($property in $semanticLock['packages'].GetEnumerator() | Where-Object { $_.Key -like 'node_modules/*' } | Sort-Object Key)
{
    $lockEntry = $property.Value
    $packageName = $property.Key.Substring('node_modules/'.Length)
    $packageJsonPath = Join-Path $semanticEditorDirectory ($property.Key.Replace('/', '\') + '\package.json')
    $licenseText = ''
    if (Test-Path -LiteralPath $packageJsonPath -PathType Leaf)
    {
        $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
        $licenseText = [string]$packageJson.license
    }
    $isBuildOnly = [bool]$lockEntry['dev']
    $isRuntimeMit = -not $isBuildOnly -and $licenseText -eq 'MIT'
    $entries.Add((New-InventoryEntry -Kind 'npm-package' -Name $packageName -Version ([string]$lockEntry['version']) `
        -Status $(if ($isRuntimeMit) { 'satisfied' } elseif ($isBuildOnly -and -not [string]::IsNullOrWhiteSpace($licenseText)) { 'satisfied' } elseif ([string]::IsNullOrWhiteSpace($licenseText)) { 'unresolved-provenance' } else { 'legal-review' }) `
        -LicenseEvidence $(if ($isRuntimeMit) { "MIT license in installed package metadata; runtime notice is shipped in tools/semantic-editor/THIRD_PARTY_NOTICES.md." } elseif ($isBuildOnly -and -not [string]::IsNullOrWhiteSpace($licenseText)) { "Build-only package license '$licenseText' in installed package metadata." } elseif ([string]::IsNullOrWhiteSpace($licenseText)) { 'Installed package metadata is unavailable or has no license field.' } else { "Installed package license '$licenseText'." }) `
        -AttributionAction $(if ($isRuntimeMit) { 'Retain the checked-in semantic-editor notice with every shipped bundle.' } elseif ($isBuildOnly) { 'Keep out of shipped runtime closure; re-review if its role changes.' } elseif ([string]::IsNullOrWhiteSpace($licenseText)) { 'Restore the lock and obtain authoritative upstream licensing terms.' } else { 'Confirm redistribution obligations and add the approved notice before public distribution.' }) `
        -Channels $allChannels -Sources @('tools/semantic-editor/package-lock.json', 'tools/semantic-editor/THIRD_PARTY_NOTICES.md')))
}

# Shipped browser, font, and Press assets.
foreach ($fontDirectory in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Lorekeeper\wwwroot\fonts') -Directory | Sort-Object Name)
{
    $oflPath = Join-Path $fontDirectory.FullName 'OFL.txt'
    $entries.Add((New-InventoryEntry -Kind 'bundled-font' -Name $fontDirectory.Name -Version 'Google Fonts source commit ec0464b978de222073645d6d3366f3fdf03376d8' `
        -Status $(if (Test-Path -LiteralPath $oflPath -PathType Leaf) { 'satisfied' } else { 'unresolved-provenance' }) `
        -LicenseEvidence $(if (Test-Path -LiteralPath $oflPath -PathType Leaf) { 'Sourced and licensed in Lorekeeper/wwwroot/fonts/SOURCES.md with an adjacent OFL.txt.' } else { 'Missing adjacent OFL.txt.' }) `
        -AttributionAction $(if (Test-Path -LiteralPath $oflPath -PathType Leaf) { 'Ship the unchanged OFL.txt with this font family.' } else { 'Restore the upstream OFL license before distribution.' }) `
        -Channels $allChannels -Sources @((Get-RepositoryRelativePath $fontDirectory.FullName), 'Lorekeeper/wwwroot/fonts/SOURCES.md')))
}
$entries.Add((New-InventoryEntry -Kind 'shipped-web-asset' -Name 'Bootstrap' -Version '5.3.3' `
    -Status 'needs-inclusion' -LicenseEvidence 'Shipped CSS/JS headers identify Bootstrap 5.3.3 as MIT, but no Bootstrap license file is retained beside the copied distribution.' `
    -AttributionAction 'Add the verified Bootstrap MIT notice to the approved consolidated distribution notice.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/lib/bootstrap/dist/css/bootstrap.css')))
$entries.Add((New-InventoryEntry -Kind 'shipped-web-asset' -Name 'vis-network' -Version 'checked-in distribution' `
    -Status 'satisfied' -LicenseEvidence 'Both Apache-2.0 and MIT license texts are retained beside the shipped distribution.' `
    -AttributionAction 'Retain both existing license texts in the shipped web asset closure.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/lib/vis-network/LICENSE-APACHE-2.0', 'Lorekeeper/wwwroot/lib/vis-network/LICENSE-MIT')))
$entries.Add((New-InventoryEntry -Kind 'branding-asset' -Name 'Lorekeeper branding and icons' -Version 'current' `
    -Status 'unresolved-provenance' -LicenseEvidence 'No recorded ownership or source provenance accompanies the bundled branding assets.' `
    -AttributionAction 'Record ownership or upstream license/provenance before public distribution.' `
    -Channels $allChannels -Sources @('Lorekeeper/wwwroot/branding')))
$entries.Add((New-InventoryEntry -Kind 'press-asset' -Name 'CGATS21 CRPC1 ICC profile' -Version 'registry file' `
    -Status 'satisfied' -LicenseEvidence 'Lorekeeper.Press/assets/profiles/SOURCE.md records the registry source, SHA-256, and unaltered distribution terms.' `
    -AttributionAction 'Retain the documented source record and distribute the unaltered profile only.' `
    -Channels $allChannels -Sources @('Lorekeeper.Press/assets/profiles/SOURCE.md', 'Lorekeeper.Press/assets/profiles/CGATS21_CRPC1.icc')))

# The exact unsigned Windows staged closure is recorded without exposing machine paths.
$stageFiles = @(Get-ChildItem -LiteralPath $windowsStageDirectory -Recurse -File)
$closureEvidenceFiles = @(
    $stageFiles |
        Where-Object { $_.Name -match '^(THIRD-PARTY-NOTICES|sbom|semantic-editor\.NOTICES|package(-lock)?\.json)' } |
        ForEach-Object { [System.IO.Path]::GetRelativePath($windowsStageDirectory, $_.FullName).Replace('\', '/') } |
        Sort-Object
)
$entries.Add((New-InventoryEntry -Kind 'windows-stage-closure' -Name 'unsigned Windows win-unpacked closure' -Version 'current local Release stage' `
    -Status 'legal-review' -LicenseEvidence "Deterministic aggregate SHA-256 $(Get-DirectoryAggregateHash $windowsStageDirectory) over $($stageFiles.Count) staged files; retained evidence files: $($closureEvidenceFiles -join ', ')." `
    -AttributionAction 'Compare the final Windows closure with the approved source license and third-party notice set before publishing.' `
    -Channels @('free-windows', 'store-windows') -Sources @('publish/win-x64/win-unpacked')))

# Account and publication prerequisites intentionally contain no credentials.
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'GitHub public-repository clearance' -Version 'not started' `
    -Status 'legal-review' -LicenseEvidence 'Public repository visibility requires the future M6 tracked-file and history audit.' `
    -AttributionAction 'Complete the documented M6 audit and resolve findings before changing repository visibility.' `
    -Channels $allChannels -Sources @('docs/research/m0.4-distribution-inventory.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'Microsoft Partner Center identity, product, price, and Store signing' -Version 'owner input required' `
    -Status 'unresolved-provenance' -LicenseEvidence 'No publisher identity, Store product reservation, pricing decision, or production signing material is in scope.' `
    -AttributionAction 'Owner supplies the account decisions only when M7 Store submission is authorized.' `
    -Channels @('store-windows') -Sources @('docs/v1-roadmap.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'Apple Developer identity, MAS profile, and real-device evidence' -Version 'owner input required' `
    -Status 'unresolved-provenance' -LicenseEvidence 'M0.6 remains account-gated; no Apple credentials, profile, or signed-device result exists.' `
    -AttributionAction 'Owner selects the signing host and supplies account/test-device access before M0.6 execution.' `
    -Channels @('direct-mac', 'mac-app-store') -Sources @('docs/v1-roadmap.md')))
$entries.Add((New-InventoryEntry -Kind 'distribution-prerequisite' -Name 'macOS dependency and artifact closure' -Version 'not measured' `
    -Status 'unresolved-provenance' -LicenseEvidence 'This inventory examines the Windows staged closure only; native macOS closure belongs to M0.6.' `
    -AttributionAction 'Generate and review the signed native macOS closure only after M0.6 is unblocked.' `
    -Channels @('direct-mac', 'mac-app-store') -Sources @('scripts/build-macos-release.ps1', 'docs/v1-roadmap.md')))

$orderedEntries = @($entries | Sort-Object kind, name, version)
$summary = [ordered]@{}
foreach ($status in @('satisfied', 'needs-inclusion', 'legal-review', 'unresolved-provenance'))
{
    $summary[$status] = @($orderedEntries | Where-Object { $_.status -eq $status }).Count
}
$inventory = [pscustomobject][ordered]@{
    formatVersion = 1
    scope = 'M0.4 local inventory only; no license adoption, publication, signing, Store submission, or history scan.'
    channels = @('direct-mac', 'free-windows', 'mac-app-store', 'store-windows')
    summary = [pscustomobject]$summary
    entries = $orderedEntries
}
$json = $inventory | ConvertTo-Json -Depth 8

$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine('# M0.4 local license and distribution inventory')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('Status: **Inventory only — no license has been adopted, no repository has been published, and no Store account or signing action has been taken.**')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('This deterministic report is generated from restored local dependency metadata, locked Press and semantic-editor inputs, shipped web/font assets, and the unsigned Windows `win-unpacked` closure. It contains no credentials, machine paths, author data, or network-derived content.')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Status summary')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Status | Entries | Meaning |')
[void]$markdown.AppendLine('|---|---:|---|')
[void]$markdown.AppendLine("| Satisfied | $($summary['satisfied']) | Current retained evidence is adequate for the stated item; it still remains in the final legal review. |")
[void]$markdown.AppendLine("| Needs inclusion | $($summary['needs-inclusion']) | Evidence exists, but the required distributed notice is not yet consolidated. |")
[void]$markdown.AppendLine("| Legal review | $($summary['legal-review']) | Source metadata exists but redistribution obligations require an owner-approved review. |")
[void]$markdown.AppendLine("| Unresolved provenance | $($summary['unresolved-provenance']) | Ownership, licensing, account, or native-closure evidence is absent. |")
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
[void]$markdown.AppendLine('| Kind | Name | Version | Status | License evidence | Required action | Channels | Sources |')
[void]$markdown.AppendLine('|---|---|---|---|---|---|---|---|')
foreach ($entry in $orderedEntries)
{
    [void]$markdown.AppendLine("| $(Get-QuotedMarkdownCell $entry.kind) | $(Get-QuotedMarkdownCell $entry.name) | $(Get-QuotedMarkdownCell $entry.version) | $(Get-QuotedMarkdownCell $entry.status) | $(Get-QuotedMarkdownCell $entry.licenseEvidence) | $(Get-QuotedMarkdownCell $entry.attributionAction) | $(Get-QuotedMarkdownCell ($entry.channels -join ', ')) | $(Get-QuotedMarkdownCell ($entry.sources -join ', ')) |")
}

[System.IO.Directory]::CreateDirectory($researchDirectory) | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($jsonOutputPath, $json + [Environment]::NewLine, $utf8)
[System.IO.File]::WriteAllText($markdownOutputPath, $markdown.ToString(), $utf8)
Write-Host "Generated $(Get-RepositoryRelativePath $jsonOutputPath) and $(Get-RepositoryRelativePath $markdownOutputPath)."
