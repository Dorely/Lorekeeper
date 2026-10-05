function Invoke-ReleaseCommand
{
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor Cyan
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

function Get-LorekeeperVersion
{
    param([Parameter(Mandatory)][string]$ProjectPath)

    $projectText = [System.IO.File]::ReadAllText($ProjectPath)
    $versionMatches = [regex]::Matches($projectText, '<Version>(?<version>[^<]+)</Version>')
    if ($versionMatches.Count -ne 1)
    {
        throw "Expected exactly one explicit <Version> in $ProjectPath."
    }
    return $versionMatches[0].Groups['version'].Value
}

function Get-LorekeeperReleaseRepositories
{
    param([Parameter(Mandatory)][string]$Version)

    # v1.0.0 is the final bridge for users of the old download repository.
    if ($Version -ceq '1.0.0')
    {
        return @('Dorely/Lorekeeper', 'Dorely/Lorekeeper-Releases')
    }
    return @('Dorely/Lorekeeper')
}

function Assert-AppImagePublicationReady
{
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [switch]$CheckOnly,
        [string]$SourceArchivePath,
        $ArtifactProvenance
    )

    $clearance = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'eng/appimage-publication.json') -Raw | ConvertFrom-Json -AsHashtable
    $noticePath = Join-Path $RepositoryRoot 'licenses/appimage-runtime/sources.json'
    $notice = Get-Content -LiteralPath $noticePath -Raw | ConvertFrom-Json -AsHashtable
    if ($clearance.formatVersion -ne 1 -or $clearance.status -cnotin @('blocked', 'approved') -or
        $clearance.runtimeRelease -cne $notice.runtime.release -or
        $clearance.runtimeSourceCommit -cne $notice.runtime.commit -or
        $clearance.runtimeSha256 -cne $notice.runtime.sha256 -or
        $clearance.toolsetVersion -cne $notice.toolset.version -or
        $clearance.toolsetArchiveSha256 -cne $notice.toolset.sha256 -or
        $clearance.noticeManifestSha256 -cne (Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash.ToLowerInvariant())
    {
        throw 'AppImage publication clearance is malformed or does not match the selected runtime, toolset, source commit, and notice manifest.'
    }
    if ($clearance.status -ceq 'blocked')
    {
        $message = 'AppImage publication is blocked: review and accept corresponding-source materials, exact dependency provenance, and recipient modified-library relink/repack evidence in eng/appimage-publication.json. Local Linux builds remain available.'
        if ($CheckOnly)
        {
            Write-Warning $message
            return
        }
        throw $message
    }

    foreach ($field in @('sourceArchiveSha256', 'sourceManifestSha256', 'relinkLogSha256', 'repackLogSha256', 'relinkedRuntimeSha256', 'repackedAppImageSha256'))
    {
        if ([string]$clearance[$field] -cnotmatch '^[a-f0-9]{64}$')
        {
            throw "Approved AppImage publication requires the reviewed $field."
        }
    }
    $archiveName = "Lorekeeper-AppImage-Runtime-Sources-$($notice.runtime.release)-x86_64.tar.gz"
    if ($clearance.sourceArchiveFileName -cne $archiveName)
    {
        throw 'The AppImage corresponding-source filename does not match the selected runtime release.'
    }
    if ([string]::IsNullOrWhiteSpace($SourceArchivePath))
    {
        $SourceArchivePath = Join-Path $RepositoryRoot ".artifacts/appimage-publication/$archiveName"
    }
    $SourceArchivePath = [IO.Path]::GetFullPath($SourceArchivePath)
    if (-not (Test-Path -LiteralPath $SourceArchivePath -PathType Leaf) -or
        [IO.Path]::GetFileName($SourceArchivePath) -cne $archiveName -or
        (Get-FileHash -LiteralPath $SourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $clearance.sourceArchiveSha256)
    {
        throw "The reviewed AppImage corresponding-source archive is missing or differs from its approved SHA-256: $SourceArchivePath"
    }

    # Read only the reviewed members in place; never extract supplier archive paths.
    $requiredMembers = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $requiredMembers.Add('sources.json', [string]$clearance.sourceManifestSha256)
    $requiredMembers.Add('recipient-validation/relink.log', [string]$clearance.relinkLogSha256)
    $requiredMembers.Add('recipient-validation/repack.log', [string]$clearance.repackLogSha256)
    $seenMembers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    Add-Type -AssemblyName System.Formats.Tar
    $fileStream = [IO.File]::OpenRead($SourceArchivePath)
    try
    {
        $gzipStream = [IO.Compression.GZipStream]::new($fileStream, [IO.Compression.CompressionMode]::Decompress, $true)
        try
        {
            $tarReader = [System.Formats.Tar.TarReader]::new($gzipStream, $true)
            try
            {
                while ($null -ne ($entry = $tarReader.GetNextEntry()))
                {
                    $memberName = if ($entry.Name.StartsWith('./', [StringComparison]::Ordinal)) { $entry.Name.Substring(2) } else { $entry.Name }
                    if (-not $requiredMembers.ContainsKey($memberName)) { continue }
                    if (-not $seenMembers.Add($memberName) -or $entry.Length -le 0 -or
                        $entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile))
                    {
                        throw "The source archive has duplicate, empty, or non-file publication evidence: $memberName"
                    }
                    $sha256 = [Security.Cryptography.SHA256]::Create()
                    try
                    {
                        if ($memberName -ceq 'sources.json')
                        {
                            if ($entry.Length -gt 1048576) { throw 'The AppImage source manifest exceeds the supported one MiB metadata limit.' }
                            $sourceManifestBytes = [byte[]]::new([int]$entry.Length)
                            $entry.DataStream.ReadExactly($sourceManifestBytes, 0, $sourceManifestBytes.Length)
                            $memberHash = [Convert]::ToHexString($sha256.ComputeHash($sourceManifestBytes)).ToLowerInvariant()
                        }
                        else { $memberHash = [Convert]::ToHexString($sha256.ComputeHash($entry.DataStream)).ToLowerInvariant() }
                    }
                    finally { $sha256.Dispose() }
                    if ($memberHash -cne $requiredMembers[$memberName])
                    {
                        throw "The source archive evidence differs from its reviewed SHA-256: $memberName"
                    }
                    if ($memberName -ceq 'sources.json')
                    {
                        $sourceManifest = [Text.UTF8Encoding]::new($false, $true).GetString($sourceManifestBytes) | ConvertFrom-Json -AsHashtable
                        if ($sourceManifest -isnot [Collections.IDictionary] -or $sourceManifest['formatVersion'] -ne 1 -or
                            $sourceManifest['runtime'] -isnot [Collections.IDictionary] -or
                            $sourceManifest['toolset'] -isnot [Collections.IDictionary] -or
                            $sourceManifest['modifiedLibfuse'] -isnot [Collections.IDictionary] -or
                            $sourceManifest['modifiedLibfuse']['patch'] -isnot [Collections.IDictionary] -or
                            [string]$sourceManifest['runtime']['release'] -cne [string]$notice.runtime.release -or
                            [string]$sourceManifest['runtime']['commit'] -cne [string]$notice.runtime.commit -or
                            [string]$sourceManifest['runtime']['sha256'] -cne [string]$notice.runtime.sha256 -or
                            [string]$sourceManifest['toolset']['version'] -cne [string]$notice.toolset.version -or
                            [string]$sourceManifest['toolset']['sha256'] -cne [string]$notice.toolset.sha256 -or
                            [string]$sourceManifest['modifiedLibfuse']['version'] -cne [string]$notice.modifiedLibfuse.version -or
                            [string]$sourceManifest['modifiedLibfuse']['patch']['sha256'] -cne [string]$notice.modifiedLibfuse.patch.sha256)
                        {
                            throw 'The reviewed AppImage source manifest differs from the selected runtime, toolset, or modified-libfuse version/patch identity.'
                        }
                    }
                }
            }
            finally { $tarReader.Dispose() }
        }
        finally { $gzipStream.Dispose() }
    }
    finally { $fileStream.Dispose() }
    if ($seenMembers.Count -ne $requiredMembers.Count)
    {
        throw 'The AppImage source archive must contain the reviewed sources.json, recipient-validation/relink.log, and recipient-validation/repack.log.'
    }
    if ($null -ne $ArtifactProvenance)
    {
        $toolset = $ArtifactProvenance.appImageToolset
        if ([string]$toolset.version -cne ([string]$notice.toolset.version).Replace('appimage@', '') -or
            [string]$toolset.archiveSha256 -cne $clearance.toolsetArchiveSha256 -or
            [string]$toolset.runtimeRelease -cne $clearance.runtimeRelease -or
            [string]$toolset.runtimeSourceCommit -cne $clearance.runtimeSourceCommit -or
            [string]$toolset.runtimeSha256 -cne $clearance.runtimeSha256 -or
            [string]$toolset.noticeManifestSha256 -cne $clearance.noticeManifestSha256)
        {
            throw 'The packaged AppImage runtime provenance differs from its approved source/relink clearance.'
        }
    }
    Write-Host 'AppImage publication clearance and corresponding-source/evidence hashes passed.'
    return $SourceArchivePath
}

function Assert-ReleaseNoticeClosure
{
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$ManagedRoot,
        [Parameter(Mandatory)][string]$DesktopRoot
    )

    $licenseRoot = Join-Path $RepositoryRoot 'licenses'
    if (-not (Test-Path -LiteralPath $licenseRoot -PathType Container))
    {
        throw 'The source distribution license directory is missing.'
    }
    $sourceFiles = @('LICENSE', 'THIRD-PARTY-NOTICES.txt') + @(
        Get-ChildItem -LiteralPath $licenseRoot -Recurse -File |
            ForEach-Object { [System.IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName) }
    )
    foreach ($relativePath in $sourceFiles)
    {
        $sourcePath = Join-Path $RepositoryRoot $relativePath
        $packagedPath = Join-Path $ManagedRoot $relativePath
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $packagedPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $packagedPath -Algorithm SHA256).Hash)
        {
            throw "The packaged license/notice does not match its source: $relativePath"
        }
    }
    $runtimeManifest = Get-Content -LiteralPath (Join-Path $licenseRoot 'runtime-notices/sources.json') -Raw | ConvertFrom-Json
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $ManagedRoot 'Lorekeeper.runtimeconfig.json') -Raw | ConvertFrom-Json
    $dependencies = Get-Content -LiteralPath (Join-Path $ManagedRoot 'Lorekeeper.deps.json') -Raw | ConvertFrom-Json
    $runtimeIdentifier = ([string]$dependencies.runtimeTarget.name).Split('/')[-1]
    $frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    if ($runtimeIdentifier -cnotin @('win-x64', 'linux-x64', 'osx-arm64') -or
        @($frameworks | Where-Object name -CEQ 'Microsoft.NETCore.App').Count -ne 1 -or
        @($frameworks | Where-Object name -CEQ 'Microsoft.AspNetCore.App').Count -ne 1)
    {
        throw 'The packaged self-contained runtime identity is incomplete or unsupported.'
    }
    foreach ($framework in $frameworks)
    {
        $identity = "$($framework.name).Runtime.$runtimeIdentifier/$($framework.version)"
        $records = @($runtimeManifest.packages | Where-Object package -CEQ $identity)
        if ($records.Count -ne 1 -or @($records[0].evidence).Count -eq 0)
        {
            throw "The packaged runtime has no exact retained license/notice evidence: $identity"
        }
    }
    foreach ($relativePath in @('wwwroot/js/semantic-editor.NOTICES.txt', 'press-runtime/THIRD-PARTY-NOTICES.txt', 'press-runtime/sbom.json'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $ManagedRoot $relativePath) -PathType Leaf))
        {
            throw "The packaged application is missing dependency evidence: $relativePath"
        }
    }
    # Electron's Chromium notice can live inside the framework on macOS.
    foreach ($noticeName in @('LICENSES.chromium.html'))
    {
        if (@(Get-ChildItem -LiteralPath $DesktopRoot -Recurse -File -Filter $noticeName).Count -eq 0)
        {
            throw "The Electron closure is missing $noticeName."
        }
    }
}

function Invoke-ReleasePreflight
{
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $previousArtifactsPath = $env:ArtifactsPath
    Push-Location $RepositoryRoot
    try
    {
        $env:ArtifactsPath = Join-Path $RepositoryRoot '.artifacts/release-preflight'
        Invoke-ReleaseCommand dotnet @('build', 'Lorekeeper.sln')
        Invoke-ReleaseCommand dotnet @('test', 'Lorekeeper.Tests/Lorekeeper.Tests.csproj')
        & (Join-Path $RepositoryRoot 'tools/distribution/Export-ThirdPartyNotices.ps1') -CheckOnly `
            -AssetsPath (Join-Path $env:ArtifactsPath 'obj/Lorekeeper/project.assets.json')
        if ($null -eq $previousArtifactsPath) { Remove-Item Env:ArtifactsPath }
        else { $env:ArtifactsPath = $previousArtifactsPath }
        Push-Location (Join-Path $RepositoryRoot 'Lorekeeper.Press')
        try
        {
            Invoke-ReleaseCommand cargo @('test', '--locked')
        }
        finally { Pop-Location }
    }
    finally
    {
        if ($null -eq $previousArtifactsPath) { Remove-Item Env:ArtifactsPath -ErrorAction SilentlyContinue }
        else { $env:ArtifactsPath = $previousArtifactsPath }
        Pop-Location
    }
}

function Assert-GitHubApiResourceMissing
{
    param(
        [Parameter(Mandatory)][string]$ApiPath,
        [Parameter(Mandatory)][string]$ExistingMessage,
        [Parameter(Mandatory)][string]$LookupFailureMessage
    )

    $lookupOutputPath = [System.IO.Path]::GetTempFileName()
    $lookupErrorPath = [System.IO.Path]::GetTempFileName()
    try
    {
        $lookupProcess = Start-Process -FilePath (Get-Command gh).Source -ArgumentList @(
            'api', '--include', $ApiPath
        ) -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $lookupOutputPath `
            -RedirectStandardError $lookupErrorPath
        $lookupExitCode = $lookupProcess.ExitCode
        $lookupOutput = @([System.IO.File]::ReadAllLines($lookupOutputPath))
        $lookupError = [System.IO.File]::ReadAllText($lookupErrorPath).Trim()
    }
    finally
    {
        Remove-Item -LiteralPath $lookupOutputPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $lookupErrorPath -Force -ErrorAction SilentlyContinue
    }

    if ($lookupExitCode -eq 0)
    {
        throw $ExistingMessage
    }

    $statusLine = if ($lookupOutput.Count -gt 0) { [string]$lookupOutput[0] } else { '' }
    if (($statusLine -notmatch '^HTTP/\S+ 404 ') -and ($lookupError -notmatch '\(HTTP 404\)'))
    {
        throw "$LookupFailureMessage GitHub returned: $statusLine $lookupError"
    }
}

function Assert-ReleaseTagUnused
{
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag
    )

    Assert-GitHubApiResourceMissing `
        -ApiPath "repos/$Repository/releases/tags/$Tag" `
        -ExistingMessage "Release $Tag already exists in $Repository. Release versions are immutable; choose a newer version." `
        -LookupFailureMessage "Could not confirm that release $Tag is unused in $Repository."
    Assert-GitHubApiResourceMissing `
        -ApiPath "repos/$Repository/git/ref/tags/$Tag" `
        -ExistingMessage "Tag $Tag already exists in $Repository without a matching release. Refusing to reuse it; choose a newer version." `
        -LookupFailureMessage "Could not confirm that tag $Tag is unused in $Repository."
}
