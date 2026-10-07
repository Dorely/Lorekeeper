param(
    [Parameter(Mandatory = $true)][string]$PressProjectDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][ValidateSet("Debug", "Release")][string]$Configuration
)

$ErrorActionPreference = "Stop"
$runningWindows = $env:OS -eq "Windows_NT"
$pressRoot = [IO.Path]::GetFullPath($PressProjectDirectory)
$runtimeRoot = [IO.Path]::GetFullPath($RuntimeDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $pressRoot ".."))
if (-not $runtimeRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Press runtime output must remain inside the Lorekeeper repository."
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try {
        $hashBytes = $sha256.ComputeHash($stream)
        return ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha256.Dispose()
    }
}

$cargoArguments = @("build", "--locked")
$cargoProfile = "debug"
if ($Configuration -eq "Release") {
    $cargoArguments += "--release"
    $cargoProfile = "release"
}

Push-Location $pressRoot
try {
    & cargo @cargoArguments
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
    $metadata = (& cargo metadata --locked --format-version 1 | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw "cargo metadata failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

$lockText = [IO.File]::ReadAllText((Join-Path $pressRoot 'Cargo.lock')) -replace '\r\n?', "`n"
$lockedChecksums = @{}
foreach ($packageMatch in [regex]::Matches(
    $lockText,
    '(?ms)^\[\[package\]\]\s*(?<body>.*?)(?=^\[\[package\]\]|\z)')) {
    $body = $packageMatch.Groups['body'].Value
    $fields = @{}
    foreach ($fieldName in @('name', 'version', 'source', 'checksum')) {
        $fieldPattern = '(?m)^{0} = "(?<value>[^"]*)"$' -f [regex]::Escape($fieldName)
        $fieldMatch = [regex]::Match($body, $fieldPattern)
        $fields[$fieldName] = if ($fieldMatch.Success) { $fieldMatch.Groups['value'].Value } else { '' }
    }
    if (-not [string]::IsNullOrWhiteSpace($fields.source)) {
        if ($fields.checksum -notmatch '^[0-9a-f]{64}$') {
            throw "Cargo.lock has no valid checksum for $($fields.name) $($fields.version)."
        }
        $key = "$($fields.name)`0$($fields.version)`0$($fields.source)"
        if ($lockedChecksums.ContainsKey($key)) {
            throw "Cargo.lock contains a duplicate package identity for $($fields.name) $($fields.version)."
        }
        $lockedChecksums[$key] = $fields.checksum
    }
}

foreach ($package in $metadata.packages | Where-Object { $_.source }) {
    $key = "$($package.name)`0$($package.version)`0$($package.source)"
    if (-not $lockedChecksums.ContainsKey($key)) {
        throw "The resolved package $($package.name) $($package.version) is not fingerprinted by Cargo.lock."
    }
}

$approvedLicenseExpressions = @(
    '(Apache-2.0 OR MIT) AND BSD-3-Clause',
    '(MIT OR Apache-2.0) AND IJG',
    '(MIT OR Apache-2.0) AND Unicode-3.0',
    '0BSD OR MIT OR Apache-2.0',
    'Apache-2.0',
    'Apache-2.0 OR BSL-1.0',
    'Apache-2.0 OR MIT',
    'Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT',
    'BSD-3-Clause OR Apache-2.0',
    'MIT',
    'MIT OR Apache-2.0',
    'MIT OR Apache-2.0 OR LGPL-2.1-or-later',
    'MIT OR Apache-2.0 OR Zlib',
    'MIT OR Zlib OR Apache-2.0',
    'MIT/Apache-2.0',
    'Unlicense OR MIT',
    'Zlib OR Apache-2.0 OR MIT'
)
$unsupported = $metadata.packages | Where-Object {
    $_.name -ne "lorekeeper-press" -and (
        [string]::IsNullOrWhiteSpace($_.license) -or
        $_.license -cnotin $approvedLicenseExpressions
    )
}
if ($unsupported) {
    throw "The locked Press graph contains an unapproved license: $($unsupported.name -join ', ')"
}

if (Test-Path -LiteralPath $runtimeRoot) {
    $resolvedRuntime = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $runtimeRoot).Path)
    if ($resolvedRuntime -ne $runtimeRoot) { throw "Refusing to replace an unexpected Press runtime path." }
    Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runtimeRoot "fonts") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runtimeRoot "profiles") | Out-Null

$executableName = if ($runningWindows) { "lorekeeper-press.exe" } else { "lorekeeper-press" }
$executable = Join-Path $pressRoot "target/$cargoProfile/$executableName"
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "The native Press executable was not produced." }
Copy-Item -LiteralPath $executable -Destination (Join-Path $runtimeRoot $executableName)

$fontFiles = @(
    "Lorekeeper/wwwroot/fonts/lora/Lora-Regular.ttf",
    "Lorekeeper/wwwroot/fonts/lora/Lora-Italic.ttf",
    "Lorekeeper/wwwroot/fonts/lora/Lora-Bold.ttf",
    "Lorekeeper/wwwroot/fonts/lora/Lora-BoldItalic.ttf",
    "Lorekeeper/wwwroot/fonts/lora/OFL.txt",
    "Lorekeeper/wwwroot/fonts/nunito/Nunito-Regular.ttf",
    "Lorekeeper/wwwroot/fonts/nunito/Nunito-Italic.ttf",
    "Lorekeeper/wwwroot/fonts/nunito/Nunito-Bold.ttf",
    "Lorekeeper/wwwroot/fonts/nunito/Nunito-BoldItalic.ttf",
    "Lorekeeper/wwwroot/fonts/nunito/OFL.txt",
    "Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-Regular.ttf",
    "Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-Italic.ttf",
    "Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-Bold.ttf",
    "Lorekeeper/wwwroot/fonts/roboto-mono/RobotoMono-BoldItalic.ttf",
    "Lorekeeper/wwwroot/fonts/roboto-mono/OFL.txt"
)
foreach ($relativePath in $fontFiles) {
    $source = Join-Path $repositoryRoot $relativePath
    $family = Split-Path (Split-Path $source -Parent) -Leaf
    $destinationDirectory = Join-Path $runtimeRoot "fonts/$family"
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $destinationDirectory (Split-Path $source -Leaf))
}
Copy-Item -LiteralPath (Join-Path $pressRoot "assets/profiles/CGATS21_CRPC1.icc") -Destination (Join-Path $runtimeRoot "profiles/CGATS21_CRPC1.icc")
Copy-Item -LiteralPath (Join-Path $pressRoot "assets/profiles/SOURCE.md") -Destination (Join-Path $runtimeRoot "profiles/SOURCE.md")

$notice = [Text.StringBuilder]::new()
[void]$notice.AppendLine("Lorekeeper Press third-party notices")
[void]$notice.AppendLine("Generated from Cargo.lock; builds fail closed for unapproved licenses.")
foreach ($package in ($metadata.packages | Where-Object name -ne "lorekeeper-press" | Sort-Object name, version)) {
    [void]$notice.AppendLine()
    [void]$notice.AppendLine("$($package.name) $($package.version) - $($package.license)")
    if ($package.repository) { [void]$notice.AppendLine($package.repository) }
    $packageRoot = Split-Path $package.manifest_path -Parent
    $licenseFiles = Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Name -Match "^(LICENSE|COPYING|UNLICENSE|NOTICE)"
    foreach ($licenseFile in $licenseFiles) {
        [void]$notice.AppendLine("--- $($licenseFile.Name) ---")
        [void]$notice.AppendLine([IO.File]::ReadAllText($licenseFile.FullName))
    }
    if ($package.name -eq 'hypher') {
        $readme = Join-Path $packageRoot 'README.md'
        if (-not (Test-Path -LiteralPath $readme -PathType Leaf)) {
            throw 'The locked English hyphenation component is missing its pattern-license notice.'
        }
        [void]$notice.AppendLine('--- README pattern-license notice ---')
        [void]$notice.AppendLine([IO.File]::ReadAllText($readme))
    }
}
[void]$notice.AppendLine()
[void]$notice.AppendLine('Bundled assets')
[void]$notice.AppendLine('Lora, Nunito, and Roboto Mono font files - SIL Open Font License 1.1; the corresponding OFL.txt files are packaged beside each family.')
[void]$notice.AppendLine('CGATS21 CRPC1 ICC profile - redistribution/source terms and the source fingerprint are packaged at profiles/SOURCE.md.')
[IO.File]::WriteAllText((Join-Path $runtimeRoot "THIRD-PARTY-NOTICES.txt"), $notice.ToString(), [Text.UTF8Encoding]::new($false))

$assetInventory = @(
    Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'fonts') -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            relativePath = $_.FullName.Substring($runtimeRoot.Length + 1).Replace('\', '/')
            sha256 = Get-Sha256Hex -Path $_.FullName
            license = 'OFL-1.1'
        }
    }
    Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'profiles') -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            relativePath = $_.FullName.Substring($runtimeRoot.Length + 1).Replace('\', '/')
            sha256 = Get-Sha256Hex -Path $_.FullName
            license = if ($_.Name -eq 'CGATS21_CRPC1.icc') { 'ICC-profile-redistribution' } else { 'Documentation' }
        }
    }
)
$sbom = [ordered]@{
    schemaVersion = 1
    generatedFrom = "Cargo.lock"
    packages = @($metadata.packages | Sort-Object name, version | ForEach-Object {
        $checksum = if ($_.source) {
            $lockedChecksums["$($_.name)`0$($_.version)`0$($_.source)"]
        } else {
            $null
        }
        [ordered]@{ name = $_.name; version = $_.version; license = $_.license; source = $_.source; checksum = $checksum }
    })
    assets = $assetInventory
}
[IO.File]::WriteAllText((Join-Path $runtimeRoot "sbom.json"), ($sbom | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$descriptionText = & (Join-Path $runtimeRoot $executableName) describe --json
if ($LASTEXITCODE -ne 0) { throw "The built Press renderer did not provide its capability contract." }
$description = $descriptionText | ConvertFrom-Json
if ($description.protocolVersion -ne 15 -or [string]::IsNullOrWhiteSpace($description.rendererVersion) -or
    [string]::IsNullOrWhiteSpace($description.printArtifactProfileRegistryVersion) -or
    [string]::IsNullOrWhiteSpace($description.printArtifactProfileRegistrySha256)) {
    throw "The built Press renderer returned an invalid capability contract."
}

$files = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        relativePath = $_.FullName.Substring($runtimeRoot.Length + 1).Replace('\', '/')
        byteLength = $_.Length
        sha256 = Get-Sha256Hex -Path $_.FullName
    }
})
$architecture = switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { "x64" }
    "ARM64" { "arm64" }
    "x86" { "x86" }
    default { [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() }
}
$manifest = [ordered]@{
    schemaVersion = 3
    platform = if ($runningWindows) {
        "windows"
    } elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::OSX)) {
        "macos"
    } elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)) {
        "linux"
    } else {
        "unsupported"
    }
    architecture = $architecture
    description = $description
    files = $files
}
[IO.File]::WriteAllText(
    (Join-Path $runtimeRoot "lorekeeper-press-runtime.json"),
    ($manifest | ConvertTo-Json -Depth 16),
    [Text.UTF8Encoding]::new($false))
