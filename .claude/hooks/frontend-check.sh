#!/usr/bin/env bash
# Per-edit quality gate for the Angular frontend (strava-segments-performance).
# Invoked by PostToolUse hooks: bash .claude/hooks/frontend-check.sh <format|typecheck|test>
# Reads the hook JSON on stdin, runs the check only for frontend .ts edits,
# and exits 2 (blocking, feedback flows to the agent) when the check fails.
set -uo pipefail

MODE="${1:-}"
ROOT="strava-segments-performance"

# Extract tool_input.file_path from the hook payload without depending on jq.
FILE=$(node -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{try{process.stdout.write((JSON.parse(d).tool_input||{}).file_path||"")}catch(e){process.stdout.write("")}})')

[ -z "$FILE" ] && exit 0

# Windows paths arrive with backslashes; normalize to forward slashes.
FILE_NORM=${FILE//\\//}

# Only act on files inside the Angular frontend project.
# Note: "strava-segments-performance-backend/" does NOT match (no slash after
# "performance"), so backend and *-backend-tests edits are correctly skipped.
case "$FILE_NORM" in
  *"$ROOT"/*) ;;
  *) exit 0 ;;
esac

# Path relative to the frontend project root (what Angular's --include expects).
REL=${FILE_NORM##*"$ROOT"/}

cd "$ROOT" || exit 0

# Run a command; on failure, surface its output to the agent and block (exit 2).
run() {
  local out status
  out=$("$@" 2>&1)
  status=$?
  if [ "$status" -ne 0 ]; then
    printf '%s\n' "$out" >&2
    exit 2
  fi
}

case "$MODE" in
  format)
    case "$REL" in
      *.ts|*.html|*.scss) run npx prettier --write "$REL" ;;
    esac
    ;;
  typecheck)
    case "$REL" in
      *.ts) run npx tsc -p tsconfig.app.json --noEmit ;;
    esac
    ;;
  test)
    case "$REL" in
      # --include with a source path runs its corresponding *.spec.ts through
      # Angular's Vitest+TestBed environment (the "related tests" behavior).
      *.ts) run npx ng test --no-watch --include "$REL" ;;
    esac
    ;;
esac

exit 0
