Set-StrictMode -Version Latest

function Get-DistributionAdmissionPolicy
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Distribution admission policy is missing: $Path"
    }

    $policy = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($policy.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$policy.policyId))
    {
        throw "Unsupported distribution admission policy: $Path"
    }

    $categoryIds = @($policy.dependencyCategories | ForEach-Object { [string]$_.id })
    $expectedCategoryIds = @('NuGet', 'npm', 'Cargo', 'native-vendored', 'fonts', 'ICC', 'assets')
    if (@(Compare-Object -ReferenceObject $expectedCategoryIds -DifferenceObject $categoryIds).Count -ne 0)
    {
        throw 'Distribution admission policy must define every supported dependency category.'
    }

    return $policy
}

function Get-SpdxTokens
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Expression
    )

    $tokens = [System.Collections.Generic.List[string]]::new()
    $position = 0
    $tokenizer = [regex]::new('\G\s*(\(|\)|AND\b|OR\b|WITH\b|[^\s()]+)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    while ($position -lt $Expression.Length)
    {
        $match = $tokenizer.Match($Expression, $position)
        if (-not $match.Success)
        {
            throw "Invalid SPDX expression near offset $position."
        }

        $token = $match.Groups[1].Value
        $tokens.Add($token)
        $position += $match.Length
    }

    return @($tokens)
}

function ConvertTo-SpdxExpressionAst
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Expression
    )

    $tokens = @(Get-SpdxTokens -Expression $Expression)
    if ($tokens.Count -eq 0)
    {
        throw 'An SPDX expression cannot be empty.'
    }

    $position = 0
    function Read-SpdxPrimary
    {
        param([object[]]$InputTokens, [ref]$InputPosition)

        if ($InputPosition.Value -ge $InputTokens.Count)
        {
            throw 'Expected an SPDX license identifier.'
        }

        $token = [string]$InputTokens[$InputPosition.Value]
        if ($token -eq '(')
        {
            $InputPosition.Value++
            $node = Read-SpdxOr -InputTokens $InputTokens -InputPosition $InputPosition
            if ($InputPosition.Value -ge $InputTokens.Count -or [string]$InputTokens[$InputPosition.Value] -ne ')')
            {
                throw 'An SPDX expression has an unclosed parenthesis.'
            }
            $InputPosition.Value++
            return [pscustomobject][ordered]@{
                kind = 'group'
                expression = $node
            }
        }

        if ($token -in @(')', 'AND', 'OR', 'WITH'))
        {
            throw "Unexpected SPDX operator '$token'."
        }

        $InputPosition.Value++
        return [pscustomobject][ordered]@{
            kind = 'license'
            id = $token
        }
    }

    function Read-SpdxWith
    {
        param([object[]]$InputTokens, [ref]$InputPosition)

        $left = Read-SpdxPrimary -InputTokens $InputTokens -InputPosition $InputPosition
        if ($InputPosition.Value -lt $InputTokens.Count -and
            [string]$InputTokens[$InputPosition.Value] -ieq 'WITH')
        {
            $InputPosition.Value++
            if ($InputPosition.Value -ge $InputTokens.Count)
            {
                throw 'Expected an SPDX exception after WITH.'
            }

            $exception = [string]$InputTokens[$InputPosition.Value]
            if ($exception -in @('(', ')', 'AND', 'OR', 'WITH'))
            {
                throw 'Expected an SPDX exception identifier after WITH.'
            }

            $InputPosition.Value++
            return [pscustomobject][ordered]@{
                kind = 'with'
                license = $left
                exception = $exception
            }
        }

        return $left
    }

    function Read-SpdxAnd
    {
        param([object[]]$InputTokens, [ref]$InputPosition)

        $left = Read-SpdxWith -InputTokens $InputTokens -InputPosition $InputPosition
        while ($InputPosition.Value -lt $InputTokens.Count -and
            [string]$InputTokens[$InputPosition.Value] -ieq 'AND')
        {
            $InputPosition.Value++
            $right = Read-SpdxWith -InputTokens $InputTokens -InputPosition $InputPosition
            $left = [pscustomobject][ordered]@{
                kind = 'and'
                left = $left
                right = $right
            }
        }

        return $left
    }

    function Read-SpdxOr
    {
        param([object[]]$InputTokens, [ref]$InputPosition)

        $left = Read-SpdxAnd -InputTokens $InputTokens -InputPosition $InputPosition
        while ($InputPosition.Value -lt $InputTokens.Count -and
            [string]$InputTokens[$InputPosition.Value] -ieq 'OR')
        {
            $InputPosition.Value++
            $right = Read-SpdxAnd -InputTokens $InputTokens -InputPosition $InputPosition
            $left = [pscustomobject][ordered]@{
                kind = 'or'
                left = $left
                right = $right
            }
        }

        return $left
    }

    $ast = Read-SpdxOr -InputTokens $tokens -InputPosition ([ref]$position)
    if ($position -ne $tokens.Count)
    {
        throw "Unexpected SPDX token '$($tokens[$position])'."
    }

    return $ast
}

function ConvertFrom-SpdxExpressionAst
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Node,
        [switch]$Parenthesize
    )

    $text = switch ($Node.kind)
    {
        'license' { [string]$Node.id }
        'group' { '(' + (ConvertFrom-SpdxExpressionAst -Node $Node.expression) + ')' }
        'with' { (ConvertFrom-SpdxExpressionAst -Node $Node.license) + ' WITH ' + [string]$Node.exception }
        'and' { (ConvertFrom-SpdxExpressionAst -Node $Node.left) + ' AND ' + (ConvertFrom-SpdxExpressionAst -Node $Node.right) }
        'or' { (ConvertFrom-SpdxExpressionAst -Node $Node.left) + ' OR ' + (ConvertFrom-SpdxExpressionAst -Node $Node.right) }
        default { throw "Unknown SPDX AST node kind '$($Node.kind)'." }
    }

    if ($Parenthesize -and $Node.kind -in @('and', 'or'))
    {
        return '(' + $text + ')'
    }

    return $text
}

function Find-DistributionLicenseRule
{
    param(
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][string]$LicenseId
    )

    $admissible = @($Policy.admissibleSpdxLicenses | Where-Object { [string]$_.id -ieq $LicenseId })
    if ($admissible.Count -gt 0)
    {
        return [pscustomobject][ordered]@{
            state = 'admitted'
            id = [string]$admissible[0].id
            restrictionClasses = @($admissible[0].restrictionClasses)
        }
    }

    $restricted = @($Policy.restrictedSpdxLicenses | Where-Object { [string]$_.id -ieq $LicenseId })
    if ($restricted.Count -gt 0)
    {
        return [pscustomobject][ordered]@{
            state = 'blocked'
            id = [string]$restricted[0].id
            restrictionClasses = @($restricted[0].restrictionClasses)
        }
    }

    $prefix = @($Policy.restrictedLicensePrefixes | Where-Object { $LicenseId.StartsWith([string]$_.prefix, [System.StringComparison]::OrdinalIgnoreCase) }) | Select-Object -First 1
    if ($null -ne $prefix)
    {
        return [pscustomobject][ordered]@{
            state = 'blocked'
            id = $LicenseId
            restrictionClasses = @($prefix.restrictionClasses)
        }
    }

    return [pscustomobject][ordered]@{
        state = 'unresolved'
        id = $LicenseId
        restrictionClasses = @()
    }
}

function Get-DistributionLicenseRank
{
    param(
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][string]$LicenseId
    )

    $index = 0
    foreach ($preferredLicense in @($Policy.licenseSelectionPreference))
    {
        if ([string]$preferredLicense -ieq $LicenseId)
        {
            return $index
        }
        $index++
    }

    return [int]::MaxValue
}

function Merge-DistributionOrSelections
{
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Left,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Right
    )

    return @($Left) + @($Right)
}

function Invoke-DistributionLicenseNode
{
    param(
        [Parameter(Mandatory)][object]$Node,
        [Parameter(Mandatory)][object]$Policy
    )

    switch ($Node.kind)
    {
        'license'
        {
            $rule = Find-DistributionLicenseRule -Policy $Policy -LicenseId ([string]$Node.id)
            return [pscustomobject][ordered]@{
                state = $rule.state
                selected = if ($rule.state -eq 'admitted') { [string]$rule.id } else { $null }
                restrictions = @($rule.restrictionClasses)
                diagnostics = if ($rule.state -eq 'unresolved') { @("No exact policy rule is recorded for SPDX license '$($Node.id)'.") } elseif ($rule.state -eq 'blocked') { @("SPDX license '$($Node.id)' has prohibited restriction classes: $($rule.restrictionClasses -join ', ').") } else { @() }
                orSelections = @()
            }
        }
        'group'
        {
            return Invoke-DistributionLicenseNode -Node $Node.expression -Policy $Policy
        }
        'with'
        {
            $base = Invoke-DistributionLicenseNode -Node $Node.license -Policy $Policy
            if ($base.state -ne 'admitted')
            {
                return $base
            }

            $exception = @($Policy.admissibleSpdxExceptions | Where-Object { [string]$_.id -ieq [string]$Node.exception })
            if ($exception.Count -eq 0)
            {
                return [pscustomobject][ordered]@{
                    state = 'unresolved'
                    selected = $null
                    restrictions = @()
                    diagnostics = @("SPDX exception '$($Node.exception)' is not admitted by this policy.")
                    orSelections = @()
                }
            }

            return [pscustomobject][ordered]@{
                state = 'admitted'
                selected = (ConvertFrom-SpdxExpressionAst -Node $Node)
                restrictions = @()
                diagnostics = @()
                orSelections = @($base.orSelections)
            }
        }
        'and'
        {
            $left = Invoke-DistributionLicenseNode -Node $Node.left -Policy $Policy
            $right = Invoke-DistributionLicenseNode -Node $Node.right -Policy $Policy
            $states = @($left.state, $right.state)
            $state = if ($states -contains 'blocked') { 'blocked' } elseif ($states -contains 'unresolved') { 'unresolved' } else { 'admitted' }
            return [pscustomobject][ordered]@{
                state = $state
                selected = if ($state -eq 'admitted') { "$($left.selected) AND $($right.selected)" } else { $null }
                restrictions = @($left.restrictions) + @($right.restrictions)
                diagnostics = @($left.diagnostics) + @($right.diagnostics)
                orSelections = Merge-DistributionOrSelections -Left @($left.orSelections) -Right @($right.orSelections)
            }
        }
        'or'
        {
            $left = Invoke-DistributionLicenseNode -Node $Node.left -Policy $Policy
            $right = Invoke-DistributionLicenseNode -Node $Node.right -Policy $Policy
            $candidates = @(
                [pscustomobject]@{ side = 'left'; result = $left; rank = if ($left.state -eq 'admitted') { Get-DistributionLicenseRank -Policy $Policy -LicenseId $left.selected } else { [int]::MaxValue } }
                [pscustomobject]@{ side = 'right'; result = $right; rank = if ($right.state -eq 'admitted') { Get-DistributionLicenseRank -Policy $Policy -LicenseId $right.selected } else { [int]::MaxValue } }
            )
            $selectedCandidate = $candidates | Where-Object { $_.result.state -eq 'admitted' } | Sort-Object rank, side | Select-Object -First 1
            $expression = ConvertFrom-SpdxExpressionAst -Node $Node
            if ($null -ne $selectedCandidate)
            {
                $selectedResult = $selectedCandidate.result
                $selection = [pscustomobject][ordered]@{
                    expression = $expression
                    selected = [string]$selectedResult.selected
                    alternatives = @($left.selected, $right.selected | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
                }
                return [pscustomobject][ordered]@{
                    state = 'admitted'
                    selected = [string]$selectedResult.selected
                    restrictions = @($selectedResult.restrictions)
                    diagnostics = @()
                    orSelections = @($left.orSelections) + @($right.orSelections) + @($selection)
                }
            }

            $state = if (@($left.state, $right.state) -contains 'blocked' -and
                @($left.state, $right.state) -notcontains 'unresolved') { 'blocked' } else { 'unresolved' }
            return [pscustomobject][ordered]@{
                state = $state
                selected = $null
                restrictions = @($left.restrictions) + @($right.restrictions)
                diagnostics = @($left.diagnostics) + @($right.diagnostics)
                orSelections = @($left.orSelections) + @($right.orSelections)
            }
        }
        default { throw "Unknown SPDX AST node kind '$($Node.kind)'." }
    }
}

function Get-DistributionLicenseDecision
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$LicenseExpression,
        [Parameter(Mandatory)][object]$Policy
    )

    if ([string]::IsNullOrWhiteSpace($LicenseExpression))
    {
        return [pscustomobject][ordered]@{
            admissionStatus = 'unresolved'
            licenseExpression = $null
            selectedLicenseExpression = $null
            selectedOrBranches = @()
            restrictionClasses = @()
            diagnostics = @('No machine-readable SPDX expression was supplied; authoritative license provenance remains unresolved.')
        }
    }

    $normalizedExpression = $LicenseExpression.Trim()
    try
    {
        $ast = ConvertTo-SpdxExpressionAst -Expression $normalizedExpression
        $result = Invoke-DistributionLicenseNode -Node $ast -Policy $Policy
        return [pscustomobject][ordered]@{
            admissionStatus = [string]$result.state
            licenseExpression = $normalizedExpression
            selectedLicenseExpression = $result.selected
            selectedOrBranches = @($result.orSelections)
            restrictionClasses = @($result.restrictions | Select-Object -Unique)
            diagnostics = @($result.diagnostics | Select-Object -Unique)
        }
    }
    catch
    {
        return [pscustomobject][ordered]@{
            admissionStatus = 'unresolved'
            licenseExpression = $normalizedExpression
            selectedLicenseExpression = $null
            selectedOrBranches = @()
            restrictionClasses = @()
            diagnostics = @("SPDX expression parsing failed closed: $($_.Exception.Message)")
        }
    }
}

function Get-DistributionMetadataDecision
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$LicenseExpression,
        [Parameter(Mandatory)][AllowNull()][string]$LicenseEvidence,
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][ValidateSet('NuGet', 'npm', 'Cargo', 'native-vendored', 'fonts', 'ICC', 'assets')][string]$DependencyCategory,
        [switch]$AuthoritativeNonSpdxEvidence
    )

    if ([string]::IsNullOrWhiteSpace($LicenseExpression))
    {
        if ($AuthoritativeNonSpdxEvidence -and -not [string]::IsNullOrWhiteSpace($LicenseEvidence))
        {
            return [pscustomobject][ordered]@{
                admissionStatus = 'admitted'
                licenseExpression = $null
                selectedLicenseExpression = 'LicenseRef-Authoritative-Retained-Text'
                selectedOrBranches = @()
                restrictionClasses = @()
                diagnostics = @('Admission uses retained authoritative non-SPDX redistribution terms for this exact asset; preserve that evidence with the distribution record.')
            }
        }

        return [pscustomobject][ordered]@{
            admissionStatus = 'unresolved'
            licenseExpression = $null
            selectedLicenseExpression = $null
            selectedOrBranches = @()
            restrictionClasses = @()
            diagnostics = @('License metadata is missing or is not an SPDX expression; this is unresolved provenance, not an automatic policy rejection.')
        }
    }

    # The exact legacy Cargo spelling is documented by the crates themselves.
    # Do not interpret arbitrary slash-separated or non-SPDX metadata as OR.
    $normalizedExpression = if ($DependencyCategory -eq 'Cargo' -and $LicenseExpression -eq 'MIT/Apache-2.0') { 'MIT OR Apache-2.0' } else { $LicenseExpression }
    $decision = Get-DistributionLicenseDecision -LicenseExpression $normalizedExpression -Policy $Policy
    if ($decision.admissionStatus -eq 'admitted' -and [string]::IsNullOrWhiteSpace($LicenseEvidence))
    {
        $decision.admissionStatus = 'unresolved'
        $decision.diagnostics = @($decision.diagnostics) + 'The SPDX expression has no retained evidence source.'
    }

    return $decision
}

function Get-DistributionRetainedPackageDecision
{
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PackageIdentity,
        [Parameter(Mandatory)][object]$Policy,
        [Parameter(Mandatory)][object]$Manifest,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )
    $rule = @($Policy.retainedPackageDecisions | Where-Object { $_.package -ceq $PackageIdentity })
    $record = @($Manifest.packages | Where-Object { $_.package -ceq $PackageIdentity })
    if ($rule.Count -ne 1 -or $record.Count -ne 1) { return $null }
    $evidence = @($record[0].evidence | Where-Object { $_.sha256 -ceq $rule[0].evidenceSha256 })
    if ($evidence.Count -ne 1) { return $null }
    $path = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $evidence[0].path))
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'licenses/third-party')) + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $rule[0].evidenceSha256) { return $null }
    return [pscustomobject][ordered]@{
        admissionStatus = 'admitted'; licenseExpression = [string]$rule[0].licenseExpression
        selectedLicenseExpression = [string]$rule[0].licenseExpression; selectedOrBranches = @()
        restrictionClasses = @(); diagnostics = @([string]$rule[0].scope)
    }
}
