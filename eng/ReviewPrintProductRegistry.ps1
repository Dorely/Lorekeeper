param(
    [Parameter(Mandatory = $false)][string]$CandidatePath = "Lorekeeper.Press/assets/print-products-v1.json",
    [Parameter(Mandatory = $false)][string]$ReportPath = "artifacts/print-product-registry-review.md",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$registryPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $CandidatePath))
$canonicalPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "Lorekeeper.Press/assets/print-products-v1.json"))
$reportFile = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReportPath))
if (-not $registryPath.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not $reportFile.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Registry candidates and review reports must remain inside the repository."
}
if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
    throw "Registry candidate '$registryPath' does not exist."
}

$bytes = [IO.File]::ReadAllBytes($registryPath)
$registry = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($registry.registryVersion) -or -not $registry.reviewedAtUtc -or $registry.products.Count -eq 0) {
    throw "The candidate is missing its registry version, review date, or products."
}
if (($registry.products.key | Sort-Object -Unique).Count -ne $registry.products.Count) {
    throw "The candidate contains duplicate product keys."
}

$failures = [Collections.Generic.List[string]]::new()
foreach ($product in $registry.products) {
    if ($product.minimumPages -le 0 -or $product.maximumPages -lt $product.minimumPages) {
        $failures.Add("$($product.key): invalid page range")
    }
    if ($product.vendor -ne "Generic" -and $product.spineModel.kind -eq "FrozenLookup") {
        $expectedPages = @($product.minimumPages..$product.maximumPages | Where-Object { $_ % 2 -eq 0 })
        $actualPages = @($product.spineModel.anchors.pages)
        if (Compare-Object $expectedPages $actualPages) {
            $failures.Add("$($product.key): frozen lookup does not contain every normalized even page")
        }
        if ($product.spineModel.anchors | Where-Object { $_.inches -le 0 }) {
            $failures.Add("$($product.key): frozen lookup contains a non-positive measurement")
        }
    }
    if ($product.vendor -ne "Generic" -and $product.spineModel.kind -eq "TemplateRequired") {
        $failures.Add("$($product.key): a specific vendor product cannot use a printer template")
    }
}
if ($failures.Count -gt 0) {
    throw "Registry review failed:`n$($failures -join "`n")"
}

$sha256 = (Get-FileHash -LiteralPath $registryPath -Algorithm SHA256).Hash.ToLowerInvariant()
$reportDirectory = Split-Path $reportFile -Parent
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Print-product registry review")
$lines.Add("")
$lines.Add("- Registry: ``$($registry.registryVersion)``")
$lines.Add("- Reviewed: ``$($registry.reviewedAtUtc)``")
$lines.Add("- SHA-256: ``$sha256``")
$lines.Add("- Products: $($registry.products.Count)")
$lines.Add("")
$lines.Add("## Official sources requiring maintainer review")
$lines.Add("")
foreach ($source in $registry.sources) { $lines.Add("- $source") }
$lines.Add("")
$lines.Add("## Product evidence")
$lines.Add("")
$lines.Add("| Key | Vendor | Format | Stock | Weight | Page range | Spine evidence |")
$lines.Add("| --- | --- | --- | --- | --- | --- | --- |")
foreach ($product in $registry.products | Sort-Object vendor, format, key) {
    $weight = if ($product.basisWeightPounds) { "$($product.basisWeightPounds) lb / $($product.gsm) gsm" } else { "printer declared" }
    $spineEvidence = if ($product.spineModel.kind -eq "FrozenLookup") { "$($product.spineModel.anchors.Count) exact measurements" } elseif ($product.spineModel.kind -eq "Caliper") { "published stock formula" } else { "printer template required" }
    $lines.Add("| ``$($product.key)`` | $($product.vendor) | $($product.format) | $($product.paperName) | $weight | $($product.minimumSubmittedPages)-$($product.maximumSubmittedPages) | $spineEvidence |")
}
[IO.File]::WriteAllLines($reportFile, $lines, [Text.UTF8Encoding]::new($false))

if ($Install -and $registryPath -ne $canonicalPath) {
    Copy-Item -LiteralPath $registryPath -Destination $canonicalPath -Force
}

Write-Host "Registry review passed: $sha256"
Write-Host "Review report: $reportFile"
