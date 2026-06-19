Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$inputText = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($inputText)) {
    exit 0
}

function Convert-ToJsonOrNull {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    try {
        return $Text | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Get-PropertyValue {
    param(
        [object]$Object,
        [string[]]$Names
    )

    if ($null -eq $Object) {
        return $null
    }

    foreach ($name in $Names) {
        if ($Object.PSObject.Properties.Name -contains $name) {
            return $Object.$name
        }
    }

    return $null
}

function Get-StringValues {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [string]) {
        return @($Value)
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $items = @()
        foreach ($key in $Value.Keys) {
            $items += Get-StringValues -Value $Value[$key]
        }

        return $items
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = @()
        foreach ($item in $Value) {
            $items += Get-StringValues -Value $item
        }

        return $items
    }

    if (@($Value.PSObject.Properties).Count -gt 0) {
        $items = @()
        foreach ($property in $Value.PSObject.Properties) {
            $items += Get-StringValues -Value $property.Value
        }

        return $items
    }

    return @()
}

function New-DenyOutput {
    param([string]$Reason)

    [ordered]@{
        permissionDecision = 'deny'
        permissionDecisionReason = $Reason
        hookSpecificOutput = [ordered]@{
            hookEventName = 'PreToolUse'
            permissionDecision = 'deny'
            permissionDecisionReason = $Reason
        }
    } | ConvertTo-Json -Compress -Depth 5
}

function Test-IsCommandTool {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $true
    }

    return $Name -match '(?i)(^|[._-])(bash|sh|zsh|fish|pwsh|powershell|cmd|terminal|shell|command)($|[._-])|run_?in_?terminal|run-?in-?terminal'
}

$inputObj = Convert-ToJsonOrNull -Text $inputText
if ($null -eq $inputObj) {
    exit 0
}

$toolName = [string](Get-PropertyValue -Object $inputObj -Names @('toolName', 'tool_name'))
if (-not (Test-IsCommandTool -Name $toolName)) {
    exit 0
}

$toolInput = Get-PropertyValue -Object $inputObj -Names @('toolArgs', 'tool_input')
if ($toolInput -is [string]) {
    $parsedToolInput = Convert-ToJsonOrNull -Text $toolInput
    if ($null -ne $parsedToolInput) {
        $toolInput = $parsedToolInput
    }
}

$haystackParts = @($toolName) + (Get-StringValues -Value $toolInput)
$haystack = ($haystackParts -join "`n")

foreach ($commandSegment in ($haystack -split '[;&|\r\n]')) {
    if ($commandSegment -match '(?i)(^|[^A-Za-z0-9_])gh\s+release\s+create(\s|$)') {
        $isExplicitPublish = $commandSegment -match '(?i)--draft\s*=?\s*false\b'
        $isDraftCreate = $commandSegment -match '(?i)--draft(\s*=\s*true)?([^A-Za-z0-9_]|$)'
        if ($isExplicitPublish -or -not $isDraftCreate) {
            New-DenyOutput -Reason 'Publishing a GitHub Release requires explicit human approval.'
            exit 0
        }
    }
}

$rules = @(
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])gh\s+pr\s+merge(\s|$)'; Reason = 'PR merges must be initiated by a human after explicit approval.' },
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])gh\s+workflow\s+run(?:\s+[^\r\n]*)?\s+(?:release\.ya?ml|\.github[\\/]workflows[\\/]release\.ya?ml|["'']?release["'']?|\d+)($|[^A-Za-z0-9_])'; Reason = 'Release workflow runs require explicit human approval.' },
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])gh\s+release\s+edit\b.*--draft\s*=?\s*false\b'; Reason = 'Publishing a GitHub Release requires explicit human approval.' },
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])git(?:\s+[^\s]+)*\s+push\b(?:\s+[^\s]+)*\s+(?:origin\s+)?main(?:$|\s)'; Reason = 'Direct pushes to main are blocked; use a reviewed pull request.' },
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])git(?:\s+[^\s]+)*\s+push\b[^\r\n]*\bHEAD:main(?:$|\s)'; Reason = 'Direct pushes to main are blocked; use a reviewed pull request.' },
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])git(?:\s+[^\s]+)*\s+push\b[^\r\n]*((^|\s)-[^\s]*f([^\w-]|$)|--force)'; Reason = 'Force pushes are blocked by repository policy.' }
)

foreach ($rule in $rules) {
    if ($haystack -match $rule.Pattern) {
        New-DenyOutput -Reason $rule.Reason
        exit 0
    }
}

exit 0
