param(
    [Parameter(Mandatory = $true)]
    [string]$PythonPath,

    [Parameter(Mandatory = $true)]
    [string]$NativeArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$FontRegularPath,

    [Parameter(Mandatory = $true)]
    [string]$FontBoldPath,

    [Parameter(Mandatory = $true)]
    [string]$FontLicensePath,

    [string]$CmykProfilePath,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\dist")
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$python = (Resolve-Path $PythonPath).Path
$nativeArchive = (Resolve-Path $NativeArchivePath).Path
$fontRegular = (Resolve-Path $FontRegularPath).Path
$fontBold = (Resolve-Path $FontBoldPath).Path
$fontLicense = (Resolve-Path $FontLicensePath).Path
$fontConfig = (Resolve-Path (Join-Path $projectRoot "assets\fonts.conf")).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) {
    if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) {
        throw "The press build output directory must be empty so stale or unreviewed payloads cannot enter the bundle evidence: $output"
    }
}
$temporaryRoot = Join-Path $projectRoot ".tmp"
$pythonVersion = (& $python -c "import platform; print(platform.python_version())").Trim()
if ($LASTEXITCODE -ne 0 -or $pythonVersion -notmatch "^3\.(13|14)\.\d+$") {
    throw "The press spike requires an explicit Python 3.13 or 3.14 interpreter."
}
$environmentPath = Join-Path $temporaryRoot "build-venv-$($pythonVersion.Replace('.', '-'))"
$workPath = Join-Path $temporaryRoot "pyinstaller-$($pythonVersion.Replace('.', '-'))"
$specPath = Join-Path $temporaryRoot "spec-$($pythonVersion.Replace('.', '-'))"
$binaryInventoryPath = Join-Path $output "lorekeeper-press-weasy-binaries.json"
$buildEvidencePath = Join-Path $output "lorekeeper-press-weasy-build.json"
$distributionFontDirectory = Join-Path $output "fonts"
$distributionLicenseDirectory = Join-Path $output "licenses"
$nativeArchiveDirectory = Join-Path $temporaryRoot "verified-native-archive"
$nativeDirectory = Join-Path $temporaryRoot "verified-native-binaries"
$nativeManifestPath = Join-Path $output "lorekeeper-press-weasy-native-source.json"
$distributionProfileDirectory = Join-Path $output "profiles"

$expectedNativeArchiveHash = "330101FF3EA50EBDE4ABF805283B6D703D5F3D71C77C983DB94357EC4524A3EF"
if ((Get-FileHash $nativeArchive -Algorithm SHA256).Hash -ne $expectedNativeArchiveHash) {
    throw "The WeasyPrint 69 portable native archive fingerprint does not match."
}

$expectedFontHashes = @{
    $fontRegular = "A82E5F4350D6B66D44D1C77F718D7C658A23AE6097C115748F5AB58C0723AF59"
    $fontBold = "1226CECB60336D5AB5401C8ECB608CD93FE2576DF85EC1F2410EAC524878323C"
    $fontLicense = "93FED46019C38BBE566B479D22148E2E8A1E85ADA614ACCB0211C37B2C61C19B"
}
foreach ($path in $expectedFontHashes.Keys) {
    $actualHash = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedFontHashes[$path]) {
        throw "Pinned font input hash mismatch: $path"
    }
}

New-Item -ItemType Directory -Force -Path $temporaryRoot, $output, $workPath, $specPath | Out-Null
New-Item -ItemType Directory -Force -Path $distributionFontDirectory, $distributionLicenseDirectory | Out-Null
[System.IO.File]::Copy($fontConfig, (Join-Path $distributionFontDirectory "fonts.conf"), $true)
[System.IO.File]::Copy($fontRegular, (Join-Path $distributionFontDirectory "LiberationSerif-Regular.ttf"), $true)
[System.IO.File]::Copy($fontBold, (Join-Path $distributionFontDirectory "LiberationSerif-Bold.ttf"), $true)
[System.IO.File]::Copy($fontLicense, (Join-Path $distributionLicenseDirectory "Liberation-Fonts-LICENSE.txt"), $true)
if (-not [string]::IsNullOrWhiteSpace($CmykProfilePath)) {
    $profile = (Resolve-Path $CmykProfilePath).Path
    New-Item -ItemType Directory -Force -Path $distributionProfileDirectory | Out-Null
    [System.IO.File]::Copy($profile, (Join-Path $distributionProfileDirectory "printing2009.icc"), $true)
}
if (-not (Test-Path (Join-Path $environmentPath "Scripts\python.exe"))) {
    & uv venv --python $python $environmentPath
    if ($LASTEXITCODE -ne 0) {
        throw "The build virtual environment could not be created."
    }
}
$environmentPython = Join-Path $environmentPath "Scripts\python.exe"
$environmentVersion = (& $environmentPython -c "import platform; print(platform.python_version())").Trim()
if ($environmentVersion -ne $pythonVersion) {
    throw "The cached build environment uses Python $environmentVersion, expected $pythonVersion."
}

$previousVirtualEnvironment = $env:VIRTUAL_ENV
$previousPath = $env:PATH
$previousWeasyDllDirectories = $env:WEASYPRINT_DLL_DIRECTORIES
$previousFontConfigFile = $env:FONTCONFIG_FILE
try {
    $env:VIRTUAL_ENV = $environmentPath
    & uv sync --project $projectRoot --locked --group dev --active
    if ($LASTEXITCODE -ne 0) {
        throw "Locked Python dependency installation failed."
    }

    foreach ($path in @($nativeArchiveDirectory, $nativeDirectory)) {
        $fullPath = [System.IO.Path]::GetFullPath($path)
        if (-not $fullPath.StartsWith("$temporaryRoot\", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Native extraction path escaped the project temporary directory: $fullPath"
        }
        if (Test-Path $fullPath) {
            Remove-Item -LiteralPath $fullPath -Recurse -Force
        }
        New-Item -ItemType Directory -Path $fullPath | Out-Null
    }
    Expand-Archive -LiteralPath $nativeArchive -DestinationPath $nativeArchiveDirectory
    $portableExecutable = Join-Path $nativeArchiveDirectory "dist\weasyprint.exe"
    if (-not (Test-Path $portableExecutable)) {
        throw "The verified archive did not contain dist/weasyprint.exe."
    }
    & $environmentPython `
        (Join-Path $PSScriptRoot "extract-pyinstaller-native.py") `
        --executable $portableExecutable `
        --output $nativeDirectory `
        --manifest $nativeManifestPath
    if ($LASTEXITCODE -ne 0) {
        throw "The verified upstream native payload could not be extracted."
    }

    $env:PATH = "$nativeDirectory;$env:SystemRoot\System32;$env:SystemRoot"
    $env:WEASYPRINT_DLL_DIRECTORIES = $nativeDirectory
    $env:FONTCONFIG_FILE = $fontConfig
    $pyinstaller = Join-Path $environmentPath "Scripts\pyinstaller.exe"
    & $pyinstaller `
        --noconfirm `
        --clean `
        --onefile `
        --name lorekeeper-press-weasy `
        --distpath $output `
        --workpath $workPath `
        --specpath $specPath `
        (Join-Path $projectRoot "launcher.py")
    if ($LASTEXITCODE -ne 0) {
        throw "The frozen renderer build failed."
    }

    & $python `
        (Join-Path $PSScriptRoot "audit-pyinstaller-binaries.py") `
        --toc (Join-Path $workPath "lorekeeper-press-weasy\Analysis-00.toc") `
        --output $binaryInventoryPath `
        --allow-root "native=$nativeDirectory" `
        --allow-root "python=$(Split-Path $python -Parent)" `
        --allow-root "environment=$environmentPath" `
        --allow-root "system=$(Join-Path $env:SystemRoot 'System32')"
    if ($LASTEXITCODE -ne 0) {
        throw "The frozen binary-source audit failed."
    }

    $sourcePaths = @(
        (Join-Path $projectRoot "launcher.py"),
        (Join-Path $projectRoot "pyproject.toml"),
        (Join-Path $projectRoot "uv.lock")
    )
    $sourcePaths += Get-ChildItem `
        (Join-Path $projectRoot "src"), `
        (Join-Path $projectRoot "scripts"), `
        (Join-Path $projectRoot "assets") `
        -Recurse `
        -File |
        Where-Object { $_.FullName -notmatch "\\__pycache__\\" } |
        Select-Object -ExpandProperty FullName
    $sourceEvidence = foreach ($sourcePath in $sourcePaths | Sort-Object -Unique) {
        $resolvedSourcePath = (Resolve-Path $sourcePath).Path
        if (-not $resolvedSourcePath.StartsWith("$projectRoot\", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Source evidence escaped the project root: $resolvedSourcePath"
        }
        [ordered]@{
            path = $resolvedSourcePath.Substring($projectRoot.Length + 1).Replace("\", "/")
            sha256 = (Get-FileHash $resolvedSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $executablePath = Join-Path $output "lorekeeper-press-weasy.exe"
    $buildEvidence = [ordered]@{
        schemaVersion = 2
        platform = "windows"
        architecture = "x64"
        pythonVersion = $pythonVersion
        pythonExecutableSha256 = (Get-FileHash $python -Algorithm SHA256).Hash.ToLowerInvariant()
        weasyPrintVersion = (& $environmentPython -c "import importlib.metadata as m; print(m.version('weasyprint'))").Trim()
        pyInstallerVersion = (& $pyinstaller --version).Trim()
        nativeArchiveSha256 = (Get-FileHash $nativeArchive -Algorithm SHA256).Hash.ToLowerInvariant()
        nativeSourceManifest = [ordered]@{
            sha256 = (Get-FileHash $nativeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
            binaryCount = (Get-Content $nativeManifestPath -Raw | ConvertFrom-Json).binaryCount
        }
        executable = [ordered]@{
            byteLength = (Get-Item $executablePath).Length
            sha256 = (Get-FileHash $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        binaryInventory = [ordered]@{
            sha256 = (Get-FileHash $binaryInventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()
            binaryCount = (Get-Content $binaryInventoryPath -Raw | ConvertFrom-Json).binaryCount
            uncontrolledBinaryCount = 0
            releaseLicenseGatePassed = $false
        }
        fontInputs = @(
            [ordered]@{ name = "LiberationSerif-Regular.ttf"; sha256 = (Get-FileHash $fontRegular -Algorithm SHA256).Hash.ToLowerInvariant() },
            [ordered]@{ name = "LiberationSerif-Bold.ttf"; sha256 = (Get-FileHash $fontBold -Algorithm SHA256).Hash.ToLowerInvariant() },
            [ordered]@{ name = "Liberation Fonts LICENSE"; sha256 = (Get-FileHash $fontLicense -Algorithm SHA256).Hash.ToLowerInvariant() }
        )
        sourceFiles = $sourceEvidence
    }
    if (-not [string]::IsNullOrWhiteSpace($CmykProfilePath)) {
        $bundledProfile = Join-Path $distributionProfileDirectory "printing2009.icc"
        $buildEvidence["cmykProfile"] = [ordered]@{
            relativePath = "profiles/printing2009.icc"
            sha256 = (Get-FileHash $bundledProfile -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $buildEvidence["bundleFiles"] = @(
        Get-ChildItem $output -Recurse -File |
        Where-Object { $_.FullName -ne $buildEvidencePath } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                relativePath = $_.FullName.Substring($output.Length + 1).Replace("\", "/")
                sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    )
    [System.IO.File]::WriteAllText(
        $buildEvidencePath,
        ($buildEvidence | ConvertTo-Json -Depth 20)
    )
}
finally {
    $env:VIRTUAL_ENV = $previousVirtualEnvironment
    $env:PATH = $previousPath
    $env:WEASYPRINT_DLL_DIRECTORIES = $previousWeasyDllDirectories
    $env:FONTCONFIG_FILE = $previousFontConfigFile
}

[pscustomobject]@{
    PythonVersion = $pythonVersion
    Executable = (Get-FileHash (Join-Path $output "lorekeeper-press-weasy.exe") -Algorithm SHA256)
    BinaryInventory = (Get-FileHash $binaryInventoryPath -Algorithm SHA256)
    BuildEvidence = (Get-FileHash $buildEvidencePath -Algorithm SHA256)
}
