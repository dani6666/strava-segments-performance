<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: OAuth Handshake Round-Trip Tests (Risk #2)

- **Plan**: context/changes/testing-oauth-roundtrip/plan.md
- **Scope**: All phases (1–5 of 5)
- **Date**: 2026-09-03
- **Verdict**: APPROVED
- **Findings**: 0 critical, 0 warnings, 4 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | PASS |
| Architecture | PASS |
| Pattern Consistency | PASS |
| Success Criteria | PASS |

## Findings

### F1 — Program.cs stub-athlete comment misstates the claim mapping

- **Severity**: 🟦 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: strava-segments-performance-backend/Program.cs:244
- **Detail**: The comment reads "the provider maps id -> NameIdentifier and firstname -> Name". The actual provider maps the Name claim from `username`, not `firstname` — which is why the stub returns `username = "e2e_rider"` and the handshake spec asserts `displayName: 'e2e_rider'` (and the spec's own comment at oauth-handshake.spec.ts:15-16 documents this correctly). The stub still sets `firstname = "E2E"`, which the misleading comment implies drives DisplayName; it does not. Cosmetic only — behavior is correct and green.
- **Fix**: Change "firstname -> Name" to "username -> Name" in the Program.cs:244 comment.
- **Decision**: FIXED

### F2 — test-plan.md §5 deploy-smoke references the wrong endpoint path

- **Severity**: 🟦 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: context/foundation/test-plan.md:117, :152
- **Detail**: The §5 deploy-smoke gate row and the §6 TBD note both describe the probe as `curl /api/auth/login`. The actual login-challenge endpoint is `/auth/login` (Program.cs:163) — there is no `/api/auth/login` route — and the shipped smoke script correctly hits `/auth/login`. Phase 5 built the real smoke but did not touch test-plan.md, so the doc still names the wrong path. Someone running the smoke manually from the doc would 404.
- **Fix**: Correct `/api/auth/login` → `/auth/login` in test-plan.md §5 (line 117) and §6 (line 152).
- **Decision**: FIXED

### F3 — SSH.NET high-severity advisory (transitive, test-only)

- **Severity**: 🟦 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj
- **Detail**: `dotnet test` emits `NU1903: SSH.NET 2024.2.0 has a known high severity vulnerability`. It arrives transitively via `Testcontainers.PostgreSql`. It is test-project-only (never in the deployed backend image) and the vulnerable code path (SSH to a remote Docker host) is not used — the fixture talks to the local Docker daemon. No runtime exposure; noted for tracking, not a blocker.
- **Fix**: No change required now. Optionally spawn a follow-up to bump Testcontainers when a version with a patched SSH.NET is available.
- **Decision**: ACCEPTED — known test-only, non-exploitable advisory; no change

### F4 — e2e-ci.yml adds a push-to-master trigger beyond the plan's "pull_request" contract

- **Severity**: 🟦 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: .github/workflows/e2e-ci.yml:3-15
- **Detail**: The Phase 3 contract said the workflow is "keyed on `pull_request`". The implementation triggers on both `push` (master) and `pull_request`, mirroring the existing backend-ci.yml. Benign and arguably better (catches regressions on direct master pushes); the `paths:` filter keeps unrelated pushes out. Flagged only as an intentional deviation from the literal contract.
- **Fix**: No change needed — keep as-is (consistent with sibling workflows). Optionally note the addition in the plan.
- **Decision**: ACCEPTED — keep as-is, consistent with sibling workflows
