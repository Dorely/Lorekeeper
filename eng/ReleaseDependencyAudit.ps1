Set-StrictMode -Version Latest

function Test-DormantElectronSplashImageSizeAdvisory
{
    param(
        [Parameter(Mandatory)][string]$DependencyName,
        [Parameter(Mandatory)][object]$Finding,
        [Parameter(Mandatory)][string]$StageDirectory
    )

    if ($DependencyName -ne 'image-size' -or -not $Finding.isDirect)
    {
        return $false
    }

    $expectedAdvisoryUrls = @(
        'https://github.com/advisories/GHSA-5p2g-fcmc-qvqq',
        'https://github.com/advisories/GHSA-w3rx-r6r6-pgpr'
    )
    $advisories = @($Finding.via)
    if ($advisories.Count -ne $expectedAdvisoryUrls.Count -or
        @($advisories | Where-Object { $_ -is [string] }).Count -gt 0)
    {
        return $false
    }

    $actualAdvisoryUrls = @($advisories | ForEach-Object { [string]$_.url } | Sort-Object)
    if (@(Compare-Object -ReferenceObject $expectedAdvisoryUrls -DifferenceObject $actualAdvisoryUrls).Count -ne 0)
    {
        return $false
    }

    $nodes = @($Finding.nodes)
    if ($nodes.Count -ne 1 -or $nodes[0] -ne 'node_modules/image-size' -or @($Finding.effects).Count -ne 0)
    {
        return $false
    }

    $manifestPath = Join-Path $StageDirectory 'package.json'
    $mainPath = Join-Path $StageDirectory 'main.js'
    $dependencyManifestPath = Join-Path $StageDirectory 'node_modules/image-size/package.json'
    foreach ($requiredPath in @($manifestPath, $mainPath, $dependencyManifestPath))
    {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf))
        {
            return $false
        }
    }

    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    $dependencyManifest = Get-Content -Raw -LiteralPath $dependencyManifestPath | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace([string]$manifest.splashscreen.imageFile) -or
        [string]$dependencyManifest.version -ne '1.2.1')
    {
        return $false
    }

    $mainText = [System.IO.File]::ReadAllText($mainPath)
    $requirePattern = [regex]::Escape("const { imageSize } = require('image-size');")
    if ([regex]::Matches($mainText, $requirePattern).Count -ne 1 -or
        [regex]::Matches($mainText, '\bimageSize\s*\(').Count -ne 1)
    {
        return $false
    }

    Write-Warning 'Accepted the two known image-size 1.2.1 parser advisories because the generated Electron host has no splash image and its only imageSize call is confined to that unreachable splash path. Any changed package, call site, configuration, or advisory fails closed.'
    return $true
}

function Get-ReleaseProductionAuditBlockingFindings
{
    param(
        [Parameter(Mandatory)][object]$Audit,
        [Parameter(Mandatory)][hashtable]$SeverityRank,
        [Parameter(Mandatory)][string]$StageDirectory
    )

    @(
        $Audit.vulnerabilities.PSObject.Properties |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Finding = $_.Value } } |
            Where-Object {
                $SeverityRank[$_.Finding.severity] -ge $SeverityRank.high -and
                -not (Test-DormantElectronSplashImageSizeAdvisory `
                    -DependencyName $_.Name `
                    -Finding $_.Finding `
                    -StageDirectory $StageDirectory)
            }
    )
}
