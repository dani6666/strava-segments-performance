#!/usr/bin/env bash
# Pre-commit gate: run the Angular specs related to the staged frontend files.
# Invoked by Lefthook:  bash .claude/hooks/precommit-frontend-tests.sh {staged_files}
# Each staged path is repo-root-relative; we strip the project prefix and pass
# it to `ng test --include`, which runs the corresponding *.spec.ts (or the spec
# itself) through Angular's Vitest+TestBed environment. Multiple files -> one run.
set -uo pipefail

ROOT="strava-segments-performance"
includes=()

for f in "$@"; do
  f=${f//\\//}                          # normalize Windows backslashes
  case "$f" in
    "$ROOT"/e2e/*) ;;                    # Playwright specs -> npx playwright test, not ng test
    "$ROOT"/*.ts) includes+=( "--include" "${f#"$ROOT"/}" ) ;;
  esac
done

# No frontend .ts files staged -> nothing to test, let the commit proceed.
[ ${#includes[@]} -eq 0 ] && exit 0

cd "$ROOT" || exit 1
exec npx ng test --no-watch "${includes[@]}"
