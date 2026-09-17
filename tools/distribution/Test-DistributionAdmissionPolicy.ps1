[CmdletBinding()]
param(
    [string]$PolicyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath))
{
    $PolicyPath = Join-Path $scriptDirectory 'DistributionAdmissionPolicy.json'
}

. (Join-Path $scriptDirectory 'DistributionAdmissionPolicy.ps1')
$policy = Get-DistributionAdmissionPolicy -Path $PolicyPath

function Assert-PolicyCase
{
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [Parameter(Mandatory)][ValidateSet('admitted', 'unresolved', 'blocked')][string]$ExpectedStatus,
        [AllowNull()][string]$ExpectedSelected,
        [int]$ExpectedSelectionCount = -1
    )

    $decision = Get-DistributionLicenseDecision -LicenseExpression $Expression -Policy $policy
    if ($decision.admissionStatus -ne $ExpectedStatus)
    {
        throw "Policy self-validation expected '$ExpectedStatus' for '$Expression', received '$($decision.admissionStatus)'."
    }
    if ($PSBoundParameters.ContainsKey('ExpectedSelected') -and $decision.selectedLicenseExpression -ne $ExpectedSelected)
    {
        throw "Policy self-validation expected selected expression '$ExpectedSelected' for '$Expression', received '$($decision.selectedLicenseExpression)'."
    }
    if ($ExpectedSelectionCount -ge 0 -and @($decision.selectedOrBranches).Count -ne $ExpectedSelectionCount)
    {
        throw "Policy self-validation expected $ExpectedSelectionCount selected OR branches for '$Expression', received $(@($decision.selectedOrBranches).Count)."
    }
}

Assert-PolicyCase -Expression 'MIT' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT'
Assert-PolicyCase -Expression '  MIT  ' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT'
Assert-PolicyCase -Expression 'MIT OR Apache-2.0' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT' -ExpectedSelectionCount 1
Assert-PolicyCase -Expression 'Apache-2.0 OR MIT' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT' -ExpectedSelectionCount 1
Assert-PolicyCase -Expression 'GPL-3.0-only OR MIT' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT' -ExpectedSelectionCount 1
Assert-PolicyCase -Expression 'Unknown-License OR MIT' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT' -ExpectedSelectionCount 1
Assert-PolicyCase -Expression 'MIT AND Apache-2.0' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT AND Apache-2.0'
Assert-PolicyCase -Expression '(Apache-2.0 OR MIT) AND BSD-3-Clause' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT AND BSD-3-Clause' -ExpectedSelectionCount 1
Assert-PolicyCase -Expression 'MIT AND (GPL-3.0-only OR Apache-2.0)' -ExpectedStatus 'admitted' -ExpectedSelected 'MIT AND Apache-2.0' -ExpectedSelectionCount 1
Assert-PolicyCase -Expression 'Apache-2.0 WITH LLVM-exception' -ExpectedStatus 'admitted' -ExpectedSelected 'Apache-2.0 WITH LLVM-exception'
Assert-PolicyCase -Expression 'MIT AND Unknown-License' -ExpectedStatus 'unresolved'
Assert-PolicyCase -Expression 'GPL-3.0-only' -ExpectedStatus 'blocked'
Assert-PolicyCase -Expression 'GPL-3.0-only OR LGPL-3.0-only' -ExpectedStatus 'blocked'
Assert-PolicyCase -Expression 'Commons-Clause-1.0' -ExpectedStatus 'blocked'
Assert-PolicyCase -Expression 'MIT/Apache-2.0' -ExpectedStatus 'unresolved'
Assert-PolicyCase -Expression '' -ExpectedStatus 'unresolved'

$missingMetadata = Get-DistributionMetadataDecision -LicenseExpression $null -LicenseEvidence 'No retained metadata.' -Policy $policy -DependencyCategory 'NuGet'
if ($missingMetadata.admissionStatus -ne 'unresolved' -or
    @($missingMetadata.diagnostics | Where-Object { $_ -match 'not an automatic policy rejection' }).Count -ne 1)
{
    throw 'Policy self-validation must classify missing metadata as unresolved rather than rejected.'
}

$authoritativeAsset = Get-DistributionMetadataDecision -LicenseExpression $null -LicenseEvidence 'Retained authoritative asset terms.' -Policy $policy -DependencyCategory 'ICC' -AuthoritativeNonSpdxEvidence
if ($authoritativeAsset.admissionStatus -ne 'admitted' -or
    $authoritativeAsset.selectedLicenseExpression -ne 'LicenseRef-Authoritative-Retained-Text')
{
    throw 'Policy self-validation must admit explicitly identified authoritative non-SPDX asset evidence.'
}

$expectedCategories = @('NuGet', 'npm', 'Cargo', 'native-vendored', 'fonts', 'ICC', 'assets')
$actualCategories = @($policy.dependencyCategories | ForEach-Object { [string]$_.id })
if (@(Compare-Object -ReferenceObject $expectedCategories -DifferenceObject $actualCategories).Count -ne 0)
{
    throw 'Policy self-validation found a missing or unexpected dependency category.'
}

Write-Host "Distribution admission policy self-validation passed ($($expectedCategories.Count) categories, 17 SPDX cases)."
