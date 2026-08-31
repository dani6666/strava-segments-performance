<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Timeframe Selection

- **Plan**: context/changes/timeframe-selection/plan.md
- **Scope**: Phase 1 & 2 of 2 (full plan)
- **Date**: 2026-08-27
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical, 3 warnings, 1 observation

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | WARNING |
| Scope Discipline | WARNING |
| Safety & Quality | PASS |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | WARNING |

## Findings

### F1 — `!important` on disabled-button styling is an unnecessary workaround

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: strava-segments-performance/src/app/dashboard/dashboard.component.scss:73-81
- **Detail**: The `&:disabled` block compiles to `.fetch-panel button:disabled` (specificity 0,2,1), which already beats the base `.fetch-panel button` rule (0,1,1) on the cascade — no `!important` needed. The symptom (base orange winning) was almost certainly a stale/duplicated stylesheet from the dev server's HMR after we changed deps + angular.json; clearing `.angular/cache` and a clean rebuild is what actually fixed it. The four `!important` flags are now dead weight and will force any future override of these buttons to also use `!important`. No other rule in this SCSS uses `!important`.
- **Fix**: Remove the four `!important` flags; the clean build (cache cleared) already makes `:disabled` win on its own. Rebuild and re-confirm the gray disabled state.
- **Decision**: FIXED — removed all four `!important` flags; cleaned `.angular/cache` and rebuilt successfully. Gray disabled state wins on specificity alone.

### F2 — Endpoint 400 behavior (criterion 1.5) is not covered by an automated test

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Success Criteria
- **Location**: strava-segments-performance-backend/Program.cs:198-199
- **Detail**: Plan criterion 1.5 called for a test asserting `POST /api/workouts/fetch` returns 400 on `After > Before`. Instead the range logic was extracted into `FetchWindowValidator` and unit-tested there (6 InlineData cases). That covers the *logic*, but nothing asserts the *endpoint wiring* (validator → `Results.BadRequest`). A future refactor that forgets to call the validator, or mis-binds the request body, would pass all tests. The wiring is currently only manually verified. Reasonable adaptation given no `WebApplicationFactory`/`Mvc.Testing` harness exists in the repo — but it is drift from the stated automated criterion.
- **Fix A ⭐ Recommended**: Accept the gap; leave the validator unit test as the automated coverage and rely on manual verification for the wiring.
  - Strength: Proportionate to a nice-to-have slice; avoids standing up integration-test infra (`Microsoft.AspNetCore.Mvc.Testing`, a test host) for one assertion.
  - Tradeoff: Endpoint wiring regressions won't be caught automatically.
  - Confidence: HIGH — the wiring is a single call site, low churn risk.
  - Blind spot: None significant.
- **Fix B**: Add `Microsoft.AspNetCore.Mvc.Testing`, expose `Program` as partial, and write a real integration test hitting the endpoint (400 on inverted range, Accepted on valid/empty).
  - Strength: Closes the criterion exactly as written; establishes endpoint-test infra the whole backend can reuse.
  - Tradeoff: New test-host dependency + auth/cookie mocking for a `.RequireAuthorization()` endpoint — real setup cost for one slice.
  - Confidence: MED — auth mocking on the minimal-API host is non-trivial to get right the first time.
  - Blind spot: Haven't scoped how much fixture work the cookie-auth requirement adds.
- **Decision**: ACCEPTED (Fix A) — validator unit test + manual verification accepted as proportionate coverage for a nice-to-have slice; no integration-test harness added.

### F3 — Frontend test infrastructure added beyond plan scope

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: strava-segments-performance/angular.json:88-90, strava-segments-performance/package.json:9,27,30
- **Detail**: The plan assumed a working test runner; none existed (no `test` target, no test deps, `node_modules` not installed). Implementation added the `@angular/build:unit-test` target, `vitest`+`jsdom` devDeps, and changed the `npm test` script to `ng test --no-watch --no-progress`. All benign and necessary to satisfy the plan's "npm test" criterion, but unplanned — and it changes the default behavior of `npm test` from watch mode to single-run, which future contributors should know.
- **Fix**: No code change needed. Record the test-runner setup in AGENTS.md (frontend commands) or a lessons entry so the single-run `npm test` default and the Vitest toolchain aren't a surprise.
- **Decision**: FIXED — updated AGENTS.md frontend commands: `npm test` now documented as "Vitest via @angular/build:unit-test; single-run, non-watch" (was incorrectly labeled "Karma").

### F4 — `NormalizeUtc` relabels rather than converts non-UTC inputs

- **Severity**: 🔵 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend/Program.cs:166-167
- **Detail**: `NormalizeUtc` does `DateTime.SpecifyKind(value, Utc)`, which relabels the Kind without converting the clock value. The frontend always sends `toISOString()` output (trailing `Z`), which `System.Text.Json` parses as `Kind=Utc` with the correct instant, so this is a correct no-op in practice — and it's what lets `new DateTimeOffset(dt, TimeSpan.Zero)` in `StravaApiClient` not throw. But a direct API caller sending an offset form (e.g. `...+02:00`) would be parsed as `Kind=Local`, then silently *relabeled* as UTC — shifting the effective window by the offset. Client contract is Z-only and documented, so real-world risk is low.
- **Fix**: Optionally harden by converting instead of relabeling (e.g. `value.Value.ToUniversalTime()` when `Kind != Utc`), or document the Z-only input contract at the endpoint. Low priority given the fixed client.
- **Decision**: FIXED — `NormalizeUtc` now converts `Local`-kind inputs via `ToUniversalTime()` (offset-form ISO strings map to the correct UTC instant); `Utc` kept as-is, `Unspecified` assumed UTC. Backend build + 17 tests pass.
