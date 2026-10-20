#Requires -Version 7.5
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [AllowEmptyString()]
    [string]$ReleaseNotes,

    [string]$Version,

    [switch]$Submit,

    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'Microsoft Store submission must run on Windows.'
}
if ($CheckOnly -and $Submit)
{
    throw 'Use -CheckOnly or -Submit, not both.'
}
if (-not (Get-Command msstore -ErrorAction SilentlyContinue))
{
    throw 'Install the Microsoft Store Developer CLI (winget install "Microsoft Store Developer CLI") and run msstore reconfigure.'
}

$productId = '9N3QQPF401L5'
$maxReleaseNotesLength = 1500
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$storeOutputDirectory = Join-Path $repoRoot '.artifacts\msix\store'
. (Join-Path $repoRoot 'eng/ReleaseWorkflow.ps1')

$sourceVersion = Get-LorekeeperVersion (Join-Path $repoRoot 'Lorekeeper\Lorekeeper.csproj')
if ([string]::IsNullOrWhiteSpace($Version))
{
    $Version = $sourceVersion
}
elseif (-not $CheckOnly -and $Version -cne $sourceVersion)
{
    throw 'Only -CheckOnly may preview a version other than Lorekeeper.csproj; submission uses the built source version.'
}
$msixVersion = ConvertTo-MsixVersion $Version
if ($ReleaseNotes.Length -gt $maxReleaseNotesLength)
{
    throw "Store release notes are limited to $maxReleaseNotesLength characters; these have $($ReleaseNotes.Length)."
}

function Invoke-MsStore
{
    param([Parameter(Mandatory)][string[]]$Arguments)

    # Progress goes to stderr and stays visible; stdout carries command results.
    Write-Host "> msstore $($Arguments -join ' ')" -ForegroundColor Cyan
    $output = & msstore @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "msstore failed with exit code $LASTEXITCODE. Run msstore reconfigure if the Partner Center key expired."
    }
    return ($output -join [Environment]::NewLine)
}

function Get-StoreSubmission
{
    # Read-only: returns the pending submission, or the last published one when none is pending.
    # String dates keep the round-tripped submission byte-faithful for the update payload.
    $submission = (Invoke-MsStore @('submission', 'get', $productId)) | ConvertFrom-Json -DateKind String
    foreach ($property in @('Id', 'Status', 'ApplicationPackages', 'Listings', 'Pricing'))
    {
        if (-not $submission.PSObject.Properties[$property])
        {
            throw "The Store submission response for $productId is missing '$property'."
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$submission.Pricing.PriceId))
    {
        throw 'The Store submission has no base price; the submission API cannot round-trip it. Inspect pricing in Partner Center.'
    }
    return $submission
}

function Assert-OlderStorePackages
{
    param(
        [Parameter(Mandatory)]$Submission,
        [string]$ExcludeFileName
    )

    foreach ($package in @($Submission.ApplicationPackages))
    {
        if ([string]$package.FileStatus -eq 'PendingDelete' -or [string]$package.FileName -ceq $ExcludeFileName) { continue }
        $existingVersion = $null
        if ([Version]::TryParse([string]$package.Version, [ref]$existingVersion) -and $existingVersion -ge [Version]$msixVersion)
        {
            throw "The Store already has package $existingVersion ($($package.FileName)); MSIX $msixVersion must be higher. Release a newer source version."
        }
    }
}

$package = $null
if (-not $CheckOnly)
{
    $reportPath = Join-Path $storeOutputDirectory 'msix-package.json'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf))
    {
        throw "Build the Store MSIX first with tools/msix/Build-WindowsMsix.ps1; missing $reportPath."
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $package = $report.package
    $packagePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$package.relativePath)))
    $storePrefix = $storeOutputDirectory.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($report.formatVersion -ne 2 -or
        [string]$package.distributionChannel -cne 'Store' -or
        [string]$package.identityName -match 'LocalValidation|LocalFeasibility' -or
        [string]$package.sourceVersion -cne $sourceVersion -or
        [string]$package.manifestVersion -cne $msixVersion -or
        -not $packagePath.StartsWith($storePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetExtension($packagePath) -cne '.msix')
    {
        throw "The prepared MSIX report does not describe a Store package for source $sourceVersion (MSIX $msixVersion). Rebuild it."
    }
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$package.sha256)
    {
        throw "The prepared MSIX is missing or no longer matches its recorded SHA-256: $packagePath"
    }
}

$published = Get-StoreSubmission
if ([string]$published.Status -cne 'Published')
{
    throw "Store submission $($published.Id) is pending with status '$($published.Status)'. Finish, submit, or delete it in Partner Center before preparing another."
}
Assert-OlderStorePackages -Submission $published
$mode = if ($Submit) { 'submitted for certification' } else { 'left as a draft for review' }
if ($CheckOnly)
{
    Write-Host "Store plan: source $Version -> MSIX $msixVersion for product $productId; $($ReleaseNotes.Length) release-note characters." -ForegroundColor Green
    Write-Host 'The Store CLI, product access, pending-submission state, and package version ordering passed inspection. Nothing was uploaded.'
    return
}

$packageFileName = [System.IO.Path]::GetFileName($packagePath)
$payloadPath = Join-Path $storeOutputDirectory 'store-submission-payload.json'
Invoke-MsStore @('publish', $packagePath, '--appId', $productId, '--noCommit') | Out-Null
try
{
    $draft = Get-StoreSubmission
    $uploaded = @($draft.ApplicationPackages | Where-Object {
        [string]$_.FileName -ceq $packageFileName -and [string]$_.FileStatus -ne 'PendingDelete'
    })
    if ([string]$draft.Status -cne 'PendingCommit' -or [string]$draft.Id -ceq [string]$published.Id -or $uploaded.Count -ne 1)
    {
        throw "The new draft does not contain exactly one active $packageFileName package."
    }
    Assert-OlderStorePackages -Submission $draft -ExcludeFileName $packageFileName

    $listings = @($draft.Listings.PSObject.Properties)
    if ($listings.Count -eq 0)
    {
        throw 'The draft submission has no listings to carry release notes.'
    }
    foreach ($listing in $listings)
    {
        $listing.Value.BaseListing.ReleaseNotes = $ReleaseNotes
    }
    [System.IO.File]::WriteAllText($payloadPath, ($draft | ConvertTo-Json -Depth 100), [System.Text.UTF8Encoding]::new($false))
    Invoke-MsStore @('submission', 'update', $productId, '--payload', $payloadPath) | Out-Null

    $draft = Get-StoreSubmission
    foreach ($listing in @($draft.Listings.PSObject.Properties))
    {
        if ([string]$listing.Value.BaseListing.ReleaseNotes -cne $ReleaseNotes)
        {
            throw "The draft's $($listing.Name) release notes did not match after the update."
        }
    }
    if ($Submit)
    {
        Invoke-MsStore @('submission', 'publish', $productId) | Out-Null
        $draft = Get-StoreSubmission
    }
}
catch
{
    throw "Store draft preparation stopped. Any draft for product $productId remains in Partner Center for inspection; finish or delete it there before rerunning. $($_.Exception.Message)"
}

$record = [pscustomobject][ordered]@{
    formatVersion = 1
    productId = $productId
    submissionId = [string]$draft.Id
    status = [string]$draft.Status
    submittedForCertification = $Submit.IsPresent
    sourceVersion = $sourceVersion
    manifestVersion = $msixVersion
    packageSha256 = [string]$package.sha256
    recordedUtc = [DateTime]::UtcNow.ToString('o')
}
[System.IO.File]::WriteAllText((Join-Path $storeOutputDirectory 'store-submission.json'),
    ($record | ConvertTo-Json) + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "Store submission $($draft.Id) for MSIX $msixVersion was $mode (status $($draft.Status))." -ForegroundColor Green
Write-Host "Partner Center: https://partner.microsoft.com/dashboard/products/$productId/overview"
