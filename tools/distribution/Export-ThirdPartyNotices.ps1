[CmdletBinding()]
param(
    [switch]$RefreshEvidence,
    [switch]$RefreshRuntimeEvidence,
    [switch]$CheckOnly,
    [string]$AssetsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (($RefreshEvidence -or $RefreshRuntimeEvidence) -and $CheckOnly) { throw 'Evidence refresh and CheckOnly cannot be combined.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidenceRoot = Join-Path $repoRoot 'licenses/third-party'
$manifestPath = Join-Path $evidenceRoot 'sources.json'
$noticePath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt'
$runtimeEvidenceRoot = Join-Path $repoRoot 'licenses/runtime-notices'
$runtimeManifestPath = Join-Path $runtimeEvidenceRoot 'sources.json'
$appImageEvidenceRoot = Join-Path $repoRoot 'licenses/appimage-runtime'
$appImageManifestPath = Join-Path $appImageEvidenceRoot 'sources.json'
if ([string]::IsNullOrWhiteSpace($AssetsPath)) { $AssetsPath = Join-Path $repoRoot 'Lorekeeper/obj/project.assets.json' }
$resolvedAssetsPath = [IO.Path]::GetFullPath($AssetsPath)
$assets = Get-Content -LiteralPath $resolvedAssetsPath -Raw | ConvertFrom-Json
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-RelativePath([string]$Path)
{
    return [IO.Path]::GetRelativePath($repoRoot, $Path).Replace('\', '/')
}

function Get-Hash([string]$Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Save-Evidence([string]$Source, [string]$Text, [string]$Name)
{
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
    $name = [IO.Path]::GetFileName($Name) -replace '[^a-zA-Z0-9._-]', '-'
    $path = Join-Path $evidenceRoot ($hash.Substring(0, 16) + '-' + $name)
    [IO.File]::WriteAllBytes($path, $bytes)
    return [pscustomobject][ordered]@{ path = Get-RelativePath $path; sha256 = $hash; source = $Source }
}

function Find-PackageDirectory([string]$RelativePath)
{
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name)
    {
        $path = Join-Path $folder $RelativePath
        if (Test-Path -LiteralPath $path -PathType Container) { return $path }
    }
    throw "Restored package unavailable: $RelativePath"
}

$packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | Sort-Object Name)
$runtimeDownloads = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
    Where-Object name -Match '^Microsoft\.(NETCore|AspNetCore|WindowsDesktop)\.App\.Runtime\.' | Sort-Object name -Unique)
if ($runtimeDownloads.Count -eq 0) { throw 'The restored assets contain no exact runtime-pack download identities.' }
if ($RefreshEvidence -or $RefreshRuntimeEvidence)
{
    [void][IO.Directory]::CreateDirectory($runtimeEvidenceRoot)
    $runtimeRecords = foreach ($download in $runtimeDownloads)
    {
        if ($download.version -notmatch '^\[([^,]+), \1\]$') { throw 'Runtime evidence requires an exact restored package version.' }
        $version = $Matches[1]
        $id = [string]$download.name
        $directory = Find-PackageDirectory ($id.ToLowerInvariant() + '/' + $version)
        [xml]$xml = Get-Content -LiteralPath (Join-Path $directory ($id.ToLowerInvariant() + '.nuspec')) -Raw
        $repository = $xml.package.metadata.SelectSingleNode("*[local-name()='repository']")
        $files = @(Get-ChildItem -LiteralPath $directory -File | Where-Object Name -Match '^(LICENSE|COPYING|NOTICE|THIRD.PARTY.NOTICES)' | Sort-Object Name)
        if (-not ($files | Where-Object Name -Match '^LICENSE')) { throw "Runtime pack lacks full license evidence: $id/$version" }
        $retained = foreach ($file in $files)
        {
            $destination = Join-Path $runtimeEvidenceRoot ($id + '/' + $version + '/' + $file.Name)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::WriteAllBytes($destination, [IO.File]::ReadAllBytes($file.FullName))
            [pscustomobject][ordered]@{ path = Get-RelativePath $destination; sha256 = Get-Hash $destination; source = "nuget:$id/$version/$($file.Name)" }
        }
        [pscustomobject][ordered]@{
            package = "$id/$version"; packageId = $id; version = $version
            runtimeIdentifier = $id.Substring($id.LastIndexOf('.Runtime.', [StringComparison]::Ordinal) + 9)
            sha512 = [IO.File]::ReadAllText((Join-Path $directory ($id.ToLowerInvariant() + '.' + $version + '.nupkg.sha512'))).Trim()
            repository = [string]$repository.GetAttribute('url'); repositoryCommit = [string]$repository.GetAttribute('commit')
            evidence = @($retained)
        }
    }
    # Debug and Release can select different current runtime patches. Replace
    # reviewed exact identities while retaining the other active configuration.
    $previousRecords = if (Test-Path -LiteralPath $runtimeManifestPath -PathType Leaf) { @((Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json).packages) } else { @() }
    $refreshedIdentities = @($runtimeRecords.package)
    $mergedRecords = @($previousRecords | Where-Object { $_.package -cnotin $refreshedIdentities }) + @($runtimeRecords)
    [IO.File]::WriteAllText($runtimeManifestPath, ([ordered]@{ formatVersion = 1; packages = @($mergedRecords | Sort-Object package) } | ConvertTo-Json -Depth 8) + "`n", $utf8)
}
if (-not (Test-Path -LiteralPath $runtimeManifestPath -PathType Leaf)) { throw 'Runtime notices are missing; refresh exact runtime evidence before packaging.' }
$runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json
foreach ($download in $runtimeDownloads)
{
    if ($download.version -notmatch '^\[([^,]+), \1\]$') { throw 'Runtime evidence requires an exact restored package version.' }
    $identity = "$($download.name)/$($Matches[1])"
    $records = @($runtimeManifest.packages | Where-Object package -CEQ $identity)
    if ($records.Count -ne 1) { throw "Retained runtime notice identity differs from produced assets: $identity" }
    $record = $records[0]
    $directory = Find-PackageDirectory ($record.packageId.ToLowerInvariant() + '/' + $record.version)
    $packageHash = [IO.File]::ReadAllText((Join-Path $directory ($record.packageId.ToLowerInvariant() + '.' + $record.version + '.nupkg.sha512'))).Trim()
    if ($packageHash -cne $record.sha512) { throw "Runtime pack content identity changed: $identity" }
}
foreach ($record in $runtimeManifest.packages)
{
    foreach ($evidence in $record.evidence)
    {
        $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $evidence.path))
        if (-not $path.StartsWith($runtimeEvidenceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Hash $path) -cne $evidence.sha256) { throw 'Retained runtime notice bytes are missing or changed.' }
    }
}
if (-not (Test-Path -LiteralPath $appImageManifestPath -PathType Leaf)) { throw 'Selected AppImage full notice evidence is missing.' }
$appImageManifest = Get-Content -LiteralPath $appImageManifestPath -Raw | ConvertFrom-Json
$expectedAppImageComponents = @('AppImage type2 runtime', 'libfuse', 'squashfuse', 'musl', 'zstd', 'zlib', 'mimalloc')
if (Compare-Object $expectedAppImageComponents @($appImageManifest.components.component)) { throw 'Selected AppImage component notice set is incomplete.' }
if (@($appImageManifest.components | Where-Object { $_.evidence.Count -eq 0 }).Count -ne 0) { throw 'An AppImage component lacks retained full terms.' }
$runtimeComponents = @($appImageManifest.components | Where-Object component -CEQ 'AppImage type2 runtime')
$libfuseComponents = @($appImageManifest.components | Where-Object component -CEQ 'libfuse')
if ($runtimeComponents.Count -ne 1 -or $libfuseComponents.Count -ne 1 -or
    [string]$runtimeComponents[0].version -cne [string]$appImageManifest.runtime.release -or
    [string]$libfuseComponents[0].version -cne [string]$appImageManifest.modifiedLibfuse.version)
{
    throw 'The selected AppImage runtime or modified-libfuse version differs from its retained component notices.'
}
$appImageEvidence = @($appImageManifest.components.evidence) + @($appImageManifest.modifiedLibfuse.patch, $appImageManifest.modifiedLibfuse.notice)
foreach ($evidence in $appImageEvidence)
{
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $evidence.path))
    if (-not $path.StartsWith($appImageEvidenceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Hash $path) -cne $evidence.sha256) { throw 'Retained AppImage full notice or patch bytes are missing or changed.' }
}
if ($RefreshEvidence)
{
    [void][IO.Directory]::CreateDirectory($evidenceRoot)
    $records = [Collections.Generic.List[object]]::new()
    $upstreamCache = @{}
    foreach ($package in $packages)
    {
        $directory = Find-PackageDirectory $package.Value.path
        $nuspec = Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' -File | Select-Object -First 1
        [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw
        $metadata = $xml.package.metadata
        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        $license = if ($null -eq $licenseNode) { '' } else { [string]$licenseNode.InnerText }
        $type = if ($null -eq $licenseNode) { '' } else { [string]$licenseNode.GetAttribute('type') }
        $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
        $repository = if ($null -eq $repositoryNode) { '' } else { [string]$repositoryNode.GetAttribute('url') }
        $commit = if ($null -eq $repositoryNode) { '' } else { [string]$repositoryNode.GetAttribute('commit') }
        $copyrightNode = $metadata.SelectSingleNode("*[local-name()='copyright']")
        $copyright = if ($null -eq $copyrightNode) { '' } else { [string]$copyrightNode.InnerText }
        $authorsNode = $metadata.SelectSingleNode("*[local-name()='authors']")
        $authors = if ($null -eq $authorsNode) { '' } else { [string]$authorsNode.InnerText }
        $retained = [Collections.Generic.List[object]]::new()
        $localEvidence = @(Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {
            $_.Name -match '^(LICENSE|COPYING|UNLICENSE|NOTICE)' -or $_.Name -match 'THIRD.?PARTY.?NOTICES' -or
            ($type -eq 'file' -and [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\', '/') -eq $license)
        } | Sort-Object FullName)
        foreach ($file in $localEvidence)
        {
            $relative = [IO.Path]::GetRelativePath($directory, $file.FullName).Replace('\', '/')
            $retained.Add((Save-Evidence "nuget:$($package.Name)/$relative" ([IO.File]::ReadAllText($file.FullName)) $file.Name))
        }
        if (-not ($localEvidence | Where-Object { $_.Name -match '^(LICENSE|COPYING|UNLICENSE)' -or ($type -eq 'file') }))
        {
            # Resolve missing package license files at the package's own repository
            # commit. Shared evidence is deduplicated by bytes, never by package name.
            $projectNode = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
            if ([string]::IsNullOrWhiteSpace($repository) -and $null -ne $projectNode) { $repository = [string]$projectNode.InnerText }
            if ($package.Name -like 'SkiaSharp/*') { $repository = 'https://github.com/mono/SkiaSharp' }
            if ($package.Name -like 'sqlite-vec/*') { $commit = 'v0.1.7-alpha.2' }
            if ($package.Name -like 'VersOne.Epub/*') { $commit = 'v3.3.6' }
            if ($package.Name -like 'SQLitePCLRaw.bundle_e_sqlite3/*') { $commit = 'v3.0.3' }
            if ($repository -notmatch '^https://github\.com/([^/]+/[^/#?]+)') { throw "No authoritative repository for license-less package $($package.Name)." }
            $githubRepo = $Matches[1].TrimEnd('/') -replace '\.git$', ''
            if ([string]::IsNullOrWhiteSpace($commit)) { throw "No exact upstream commit/tag for $($package.Name)." }
            $key = "$githubRepo/$commit"
            if (-not $upstreamCache.ContainsKey($key))
            {
                $evidence = $null
                foreach ($candidate in @('LICENSE', 'LICENSE.txt', 'LICENSE.TXT', 'LICENSE.md', 'License.txt', 'License.md', 'license.txt', 'LICENSE-MIT', 'LICENSE-MIT.txt', 'UNLICENSE'))
                {
                    $url = "https://raw.githubusercontent.com/$githubRepo/$commit/$candidate"
                    try
                    {
                        $response = Invoke-WebRequest -Uri $url
                        $evidence = Save-Evidence $url ([string]$response.Content) $candidate
                        break
                    }
                    catch
                    {
                        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
                    }
                }
                if ($null -eq $evidence) { throw "License file not found at exact source $key ($($package.Name))." }
                $upstreamCache[$key] = $evidence
            }
            $retained.Add($upstreamCache[$key])
        }
        $expression = if ($type -eq 'expression') { $license } elseif ($package.Name -like 'LibGit2Sharp.NativeBinaries/*') { 'LicenseRef-libgit2-unlimited-linking-exception' } elseif ($package.Name -like 'LibGit2Sharp/*') { 'MIT' } elseif ($package.Name -like 'SourceGear.sqlite3/*') { 'LicenseRef-SQLite-public-domain' } else { throw "Unreviewed file license: $($package.Name)" }
        $records.Add([pscustomobject][ordered]@{
            package = $package.Name; sha512 = [string]$package.Value.sha512; licenseExpression = $expression
            repository = $repository; repositoryCommit = $commit; copyright = $copyright; authors = $authors
            evidence = @($retained | Sort-Object path, source -Unique)
        })
    }
    $bootstrap = Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/twbs/bootstrap/v5.3.3/LICENSE'
    $bootstrapEvidence = Save-Evidence 'https://raw.githubusercontent.com/twbs/bootstrap/v5.3.3/LICENSE' ([string]$bootstrap.Content) 'bootstrap-LICENSE.txt'
    $otherActiveInternalAssets = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).packages | Where-Object { $_.package -like 'Microsoft.AspNetCore.App.Internal.Assets/*' -and $_.package -cnotin @($records.package) })
    } else { @() }
    $manifest = [pscustomobject][ordered]@{ formatVersion = 1; packages = @(@($records) + $otherActiveInternalAssets | Sort-Object package); webAssets = @([pscustomobject][ordered]@{ name = 'Bootstrap'; version = '5.3.3'; licenseExpression = 'MIT'; evidence = @($bootstrapEvidence) }) }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8) + "`n", $utf8)
}

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Retained sources.json is missing; restore and run -RefreshEvidence for reviewed package changes.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$expectedPackages = @($packages.Name)
$actualPackages = @($manifest.packages.package)
if (Compare-Object @($expectedPackages | Where-Object { $_ -notlike 'Microsoft.AspNetCore.App.Internal.Assets/*' }) @($actualPackages | Where-Object { $_ -notlike 'Microsoft.AspNetCore.App.Internal.Assets/*' })) { throw 'Retained notice package set differs from the restored dependency closure; refresh and review evidence.' }
$activeAspNetVersions = @($runtimeManifest.packages | Where-Object packageId -Like 'Microsoft.AspNetCore.App.Runtime.*' | Select-Object -ExpandProperty version -Unique)
foreach ($identity in $actualPackages | Where-Object { $_ -like 'Microsoft.AspNetCore.App.Internal.Assets/*' })
{
    if ($identity.Split('/')[1] -cnotin $activeAspNetVersions) { throw 'Internal ASP.NET assets evidence does not match a current retained runtime configuration.' }
}
foreach ($package in $packages)
{
    $record = @($manifest.packages | Where-Object { $_.package -eq $package.Name })
    if ($record.Count -ne 1 -or $record[0].sha512 -ne [string]$package.Value.sha512) { throw "Exact package content identity changed: $($package.Name)." }
}
foreach ($entry in @($manifest.packages) + @($manifest.webAssets))
{
    if (@($entry.evidence).Count -eq 0) { throw 'An entry has no retained license evidence.' }
    foreach ($evidence in $entry.evidence)
    {
        $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $evidence.path))
        if (-not $path.StartsWith($evidenceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence path escapes the retained license directory.' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Hash $path) -ne $evidence.sha256) { throw "Retained license bytes changed: $($evidence.path)." }
    }
}

$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine('Lorekeeper third-party notices')
[void]$builder.AppendLine('Generated by tools/distribution/Export-ThirdPartyNotices.ps1 from exact restored package identities and retained upstream license files.')
[void]$builder.AppendLine('Third-party rights remain independent of the first-party alternatives in LICENSE.')
[void]$builder.AppendLine()
[void]$builder.AppendLine('Managed and native NuGet dependencies (includes build-time inputs conservatively):')
foreach ($record in $manifest.packages)
{
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("$($record.package) — $($record.licenseExpression)")
    if (-not [string]::IsNullOrWhiteSpace($record.copyright)) { [void]$builder.AppendLine($record.copyright) }
    [void]$builder.AppendLine("Authors: $($record.authors)")
    [void]$builder.AppendLine("Source: $($record.repository) $($record.repositoryCommit)")
    foreach ($evidence in $record.evidence) { [void]$builder.AppendLine("Retained full terms/notice: $($evidence.path) (SHA-256 $($evidence.sha256)); $($evidence.source)") }
}
[void]$builder.AppendLine()
[void]$builder.AppendLine('libgit2 distribution scope: the unmodified compiled library is linked into Lorekeeper. Its retained GPLv2 text contains an explicit unlimited linking exception permitting this combined distribution. This decision does not admit library modifications or standalone unlinked redistribution under a permissive license.')
foreach ($record in $manifest.webAssets)
{
    [void]$builder.AppendLine()
    [void]$builder.AppendLine("$($record.name) $($record.version) — $($record.licenseExpression)")
    foreach ($evidence in $record.evidence) { [void]$builder.AppendLine("Retained full terms: $($evidence.path) (SHA-256 $($evidence.sha256)); $($evidence.source)") }
}
[void]$builder.AppendLine()
[void]$builder.AppendLine('Other retained distribution notices:')
[void]$builder.AppendLine('- vis-network: wwwroot/lib/vis-network/LICENSE-APACHE-2.0 and LICENSE-MIT (source checkout: Lorekeeper/wwwroot/lib/vis-network/).')
[void]$builder.AppendLine('- ProseMirror and its runtime dependencies: wwwroot/js/semantic-editor.NOTICES.txt; source lock and full notices in tools/semantic-editor/package-lock.json and THIRD_PARTY_NOTICES.md.')
[void]$builder.AppendLine('- Bundled fonts: wwwroot/fonts/SOURCES.md and every font family OFL.txt; unchanged licensed faces also occur in press-runtime/fonts/.')
[void]$builder.AppendLine('- Lorekeeper Press: press-runtime/THIRD-PARTY-NOTICES.txt and sbom.json contain the complete locked Cargo and embedded asset notices. Its integrity manifest governs that separate contained runtime.')
[void]$builder.AppendLine('- Electron and Chromium: keep the platform runtime LICENSE and LICENSES.chromium.html in every Electron distribution; the generated Electron dependency closure is checked during release packaging.')
[void]$builder.AppendLine('- Self-contained .NET and ASP.NET Core runtimes: exact SDK runtime-pack full licenses and notices are retained under licenses/runtime-notices/ with source-package hashes in sources.json; these distinct paths avoid first-party root filename collisions. Packaging validates includedFrameworks against this retained evidence.')
foreach ($record in $runtimeManifest.packages)
{
    [void]$builder.AppendLine("  $($record.package); source $($record.repository) $($record.repositoryCommit)")
    foreach ($evidence in $record.evidence) { [void]$builder.AppendLine("  Retained full terms/notice: $($evidence.path) (SHA-256 $($evidence.sha256))") }
}
[void]$builder.AppendLine('- ICC profile: Lorekeeper.Press/assets/profiles/SOURCE.md records the registry source, hash, and unchanged distribution requirement; Press notices repeat the terms.')
[void]$builder.AppendLine("- AppImage type2 runtime $($appImageManifest.runtime.release): exact component full terms and modified-libfuse notice/patch metadata are retained under licenses/appimage-runtime/sources.json. Full notice inclusion is prepared; LGPL corresponding-source, dependency provenance and recipient relink clearance remain pending before public AppImage distribution.")
foreach ($component in $appImageManifest.components)
{
    [void]$builder.AppendLine("  $($component.component) $($component.version); $($component.license)")
    foreach ($evidence in $component.evidence) { [void]$builder.AppendLine("  Retained full terms/notice: $($evidence.path) (SHA-256 $($evidence.sha256))") }
}
[void]$builder.AppendLine("  Modified-libfuse notice: $($appImageManifest.modifiedLibfuse.notice.path); original supplier patch/date and exact runtime hash are recorded in sources.json.")
[void]$builder.AppendLine('- First-party branding provenance: wwwroot/branding/SOURCES.md (source checkout: Lorekeeper/wwwroot/branding/).')
$content = $builder.ToString().Replace("`r`n", "`n")
if ($CheckOnly)
{
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or [IO.File]::ReadAllText($noticePath).Replace("`r`n", "`n") -cne $content) { throw 'THIRD-PARTY-NOTICES.txt is stale; regenerate and review it.' }
    Write-Host "Verified $($manifest.packages.Count) exact NuGet packages and retained notice bytes."
}
else
{
    [IO.File]::WriteAllText($noticePath, $content, $utf8)
    Write-Host "Generated THIRD-PARTY-NOTICES.txt for $($manifest.packages.Count) exact NuGet packages."
}
