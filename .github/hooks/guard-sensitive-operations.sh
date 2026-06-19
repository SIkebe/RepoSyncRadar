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

if command -v python3 >/dev/null 2>&1; then
  haystack="$(printf '%s' "$input" | python3 -c '
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
else
  haystack="$input"
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])gh[[:space:]]+pr[[:space:]]+merge([[:space:]]|$)'; then
  deny "PR merges must be initiated by a human after explicit approval."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])gh[[:space:]]+workflow[[:space:]]+run[[:space:]]+release\.ya?ml([[:space:]]|$)'; then
  deny "Release workflow runs require explicit human approval."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])gh[[:space:]]+release[[:space:]]+edit[[:space:]][^[:cntrl:]]*--draft[[:space:]]*=?[[:space:]]*false'; then
  deny "Publishing a GitHub Release requires explicit human approval."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])gh[[:space:]]+release[[:space:]]+create[[:space:]][^[:cntrl:]]*--draft[[:space:]]*=?[[:space:]]*false'; then
  deny "Publishing a GitHub Release requires explicit human approval."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])git[[:space:]]+push[[:space:]][^[:cntrl:]]*(origin[[:space:]]+)?main($|[[:space:]])'; then
  deny "Direct pushes to main are blocked; use a reviewed pull request."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])git[[:space:]]+push[[:space:]][^[:cntrl:]]*HEAD:main($|[[:space:]])'; then
  deny "Direct pushes to main are blocked; use a reviewed pull request."
  exit 0
fi

if printf '%s' "$haystack" | grep -Eiq '(^|[;&|[:space:]])git[[:space:]]+push[[:space:]][^[:cntrl:]]*--force'; then
  deny "Force pushes are blocked by repository policy."
  exit 0
fi
