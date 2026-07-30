param(
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$pressRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $pressRoot) "docs\research\press-spike-license-inventory.json"
}

Push-Location $pressRoot
try {
    $lockChecksums = @{}
    $lockName = $null
    $lockVersion = $null
    foreach ($line in Get-Content -LiteralPath (Join-Path $pressRoot "Cargo.lock")) {
        if ($line -eq "[[package]]") {
            $lockName = $null
            $lockVersion = $null
            continue
        }
        if ($line -match '^name = "([^"]+)"$') {
            $lockName = $Matches[1]
            continue
        }
        if ($line -match '^version = "([^"]+)"$') {
            $lockVersion = $Matches[1]
            continue
        }
        if ($line -match '^checksum = "([^"]+)"$' -and $lockName -and $lockVersion) {
            $lockChecksums["$lockName@$lockVersion"] = $Matches[1]
        }
    }

    $metadataText = & cargo metadata --locked --format-version 1
    if ($LASTEXITCODE -ne 0) {
        throw "cargo metadata failed with exit code $LASTEXITCODE."
    }
    $metadata = $metadataText | ConvertFrom-Json

    $packages = $metadata.packages |
        Where-Object { $_.name -ne "lorekeeper-press" } |
        Sort-Object name, version |
        ForEach-Object {
            $packageDirectory = Split-Path -Parent $_.manifest_path
            $licenseFiles = Get-ChildItem -LiteralPath $packageDirectory -File |
                Where-Object {
                    $_.Name -match "^(LICENSE|LICENCE|COPYING|NOTICE)(\.|$|-)"
                } |
                Sort-Object Name |
                ForEach-Object {
                    [pscustomobject]@{
                        name = $_.Name
                        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                }

            $vcsInfoPath = Join-Path $packageDirectory ".cargo_vcs_info.json"
            $vcsCommit = $null
            $vcsPath = $null
            if (Test-Path -LiteralPath $vcsInfoPath) {
                $vcsInfo = Get-Content -Raw -LiteralPath $vcsInfoPath | ConvertFrom-Json
                $vcsCommit = $vcsInfo.git.sha1
                $vcsPath = $vcsInfo.path_in_vcs
            }

            [pscustomobject]@{
                name = $_.name
                version = $_.version
                source = $_.source
                registryChecksum = $lockChecksums["$($_.name)@$($_.version)"]
                vcsCommit = $vcsCommit
                pathInVcs = $vcsPath
                licenseExpression = $_.license
                repository = $_.repository
                licenseFiles = @($licenseFiles)
            }
        }

    $directDependencyNames = $metadata.packages |
        Where-Object { $_.name -eq "lorekeeper-press" } |
        Select-Object -First 1 |
        ForEach-Object { $_.dependencies.name } |
        Sort-Object -Unique

    $inventory = [ordered]@{
        schemaVersion = 1
        package = "lorekeeper-press"
        packageVersion = "0.1.0"
        lockfile = "Lorekeeper.Press/Cargo.lock"
        dependencyCount = @($packages).Count
        directDependencies = @($directDependencyNames)
        packages = @($packages)
        shippedAssetNotes = @(
            "typst-assets embeds Libertinus Serif, New Computer Modern, and DejaVu Sans Mono font data.",
            "The typst-assets NOTICE file is included in this inventory and must be reproduced in the eventual binary distribution notices.",
            "No CMYK ICC profile is bundled by this spike."
        )
    }

    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    $json = $inventory | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText(
        $resolvedOutputPath,
        $json,
        [System.Text.UTF8Encoding]::new($false))
    Write-Output $resolvedOutputPath
}
finally {
    Pop-Location
}
