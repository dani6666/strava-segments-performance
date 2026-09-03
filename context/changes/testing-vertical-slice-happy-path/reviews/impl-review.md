<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Vertical-Slice Happy-Path E2E

- **Plan**: context/changes/testing-vertical-slice-happy-path/plan.md
- **Scope**: All 3 phases (full plan)
- **Date**: 2026-09-03
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical, 3 warnings, 4 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | PASS |
| Success Criteria | PASS |

Success Criteria note: 3.1 (local e2e), 3.3–3.6 all green; 3.2 (branch-PR CI) is inherently a post-push signal and remains `[ ]` pending push.

## Findings

### F1 — Debounce timeout not cleared on component destroy

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality (reliability)
- **Location**: strava-segments-performance/src/app/dashboard/dashboard.component.ts:23,50
- **Detail**: `analysisRetriggerTimeoutId` set by the picker `effect` is never cleared on component destroy. If the user navigates away within the ~300ms debounce window, the callback fires on a torn-down component and calls `analysisService.load(...)` from a dead view — a small leak plus a phantom analysis request.
- **Fix**: Implement `OnDestroy` and clear the pending timeout: `ngOnDestroy() { if (this.analysisRetriggerTimeoutId !== null) clearTimeout(this.analysisRetriggerTimeoutId); }`.
- **Decision**: FIXED

### F2 — Out-of-order response race in `AnalysisService.load()`

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality (reliability)
- **Location**: strava-segments-performance/src/app/workouts/analysis.service.ts:20-37 (with dashboard.component.ts:50-56)
- **Detail**: Every picker change past the debounce opens a fresh `httpClient.get(...).subscribe(...)` without cancelling the previous in-flight request. A slow earlier response can arrive after a fast later one and overwrite `series` with stale data — the chart then shows the pre-narrow trend under a narrowed picker.
- **Fix A ⭐ Recommended**: Convert `load()` to feed a Subject and `switchMap` to the HTTP call; the RxJS operator cancels the previous subscription on the next value.
  - Strength: Standard Angular pattern; matches how debounced HTTP is done idiomatically; small diff.
  - Tradeoff: Introduces one Subject field and a subscription in the service.
  - Confidence: HIGH — the repo already uses RxJS elsewhere (workout-fetch.service.ts).
  - Blind spot: Haven't verified whether any test doubles rely on the plain `httpClient.get` call pattern.
- **Fix B**: Gate writes with a monotonically increasing request id; drop responses whose id is stale.
  - Strength: No RxJS added; smallest surface change.
  - Tradeoff: Manual bookkeeping the team has to remember; misses cancellation on the wire (still consumes bandwidth).
  - Confidence: MEDIUM — works but is a step below the idiomatic fix.
  - Blind spot: None significant.
- **Decision**: FIXED via Fix A (Subject + switchMap + catchError to keep the outer subscription alive)

### F3 — `waitForResponse` registered after fixture's implicit `page.goto`

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality (reliability / flake risk)
- **Location**: strava-segments-performance/e2e/vertical-slice.spec.ts:19-23
- **Detail**: The `fixtures.ts` extension runs `page.goto('/dashboard')` before the test body executes. The spec then declares `await page.waitForResponse(...)` for the initial `/api/analysis/fitness-trend` call. If the response lands during navigation (very possible against a local backend), the wait misses it and times out. Currently passing because the analysis chain is slow enough to arrive after the wait registers, but this is timing-dependent — a fast day in CI would flake.
- **Fix**: Register the wait before navigation. Either (a) push the goto into the test and wrap in `Promise.all([page.waitForResponse(...), page.goto('/dashboard')])`, or (b) stop the fixture from auto-navigating for this spec (skip the fixture, use bare `test` from `@playwright/test`).
- **Decision**: FIXED (both — import bare test from @playwright/test AND wrap in Promise.all)

### F4 — EF Core `ExecuteDeleteAsync` inside `BeginTransactionAsync`

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🔎 MEDIUM — real tradeoff; worth verifying once
- **Dimension**: Safety & Quality (data-safety)
- **Location**: strava-segments-performance-backend/Program.cs:227-233, 338-346
- **Detail**: Both seed and reset wrap `SegmentEfforts.ExecuteDeleteAsync()` / `Activities.ExecuteDeleteAsync()` / `WorkoutFetchStatuses.ExecuteDeleteAsync()` inside `db.Database.BeginTransactionAsync()`. Older Npgsql provider versions did not enlist bulk `ExecuteDelete` in the ambient transaction. If not enlisted, a mid-op crash could leave partial state — dangerous in an E2E setup only if a test kills the process mid-seed.
- **Fix**: Quick smoke: run the seed, kill the backend before `tx.CommitAsync()`, verify no rows landed. If uncommitted rows are visible, either upgrade Npgsql or replace bulk `ExecuteDeleteAsync` with `RemoveRange` + `SaveChangesAsync` (which flows through the transaction reliably).
- **Decision**: SKIPPED (E2E-only surface; risk accepted)

### F5 — Duplicated `IsEnvironment("E2E")` guard block

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🏃 LOW — trivial refactor
- **Dimension**: Pattern Consistency
- **Location**: strava-segments-performance-backend/Program.cs:189 and Program.cs:220 (or wherever the new block opens)
- **Detail**: Two adjacent `if (app.Environment.IsEnvironment("E2E")) { ... }` blocks — the original `/auth/test-login` + `/e2e-stub/*` region and the new `/e2e/seed` + `/e2e/reset` region. Not incorrect, but the "never-in-prod" audit surface is now spread across two spots.
- **Fix**: Fold the new seed/reset endpoints into the existing E2E-only block.
- **Decision**: FIXED (folded /e2e-stub/* into the same E2E block; single env-gate now covers all E2E-only endpoints)

### F6 — Fixture dates are absolute (2026-08-15/2026-08-22)

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🔎 MEDIUM — forward-looking risk
- **Dimension**: Safety & Quality (reliability, future-proofing)
- **Location**: strava-segments-performance-backend/Program.cs:241,254
- **Detail**: Fixture activities are pinned to absolute dates in August 2026. If the dashboard picker ever defaults to a "last N days from now" window, these fixtures fall outside the default window as time passes and the initial auto-analysis returns empty, breaking the spec at a distance. Today the picker defaults are empty (no filter), so this is latent.
- **Fix**: Consider anchoring fixture dates relative to `DateTime.UtcNow` (e.g. `UtcNow.Date.AddDays(-14)`), and update the spec's "narrow To" value to match. Or make the picker's default explicit in the spec via `page.getByLabel('To').fill(...)` before the initial wait.
- **Decision**: SKIPPED (latent risk; revisit if the picker default ever becomes relative)

### F7 — Plan text says `POST /auth/test-login`; endpoint is `GET`

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🏃 LOW — documentation drift, not a code defect
- **Dimension**: Plan Adherence (plan-text accuracy)
- **Location**: context/changes/testing-vertical-slice-happy-path/plan.md:178 (Phase 3, global-teardown contract)
- **Detail**: The plan writes "POST /auth/test-login?athleteId=12345&name=Test Rider" for the global teardown. `/auth/test-login` is `MapGet` in Program.cs:196. The implementation correctly uses `ctx.get(...)`. This is a plan-text bug, not a code drift.
- **Fix**: Amend the plan's Phase 3 global-teardown contract to say `GET /auth/test-login`, or leave as historical record and note the correction in change.md.
- **Decision**: FIXED (plan.md:178 corrected POST → GET)
