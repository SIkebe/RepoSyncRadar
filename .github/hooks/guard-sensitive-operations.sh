#!/usr/bin/env bash
set -euo pipefail

input="$(cat)"
if [[ -z "${input//[[:space:]]/}" ]]; then
  exit 0
fi

deny() {
  local reason="$1"
  printf '{"permissionDecision":"deny","permissionDecisionReason":"%s","hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}\n' \
    "$(printf '%s' "$reason" | sed 's/\\/\\\\/g; s/"/\\"/g')" \
    "$(printf '%s' "$reason" | sed 's/\\/\\\\/g; s/"/\\"/g')"
}

tool_name=""
if command -v python3 >/dev/null 2>&1; then
  parsed_payload="$(printf '%s' "$input" | python3 -c '
import json
import sys

def strings(value):
    if value is None:
        return []
    if isinstance(value, str):
        return [value]
    if isinstance(value, dict):
        out = []
        for item in value.values():
            out.extend(strings(item))
        return out
    if isinstance(value, list):
        out = []
        for item in value:
            out.extend(strings(item))
        return out
    return []

try:
    payload = json.load(sys.stdin)
except Exception:
    sys.exit(0)

tool_name = payload.get("toolName") or payload.get("tool_name") or ""
tool_input = payload.get("toolArgs")
if tool_input is None:
    tool_input = payload.get("tool_input")
if isinstance(tool_input, str):
    try:
        tool_input = json.loads(tool_input)
    except Exception:
        pass

print("\n".join([str(tool_name)] + strings(tool_input)))
')"
  tool_name="${parsed_payload%%$'\n'*}"
  haystack="$parsed_payload"
else
  tool_name="$(printf '%s' "$input" | sed -n 's/.*"tool[Nn]ame"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p; s/.*"tool_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
  haystack="$input"
fi

if [[ -n "$tool_name" ]] &&
   ! printf '%s' "$tool_name" | grep -Eiq '(^|[._-])(bash|sh|zsh|fish|pwsh|powershell|cmd|terminal|shell|command)($|[._-])|run_?in_?terminal|run-?in-?terminal'; then
  exit 0
fi

command_prefix='(^|[^[:alnum:]_])'
release_workflow_identifier='(release\.ya?ml|\.github[\\/]workflows[\\/]release\.ya?ml|["'\'']?release["'\'']?|[0-9]+)'
release_create_pattern='(^|[^[:alnum:]_])gh[[:space:]]+release[[:space:]]+create([[:space:]]|$)'
draft_false_pattern='--draft[[:space:]]*=?[[:space:]]*false([^[:alnum:]_]|$)'
draft_true_pattern='--draft([[:space:]]*=[[:space:]]*true)?([^[:alnum:]_]|$)'

if printf '%s' "$haystack" | grep -Eiq "${command_prefix}gh[[:space:]]+pr[[:space:]]+merge([[:space:]]|$)"; then
  deny "PR merges must be initiated by a human after explicit approval."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq "${command_prefix}gh[[:space:]]+workflow[[:space:]]+run([[:space:]][^[:cntrl:]]*)?[[:space:]]${release_workflow_identifier}([[:space:]]|$)"; then
  deny "Release workflow runs require explicit human approval."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq "${command_prefix}gh[[:space:]]+release[[:space:]]+edit[[:space:]][^[:cntrl:]]*--draft[[:space:]]*=?[[:space:]]*false"; then
  deny "Publishing a GitHub Release requires explicit human approval."
  exit 0
fi

while IFS= read -r command_segment || [[ -n "$command_segment" ]]; do
  if [[ "$command_segment" =~ $release_create_pattern ]]; then
    if [[ "$command_segment" =~ $draft_false_pattern ]] ||
       ! [[ "$command_segment" =~ $draft_true_pattern ]]; then
      deny "Publishing a GitHub Release requires explicit human approval."
      exit 0
    fi
  fi
done < <(printf '%s' "$haystack" | tr ';&|' '\n')

if printf '%s' "$haystack" | grep -Eiq "${command_prefix}git[[:space:]]+push[[:space:]][^[:cntrl:]]*(origin[[:space:]]+)?main($|[[:space:]])"; then
  deny "Direct pushes to main are blocked; use a reviewed pull request."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq "${command_prefix}git[[:space:]]+push[[:space:]][^[:cntrl:]]*HEAD:main($|[[:space:]])"; then
  deny "Direct pushes to main are blocked; use a reviewed pull request."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq "${command_prefix}git[[:space:]]+push([^[:cntrl:]]*--force|[^[:cntrl:]]*[[:space:]]-[^[:space:]]*f([^[:alnum:]_-]|$))"; then
  deny "Force pushes are blocked by repository policy."
  exit 0
fi
