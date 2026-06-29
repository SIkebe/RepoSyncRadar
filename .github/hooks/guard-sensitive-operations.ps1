Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Copilot PreToolUse sends one JSON payload on stdin. This hook reads that
# payload, extracts shell-command strings, and denies sensitive repo operations.
$inputText = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($inputText)) {
    exit 0
}

# Parse a JSON string when possible. Invalid hook input is ignored so the hook
# does not block unrelated tool calls because of malformed metadata.
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

# Support both Copilot CLI camelCase and VS Code snake_case payload fields.
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

# Flatten nested tool input into strings so command arguments can be scanned no
# matter whether the host sends them as objects, arrays, or JSON-encoded text.
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

    # Emit both CLI-style and VS Code-style deny fields for shared hook support.
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

# PreToolUse fires for all tools; only inspect actual shell/terminal tools.
function Test-IsCommandTool {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $true
    }

    return $Name -match '(?i)(^|[._-])(bash|sh|zsh|fish|pwsh|powershell|cmd|terminal|shell|command)($|[._-])|run_?in_?terminal|run-?in-?terminal'
}

# Build a command-search haystack from the tool name and all string inputs.
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

# `gh release create` publishes by default, so require an explicit draft create.
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

foreach ($commandSegment in ($haystack -split '[;&|\r\n]')) {
    # Block direct pushes to main, but only inspect the git-push command segment.
    # A later command such as `gh pr create --base main` must not be treated as
    # the push destination.
    if ($commandSegment -match '(?im)(^|[^A-Za-z0-9_])git(?:\s+[^\s]+)*\s+push\b(?:\s+[^\s]+)*\s+(?:origin\s+)?(?:main|refs/heads/main)(?:$|\s)') {
        New-DenyOutput -Reason 'Direct pushes to main are blocked; use a reviewed pull request.'
        exit 0
    }

    # Block explicit refspec pushes to main.
    if ($commandSegment -match '(?im)(^|[^A-Za-z0-9_])git(?:\s+[^\s]+)*\s+push\b[^\r\n]*\b(?:HEAD|\+?refs/heads/[^:\s]+|\+?[^:\s]+):(?:main|refs/heads/main)(?:$|\s)') {
        New-DenyOutput -Reason 'Direct pushes to main are blocked; use a reviewed pull request.'
        exit 0
    }

    # Block force pushes via long or short flags, with or without git global options.
    if ($commandSegment -match '(?im)(^|[^A-Za-z0-9_])git(?:\s+[^\s]+)*\s+push\b[^\r\n]*((^|\s)-[^\s]*f([^\w-]|$)|--force)') {
        New-DenyOutput -Reason 'Force pushes are blocked by repository policy.'
        exit 0
    }
}

# Deny rules below use non-word command boundaries so quoted, parenthesized, or
# shell-expanded commands are detected consistently with the bash hook.
$rules = @(
    # PR merges must remain a human action.
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])gh\s+pr\s+merge(\s|$)'; Reason = 'PR merges must be initiated by a human after explicit approval.' },
    # Block Release workflow dispatch by file name, display name, path, or ID.
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])gh\s+workflow\s+run(?:\s+[^\r\n]*)?\s+(?:release\.ya?ml|\.github[\\/]workflows[\\/]release\.ya?ml|["'']?release["'']?|\d+)($|[^A-Za-z0-9_])'; Reason = 'Release workflow runs require explicit human approval.' },
    # Publishing an existing draft release is sensitive; draft edits remain allowed.
    @{ Pattern = '(?im)(^|[^A-Za-z0-9_])gh\s+release\s+edit\b.*--draft\s*=?\s*false\b'; Reason = 'Publishing a GitHub Release requires explicit human approval.' }
)

foreach ($rule in $rules) {
    if ($haystack -match $rule.Pattern) {
        New-DenyOutput -Reason $rule.Reason
        exit 0
    }
}

exit 0
