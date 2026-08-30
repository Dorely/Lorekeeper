param(
    [Parameter(Mandatory = $false)][string]$CandidatePath = "Lorekeeper.Press/assets/print-artifact-profiles-v1.json",
    [Parameter(Mandatory = $false)][string]$ReportPath = "artifacts/print-artifact-profile-review.md",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$registryPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $CandidatePath))
$canonicalPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "Lorekeeper.Press/assets/print-artifact-profiles-v1.json"))
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
if ([string]::IsNullOrWhiteSpace($registry.registryVersion) -or -not $registry.reviewedAtUtc -or $registry.profiles.Count -eq 0) {
    throw "The candidate is missing its registry version, review date, or artifact profiles."
}
if (($registry.profiles.key | Sort-Object -Unique).Count -ne $registry.profiles.Count) {
    throw "The candidate contains duplicate artifact profile keys."
}

$failures = [Collections.Generic.List[string]]::new()
foreach ($profile in $registry.profiles) {
    if ($profile.minimumPages -le 0 -or $profile.maximumPages -lt $profile.minimumPages) {
        $failures.Add("$($profile.key): invalid page range")
    }
    if ($profile.vendor -ne "Generic" -and $profile.spineModel.kind -eq "FrozenLookup") {
        $expectedPages = @($profile.minimumPages..$profile.maximumPages | Where-Object { $_ % 2 -eq 0 })
        $actualPages = @($profile.spineModel.anchors.pages)
        if (Compare-Object $expectedPages $actualPages) {
            $failures.Add("$($profile.key): frozen lookup does not contain every normalized even page")
        }
        if ($profile.spineModel.anchors | Where-Object { $_.inches -le 0 }) {
            $failures.Add("$($profile.key): frozen lookup contains a non-positive measurement")
        }
    }
    if ($profile.vendor -notin @("Generic", "BarnesAndNoblePress") -and $profile.spineModel.kind -eq "TemplateRequired") {
        $failures.Add("$($profile.key): this vendor artifact profile cannot use imported geometry")
    }
}
if ($failures.Count -gt 0) {
    throw "Registry review failed:`n$($failures -join "`n")"
}

$sha256 = (Get-FileHash -LiteralPath $registryPath -Algorithm SHA256).Hash.ToLowerInvariant()
$reportDirectory = Split-Path $reportFile -Parent
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Print-artifact profile registry review")
$lines.Add("")
$lines.Add("- Registry: ``$($registry.registryVersion)``")
$lines.Add("- Reviewed: ``$($registry.reviewedAtUtc)``")
$lines.Add("- SHA-256: ``$sha256``")
$lines.Add("- Profiles: $($registry.profiles.Count)")
$lines.Add("")
$lines.Add("## Official sources requiring maintainer review")
$lines.Add("")
foreach ($source in $registry.sources) { $lines.Add("- $source") }
$lines.Add("")
$lines.Add("## Artifact evidence")
$lines.Add("")
$lines.Add("| Key | Vendor | Format | Process | Construction | Weight | Page range | Spine evidence |")
$lines.Add("| --- | --- | --- | --- | --- | --- | --- | --- |")
foreach ($profile in $registry.profiles | Sort-Object vendor, format, key) {
    $weight = if ($profile.basisWeightPounds) { "$($profile.basisWeightPounds) lb / $($profile.gsm) gsm" } else { "template declared" }
    $spineEvidence = if ($profile.spineModel.kind -eq "FrozenLookup") { "$($profile.spineModel.anchors.Count) exact measurements" } elseif ($profile.spineModel.kind -eq "Caliper") { "published thickness formula" } else { "printer template required" }
    $lines.Add("| ``$($profile.key)`` | $($profile.vendor) | $($profile.format) | $($profile.interiorProcess) | $($profile.coverMaterial) | $weight | $($profile.minimumSubmittedPages)-$($profile.maximumSubmittedPages) | $spineEvidence |")
}
[IO.File]::WriteAllLines($reportFile, $lines, [Text.UTF8Encoding]::new($false))

if ($Install -and $registryPath -ne $canonicalPath) {
    Copy-Item -LiteralPath $registryPath -Destination $canonicalPath -Force
}

Write-Host "Registry review passed: $sha256"
Write-Host "Review report: $reportFile"
