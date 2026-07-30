param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$CmykProfilePath,

    [string]$OutputDirectory = (
        Join-Path $PSScriptRoot "..\.tmp\verification-$([Guid]::NewGuid().ToString('N'))"
    )
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$repositoryRoot = (Resolve-Path (Join-Path $projectRoot "..")).Path
$executable = (Resolve-Path $ExecutablePath).Path
$profile = (Resolve-Path $CmykProfilePath).Path
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output | Out-Null
$distributionRoot = (Get-Item $executable).DirectoryName
$fontDirectory = Join-Path $distributionRoot "fonts"
$fontConfig = Join-Path $fontDirectory "fonts.conf"
$fontCache = Join-Path $output "font-cache"
if (-not (Test-Path $fontConfig)) {
    throw "The controlled font configuration is missing from the distribution."
}
New-Item -ItemType Directory -Path $fontCache | Out-Null

function Invoke-RendererProcess {
    param(
        [string]$Arguments = "",

        [Parameter(Mandatory = $true)]
        [string]$RequestJson,

        [string]$ProcessExecutable = $executable,

        [ValidateSet("controlled", "missing", "hostile")]
        [string]$FontEnvironment = "controlled"
    )

    $processDistribution = (Get-Item $ProcessExecutable).DirectoryName
    $processFontDirectory = Join-Path $processDistribution "fonts"
    $processFontConfig = Join-Path $processFontDirectory "fonts.conf"
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $process.StartInfo.FileName = $ProcessExecutable
    $process.StartInfo.Arguments = $Arguments
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true
    if ($FontEnvironment -eq "controlled") {
        $process.StartInfo.EnvironmentVariables["FONTCONFIG_FILE"] = $processFontConfig
        $process.StartInfo.EnvironmentVariables["FONTCONFIG_PATH"] = $processFontDirectory
        $process.StartInfo.EnvironmentVariables["XDG_CACHE_HOME"] = $fontCache
        $process.StartInfo.EnvironmentVariables["FONTCONFIG_USE_MMAP"] = "0"
    }
    elseif ($FontEnvironment -eq "missing") {
        foreach ($name in @("FONTCONFIG_FILE", "FONTCONFIG_PATH", "XDG_CACHE_HOME", "FONTCONFIG_USE_MMAP")) {
            $process.StartInfo.EnvironmentVariables.Remove($name)
        }
    }
    else {
        $process.StartInfo.EnvironmentVariables["FONTCONFIG_FILE"] = Join-Path $output "hostile-fonts.conf"
        $process.StartInfo.EnvironmentVariables["FONTCONFIG_PATH"] = $output
        $process.StartInfo.EnvironmentVariables["XDG_CACHE_HOME"] = $output
        $process.StartInfo.EnvironmentVariables["FONTCONFIG_USE_MMAP"] = "1"
    }
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $null = $process.Start()
    $process.StandardInput.Write($RequestJson)
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $stopwatch.Stop()
    if ($stderr) {
        throw "The renderer wrote to stderr: $stderr"
    }
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        Result = $stdout | ConvertFrom-Json
    }
}

function Invoke-Renderer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RequestJson,

        [string]$ProfilePath
    )

    $arguments = "--output-root `"$output`""
    if ($ProfilePath) {
        $arguments += " --cmyk-profile `"$ProfilePath`""
    }
    Invoke-RendererProcess -Arguments $arguments -RequestJson $RequestJson
}

function Assert-Completed {
    param(
        [Parameter(Mandatory = $true)]
        $Run,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ($Run.ExitCode -ne 0 -or $Run.Result.status -ne "completed") {
        throw "$Label failed: $($Run.Result | ConvertTo-Json -Depth 20 -Compress)"
    }
    if ($Run.Result.artifacts.Count -ne 2) {
        throw "$Label did not emit both artifacts."
    }
    foreach ($artifact in $Run.Result.artifacts) {
        $artifactPath = Join-Path $output $artifact.relativePath
        $hash = (Get-FileHash $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $artifact.sha256) {
            throw "Artifact hash mismatch for $Label/$($artifact.kind)."
        }
    }
}

$environmentArguments = "--output-root `"$output`""
foreach ($environmentKind in @("missing", "hostile")) {
    $environmentRun = Invoke-RendererProcess `
        -Arguments $environmentArguments `
        -RequestJson "{}" `
        -FontEnvironment $environmentKind
    if (
        $environmentRun.ExitCode -eq 0 -or
        $environmentRun.Result.status -ne "failed" -or
        "PRESS_FONT_ENVIRONMENT_INVALID" -notin $environmentRun.Result.diagnostics.code
    ) {
        throw "$environmentKind font environment did not fail with the versioned startup diagnostic."
    }
}

$tamperedDistribution = Join-Path $output "tampered-distribution"
$tamperedFontDirectory = Join-Path $tamperedDistribution "fonts"
$tamperedLicenseDirectory = Join-Path $tamperedDistribution "licenses"
New-Item -ItemType Directory -Path $tamperedDistribution, $tamperedFontDirectory, $tamperedLicenseDirectory | Out-Null
$tamperedExecutable = Join-Path $tamperedDistribution "lorekeeper-press-weasy.exe"
Copy-Item -LiteralPath $executable -Destination $tamperedExecutable
Copy-Item -Path (Join-Path $fontDirectory "*") -Destination $tamperedFontDirectory
Copy-Item -Path (Join-Path $distributionRoot "licenses\*") -Destination $tamperedLicenseDirectory
[System.IO.File]::WriteAllBytes(
    (Join-Path $tamperedFontDirectory "LiberationSerif-Regular.ttf"),
    [byte[]](0, 1, 2, 3)
)
$tamperedRun = Invoke-RendererProcess `
    -Arguments $environmentArguments `
    -RequestJson "{}" `
    -ProcessExecutable $tamperedExecutable
if (
    $tamperedRun.ExitCode -eq 0 -or
    $tamperedRun.Result.status -ne "failed" -or
    "PRESS_FONT_ENVIRONMENT_INVALID" -notin $tamperedRun.Result.diagnostics.code
) {
    throw "A tampered sibling font did not fail with the versioned startup diagnostic."
}

$startupRun = Invoke-RendererProcess -Arguments "" -RequestJson "{}"
if (
    $startupRun.ExitCode -eq 0 -or
    $startupRun.Result.status -ne "failed" -or
    "PRESS_STARTUP_ARGUMENTS_INVALID" -notin $startupRun.Result.diagnostics.code
) {
    throw "Missing startup arguments did not return the versioned error envelope."
}

$pdfxRequestPath = Join-Path $repositoryRoot "Lorekeeper.Press\fixtures\invalid-pdfx-request.json"
$pdfxRequest = Get-Content $pdfxRequestPath -Raw | ConvertFrom-Json
$pdfxRequest.jobId = "weasy-pdfx-determinism-a"
$pdfxRequestJsonA = $pdfxRequest | ConvertTo-Json -Depth 10 -Compress
$pdfxRunA = Invoke-Renderer -RequestJson $pdfxRequestJsonA -ProfilePath $profile
Assert-Completed -Run $pdfxRunA -Label "PDF/X-named run A"

$pdfxRequest.jobId = "weasy-pdfx-determinism-b"
$pdfxRequestJsonB = $pdfxRequest | ConvertTo-Json -Depth 10 -Compress
$previousFontConfigFile = $env:FONTCONFIG_FILE
$previousFontConfigPath = $env:FONTCONFIG_PATH
$previousXdgCacheHome = $env:XDG_CACHE_HOME
try {
    $env:FONTCONFIG_FILE = Join-Path $output "host-must-not-control-fonts.conf"
    $env:FONTCONFIG_PATH = $output
    $env:XDG_CACHE_HOME = $output
    $pdfxRunB = Invoke-Renderer -RequestJson $pdfxRequestJsonB -ProfilePath $profile
}
finally {
    $env:FONTCONFIG_FILE = $previousFontConfigFile
    $env:FONTCONFIG_PATH = $previousFontConfigPath
    $env:XDG_CACHE_HOME = $previousXdgCacheHome
}
Assert-Completed -Run $pdfxRunB -Label "PDF/X-named run B"

if (
    $pdfxRunA.Result.evidence.declaredStandard -ne "PDF/X-1a:2001" -or
    $null -ne $pdfxRunA.Result.evidence.claimedStandard -or
    $null -ne $pdfxRunA.Result.evidence.independentlyValidatedStandard
) {
    throw "The PDF/X-named fixture reported an invalid declaration/claim boundary."
}
if (
    $pdfxRunA.Result.evidence.hasTransparency -or
    $pdfxRunA.Result.evidence.hasEncryption -or
    $pdfxRunA.Result.evidence.hasForbiddenActions
) {
    throw "A forbidden feature was found in the PDF/X-named fixture."
}
for ($index = 0; $index -lt 2; $index++) {
    if ($pdfxRunA.Result.artifacts[$index].sha256 -ne $pdfxRunB.Result.artifacts[$index].sha256) {
        throw "The two fresh PDF/X-named runs were not byte-identical."
    }
}

$collisionRun = Invoke-Renderer -RequestJson $pdfxRequestJsonA -ProfilePath $profile
if (
    $collisionRun.ExitCode -eq 0 -or
    $collisionRun.Result.status -ne "failed" -or
    "PRESS_RENDER_FAILED" -notin $collisionRun.Result.diagnostics.code
) {
    throw "An immutable output collision did not fail with the expected envelope."
}

$invalidProfilePath = Join-Path $output "invalid-profile.icc"
[System.IO.File]::WriteAllBytes($invalidProfilePath, [byte[]](0, 1, 2, 3))
$pdfxRequest.jobId = "weasy-invalid-profile"
$invalidProfileRun = Invoke-Renderer `
    -RequestJson ($pdfxRequest | ConvertTo-Json -Depth 10 -Compress) `
    -ProfilePath $invalidProfilePath
if (
    $invalidProfileRun.ExitCode -eq 0 -or
    $invalidProfileRun.Result.status -ne "rejected" -or
    "PRESS_CMYK_PROFILE_REJECTED" -notin $invalidProfileRun.Result.diagnostics.code -or
    $invalidProfileRun.Result.artifacts.Count -ne 0
) {
    throw "A profile with the wrong fingerprint was not rejected without artifacts."
}

$kdpRequestPath = Join-Path $repositoryRoot "Lorekeeper.Press\fixtures\representative.json"
$kdpRequest = Get-Content $kdpRequestPath -Raw | ConvertFrom-Json
$kdpRequest.jobId = "weasy-kdp-verification"
$kdpRun = Invoke-Renderer -RequestJson ($kdpRequest | ConvertTo-Json -Depth 10 -Compress)
Assert-Completed -Run $kdpRun -Label "KDP-named run"
if (
    $kdpRun.Result.evidence.pdfVersion -ne "1.7" -or
    $null -ne $kdpRun.Result.evidence.declaredStandard -or
    $null -ne $kdpRun.Result.evidence.claimedStandard -or
    $null -ne $kdpRun.Result.evidence.independentlyValidatedStandard
) {
    throw "The KDP-named fixture made an invalid standard or vendor claim."
}

$independentInputPath = Join-Path $output "independent-verification-input.json"
$independentInput = @{
    outputRoot = $output
    pdfxRunA = $pdfxRunA.Result
    pdfxRunB = $pdfxRunB.Result
    kdpRun = $kdpRun.Result
} | ConvertTo-Json -Depth 30
[System.IO.File]::WriteAllText($independentInputPath, $independentInput)
$independentReportJson = & uv run `
    --project $projectRoot `
    --locked `
    python `
    (Join-Path $PSScriptRoot "verify-artifacts.py") `
    $independentInputPath
if ($LASTEXITCODE -ne 0) {
    throw "Independent artifact verification failed."
}
$independentReport = $independentReportJson | ConvertFrom-Json

$binary = Get-Item $executable
$binaryInventoryPath = Join-Path $binary.DirectoryName "lorekeeper-press-weasy-binaries.json"
$buildEvidencePath = Join-Path $binary.DirectoryName "lorekeeper-press-weasy-build.json"
$nativeSourcePath = Join-Path $binary.DirectoryName "lorekeeper-press-weasy-native-source.json"
if (
    -not (Test-Path $binaryInventoryPath) -or
    -not (Test-Path $buildEvidencePath) -or
    -not (Test-Path $nativeSourcePath)
) {
    throw "The controlled build evidence, native source, and binary inventory must accompany the executable."
}
$binaryInventory = Get-Content $binaryInventoryPath -Raw | ConvertFrom-Json
$buildEvidence = Get-Content $buildEvidencePath -Raw | ConvertFrom-Json
$nativeSource = Get-Content $nativeSourcePath -Raw | ConvertFrom-Json
if ($binaryInventory.releaseLicenseGatePassed -ne $false) {
    throw "The spike binary inventory must not fabricate a completed release-license gate."
}
$executableHash = (Get-FileHash $executable -Algorithm SHA256).Hash.ToLowerInvariant()
$binaryInventoryHash = (Get-FileHash $binaryInventoryPath -Algorithm SHA256).Hash.ToLowerInvariant()
$nativeSourceHash = (Get-FileHash $nativeSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (
    $buildEvidence.executable.sha256 -ne $executableHash -or
    $buildEvidence.binaryInventory.sha256 -ne $binaryInventoryHash -or
    $buildEvidence.nativeSourceManifest.sha256 -ne $nativeSourceHash -or
    $buildEvidence.binaryInventory.binaryCount -ne $binaryInventory.binaryCount -or
    $buildEvidence.nativeSourceManifest.binaryCount -ne $nativeSource.binaryCount
) {
    throw "The controlled build evidence does not match its executable or inventories."
}
$evidence = [ordered]@{
    schemaVersion = 1
    platform = "windows-x64"
    executable = [ordered]@{
        byteLength = $binary.Length
        sha256 = $executableHash
    }
    profileSha256 = (Get-FileHash $profile -Algorithm SHA256).Hash.ToLowerInvariant()
    buildEvidenceSha256 = (Get-FileHash $buildEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    binaryInventory = [ordered]@{
        sha256 = $binaryInventoryHash
        binaryCount = $binaryInventory.binaryCount
        releaseLicenseGatePassed = $false
    }
    nativeSource = [ordered]@{
        sha256 = $nativeSourceHash
        binaryCount = $nativeSource.binaryCount
    }
    fixtureSha256 = [ordered]@{
        pdfx = (Get-FileHash $pdfxRequestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        kdp = (Get-FileHash $kdpRequestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    elapsedMilliseconds = [ordered]@{
        pdfxA = $pdfxRunA.ElapsedMilliseconds
        pdfxB = $pdfxRunB.ElapsedMilliseconds
        kdp = $kdpRun.ElapsedMilliseconds
    }
    artifacts = $independentReport.artifacts
    checks = [ordered]@{
        sourceUnitFixtures = "run separately"
        startupEnvelope = $true
        wrongProfileRejected = $true
        immutableCollisionRejected = $true
        byteDeterministic = $true
        missingFontEnvironmentRejected = $true
        hostileFontEnvironmentRejected = $true
        tamperedFontBundleRejected = $true
        independentParseAndTextExtraction = $true
        internalStructuralInspection = $true
        externalPdfxValidation = $false
        vendorUpload = $false
        physicalProof = $false
        releaseLicenseGate = $false
    }
}
$evidencePath = Join-Path $output "evidence.json"
[System.IO.File]::WriteAllText(
    $evidencePath,
    ($evidence | ConvertTo-Json -Depth 30)
)
$evidence | ConvertTo-Json -Depth 30
