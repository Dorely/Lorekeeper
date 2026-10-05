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
