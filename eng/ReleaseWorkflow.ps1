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
