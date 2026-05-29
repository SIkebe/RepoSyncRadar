[CmdletBinding()]
param(
    [string] $TemplatePath = ".github\PULL_REQUEST_TEMPLATE.md",
    [string] $Body,
    [string] $BodyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-AllText {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "File not found: $Path"
    }

    return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $Path))
}

function Get-MarkdownSection {
    param(
        [Parameter(Mandatory = $true)][string] $Markdown,
        [Parameter(Mandatory = $true)][string] $Heading
    )

    $escapedHeading = [regex]::Escape($Heading)
    $pattern = "(?ms)^##\s+$escapedHeading\s*\r?\n(?<content>.*?)(?=^##\s+|\z)"
    $match = [regex]::Match($Markdown, $pattern)
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups["content"].Value.Trim()
}

$template = Read-AllText -Path $TemplatePath

if (-not [string]::IsNullOrWhiteSpace($BodyPath)) {
    $Body = Read-AllText -Path $BodyPath
}
elseif ([string]::IsNullOrWhiteSpace($Body) -and -not [string]::IsNullOrWhiteSpace($env:PR_BODY)) {
    $Body = $env:PR_BODY
}

if ([string]::IsNullOrWhiteSpace($Body)) {
    throw "Pull request body is empty. Use .github\PULL_REQUEST_TEMPLATE.md and resolve all validation items."
}

$requiredHeadings = @(
    [regex]::Matches($template, "(?m)^##\s+(?<heading>.+?)\s*$") |
        ForEach-Object { $_.Groups["heading"].Value.Trim() }
)

$missingHeadings = @(
    foreach ($heading in $requiredHeadings) {
        if (-not [regex]::IsMatch($Body, "(?m)^##\s+$([regex]::Escape($heading))\s*$")) {
            $heading
        }
    }
)

if ($missingHeadings.Count -gt 0) {
    throw "Pull request body is missing template section(s): $($missingHeadings -join ', '). Read .github\PULL_REQUEST_TEMPLATE.md before creating or updating the PR."
}

$validation = Get-MarkdownSection -Markdown $Body -Heading "Validation"
if ([string]::IsNullOrWhiteSpace($validation)) {
    throw "Pull request body must include a populated Validation section."
}

$unresolvedValidationItems = @(
    [regex]::Matches($validation, "(?m)^\s*-\s+\[\s\]\s+(?<item>.+)$") |
        ForEach-Object { $_.Groups["item"].Value.Trim() }
)

if ($unresolvedValidationItems.Count -gt 0) {
    throw "Resolve every Validation checklist item by checking it or replacing it with 'N/A - <reason>': $($unresolvedValidationItems -join '; ')"
}

$summary = Get-MarkdownSection -Markdown $Body -Heading "Summary"
if ([string]::IsNullOrWhiteSpace($summary) -or [regex]::IsMatch($summary, "(?m)^\s*-\s*$")) {
    throw "Pull request body must include a non-placeholder Summary section."
}

Write-Host "Pull request body matches .github\PULL_REQUEST_TEMPLATE.md requirements."
