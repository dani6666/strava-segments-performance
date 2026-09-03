#!/bin/sh
# oauth-redirect-smoke.sh — deploy-time OAuth redirect smoke (test-plan.md §5, Risk #2).
#
# Asserts the one thing no offline test can see: the live public GET /auth/login
# builds an https://<prod-host>/auth/callback redirect_uri — i.e. nginx's forwarded
# proto/host are flowing and TLS terminates as expected (the "fixing https forwarding"
# failure class). Gated on the just-deployed build SHA so it cannot pass against a
# stale revision.
#
# Usage: oauth-redirect-smoke.sh <base-url> <expected-sha>
# POSIX sh + curl + awk. No secrets. Side-effect-free: the 302 is generated before Strava
# is contacted, and the throwaway correlation cookie is discarded.
set -eu

BASE="${1:-}"
EXPECTED_SHA="${2:-}"

if [ -z "$BASE" ] || [ -z "$EXPECTED_SHA" ]; then
  echo "usage: $0 <base-url> <expected-sha>" >&2
  exit 2
fi

# Normalize: drop a trailing slash so "$BASE/health" never doubles up.
BASE="${BASE%/}"

# --- 1. Poll /health until it reports the expected SHA (bounded) -------------
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-180}"
POLL_INTERVAL="${POLL_INTERVAL:-5}"
DEADLINE=$(( $(date +%s) + HEALTH_TIMEOUT ))

echo "Polling $BASE/health for sha=$EXPECTED_SHA (timeout ${HEALTH_TIMEOUT}s)..."
while :; do
  BODY=$(curl -fsS "$BASE/health" 2>/dev/null || true)
  SHA=$(printf '%s' "$BODY" | sed -n 's/.*"sha":"\([^"]*\)".*/\1/p')
  if [ "$SHA" = "$EXPECTED_SHA" ]; then
    echo "  /health reports expected sha: $SHA"
    break
  fi
  if [ "$(date +%s)" -ge "$DEADLINE" ]; then
    echo "FAIL: timed out waiting for /health to report sha=$EXPECTED_SHA (last saw: '${SHA:-<none>}')" >&2
    exit 1
  fi
  sleep "$POLL_INTERVAL"
done

# --- 2. Hit /auth/login and capture the 302 + Location header ----------------
echo "Requesting $BASE/auth/login (expecting 302 to the OAuth authorize endpoint)..."
HEADERS=$(curl -sS -D - -o /dev/null "$BASE/auth/login")

STATUS=$(printf '%s\n' "$HEADERS" | awk 'NR==1{print $2; exit}')
if [ "$STATUS" != "302" ]; then
  echo "FAIL: expected 302 from /auth/login, got '$STATUS'" >&2
  exit 1
fi

LOCATION=$(printf '%s\n' "$HEADERS" | grep -i '^location:' | head -n1 \
  | sed 's/^[Ll]ocation:[[:space:]]*//' | tr -d '\r')
if [ -z "$LOCATION" ]; then
  echo "FAIL: /auth/login returned 302 with no Location header" >&2
  exit 1
fi

# --- 3. Extract + URL-decode the redirect_uri query param --------------------
ENCODED=$(printf '%s' "$LOCATION" | sed -n 's/.*[?&]redirect_uri=\([^&]*\).*/\1/p')
if [ -z "$ENCODED" ]; then
  echo "FAIL: no redirect_uri param in Location: $LOCATION" >&2
  exit 1
fi

# URL-decode the redirect_uri with plain sed. A redirect_uri only ever carries a
# fixed, small set of encoded chars (: / ? = &, plus + -> space), so literal swaps
# cover it — and unlike `printf %b`, sed behaves the same under dash (the /bin/sh
# this runs on in CI) and bash. Hex digits are matched case-insensitively.
DECODED=$(printf '%s' "$ENCODED" | sed \
  's/+/ /g; s/%3[Aa]/:/g; s/%2[Ff]/\//g; s/%3[Ff]/?/g; s/%3[Dd]/=/g; s/%26/\&/g')

# --- 4. Assert scheme https + expected public host + /auth/callback path -----
EXPECTED_PREFIX="$BASE/auth/callback"
case "$DECODED" in
  http://*)
    echo "FAIL: redirect_uri uses insecure http scheme: $DECODED" >&2
    exit 1
    ;;
  *localhost*|*.railway.internal*|*railway.internal*)
    echo "FAIL: redirect_uri points at a non-public host: $DECODED" >&2
    exit 1
    ;;
  "$EXPECTED_PREFIX"|"$EXPECTED_PREFIX"\?*)
    echo "OK: redirect_uri = $DECODED"
    exit 0
    ;;
  *)
    echo "FAIL: redirect_uri '$DECODED' does not start with '$EXPECTED_PREFIX'" >&2
    exit 1
    ;;
esac
