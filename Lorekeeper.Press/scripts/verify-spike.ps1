param(
    [string]$OutputRoot = "",
    [string]$PopplerBin = ""
)

$ErrorActionPreference = "Stop"
$pressRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent $pressRoot) ".tmp\press-spike-verification"
}

$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
$runRoot = Join-Path $resolvedOutputRoot ("run-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $runRoot | Out-Null

Push-Location $pressRoot
try {
    & cargo test --locked
    if ($LASTEXITCODE -ne 0) {
        throw "cargo test failed with exit code $LASTEXITCODE."
    }

    & cargo build --release --locked
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build --release failed with exit code $LASTEXITCODE."
    }

    $binaryName = if ($IsWindows -or $env:OS -eq "Windows_NT") {
        "lorekeeper-press.exe"
    }
    else {
        "lorekeeper-press"
    }
    $binaryPath = Join-Path $pressRoot "target\release\$binaryName"
    $representativeOutput = Join-Path $runRoot "representative-response.json"
    $invalidOutput = Join-Path $runRoot "invalid-pdfx-response.json"
    $representativeArguments = "--output-root `"$runRoot`""

    $representativeProcess = Start-Process -FilePath $binaryPath `
        -ArgumentList $representativeArguments `
        -RedirectStandardInput (Join-Path $pressRoot "fixtures\representative.json") `
        -RedirectStandardOutput $representativeOutput `
        -NoNewWindow `
        -PassThru `
        -Wait
    if ($representativeProcess.ExitCode -ne 0) {
        throw "Representative fixture failed with exit code $($representativeProcess.ExitCode)."
    }

    $invalidProcess = Start-Process -FilePath $binaryPath `
        -ArgumentList $representativeArguments `
        -RedirectStandardInput (Join-Path $pressRoot "fixtures\invalid-pdfx-request.json") `
        -RedirectStandardOutput $invalidOutput `
        -NoNewWindow `
        -PassThru `
        -Wait
    if ($invalidProcess.ExitCode -ne 0) {
        throw "The fail-closed PDF/X fixture could not be evaluated."
    }

    $representative = Get-Content -Raw -LiteralPath $representativeOutput | ConvertFrom-Json
    $invalid = Get-Content -Raw -LiteralPath $invalidOutput | ConvertFrom-Json
    if ($representative.status -ne "completed") {
        throw "The representative fixture did not complete."
    }
    if ($invalid.status -ne "rejected" -or $invalid.artifacts.Count -ne 0) {
        throw "The PDF/X fixture did not fail closed."
    }

    $durations = 1..5 | ForEach-Object {
        $jobOutput = Join-Path $runRoot "timing-$_.json"
        $timingOutputRoot = Join-Path $runRoot "timing-output-$_"
        $timingArguments = "--output-root `"$timingOutputRoot`""
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $binaryPath `
            -ArgumentList $timingArguments `
            -RedirectStandardInput (Join-Path $pressRoot "fixtures\representative.json") `
            -RedirectStandardOutput $jobOutput `
            -NoNewWindow `
            -PassThru `
            -Wait
        $timer.Stop()
        if ($process.ExitCode -ne 0) {
            throw "Timed renderer invocation failed."
        }
        $timer.Elapsed.TotalMilliseconds
    }

    $interiorPath = Join-Path $runRoot "representative-novel\interior.pdf"
    $renderedPagePath = Join-Path $runRoot "interior-first-page"
    if (-not [string]::IsNullOrWhiteSpace($PopplerBin)) {
        $executableSuffix = if ($IsWindows -or $env:OS -eq "Windows_NT") { ".exe" } else { "" }
        $pdfInfo = Join-Path $PopplerBin "pdfinfo$executableSuffix"
        $pdfToPpm = Join-Path $PopplerBin "pdftoppm$executableSuffix"
        & $pdfInfo $interiorPath
        if ($LASTEXITCODE -ne 0) {
            throw "Poppler pdfinfo failed."
        }
        & $pdfToPpm -f 1 -singlefile -png -r 72 $interiorPath $renderedPagePath
        if ($LASTEXITCODE -ne 0) {
            throw "Poppler rendering failed."
        }
    }

    [pscustomobject]@{
        BinaryBytes = (Get-Item -LiteralPath $binaryPath).Length
        FreshProcessRenderMillisecondsMinimum = ($durations | Measure-Object -Minimum).Minimum
        FreshProcessRenderMillisecondsMedian = ($durations | Sort-Object)[2]
        FreshProcessRenderMillisecondsMaximum = ($durations | Measure-Object -Maximum).Maximum
        OutputRoot = $runRoot
        PopplerRenderRun = -not [string]::IsNullOrWhiteSpace($PopplerBin)
    } | Format-List
}
finally {
    Pop-Location
}
