<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Delete All Activities & Reset to Initial State

- **Plan**: context/changes/delete-all-activities/plan.md
- **Scope**: Phase 1 & 2 of 2 (full plan)
- **Date**: 2026-09-04
- **Verdict**: APPROVED
- **Findings**: 0 critical, 2 warnings, 1 observation

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | WARNING |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | PASS |
| Success Criteria | PASS |

## Findings

### F1 — Delete flow has no error handling

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance/src/app/workouts/workout-fetch.service.ts:113 (`deleteAll`), strava-segments-performance/src/app/dashboard/dashboard.component.ts:102
- **Detail**: The Phase 2 contract for the service method said "Follow the existing `trigger()`/`checkStatus()` subscribe-with-error-handler shape." Both of those methods route failures through `setFailed(err)`. The new `deleteAll()` has only a success-path `tap` and no error handling, and the component's `this.fetchService.deleteAll().subscribe(() => this.analysisService.reset())` passes no `error` callback. If `DELETE /api/workouts` returns non-2xx or the network fails, the observable errors with no handler: the user gets no feedback, the UI stays in the `completed` state, and RxJS reports an unhandled error. Data integrity is safe (the backend transaction rolls back), but the failure is silent.
- **Fix**: Add an `error` callback in the component subscribe (or a `catchError`/`setFailed` in the service `deleteAll`) so a failed delete surfaces the same failed-status feedback as `trigger()`/`checkStatus()`.
- **Decision**: SKIPPED

### F2 — Prettier reformatting churn on unrelated lines

- **Severity**: 👁 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: strava-segments-performance/src/app/workouts/workout-fetch.service.ts
- **Detail**: Beyond the feature additions, the Phase 2 commit reformats existing code that the change did not functionally touch — the `FetchStatusValue` union split across lines, arrow-param parens (`status =>` → `(status) =>`), trailing commas, and multi-line reflows in `trigger()`, `checkStatus()`, `startPolling()`, and `setFailed()`. Benign (it aligns the file with Prettier) but it inflates the diff and mixes formatting churn into a feature commit, raising merge-conflict surface for unrelated lines.
- **Fix**: Leave as-is (the file is now Prettier-consistent); in future, keep formatting-only churn out of feature commits or run the formatter repo-wide in a dedicated commit.
- **Decision**: SKIPPED

### F3 — Pending analysis-retrigger debounce timer not cleared on delete

- **Severity**: 👁 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance/src/app/dashboard/dashboard.component.ts:100
- **Detail**: The plan's Phase 2 concern ("avoid the completion effect re-triggering an analysis load") is correctly handled for the completion effect (dashboard.component.ts:30-37 — resetting status to `idle` fails its `status === 'completed'` guard). But the second, picker-debounce effect (lines 39-57) can have a pending `setTimeout` (`analysisRetriggerTimeoutId`) in flight from a recent date-picker change. If the user changes a date and confirms delete within the 300ms window, that timer fires post-reset and calls `analysisService.load(...)` against now-empty data. Not user-visible — the analysis `@switch` is nested inside the `completed` fetch-status branch, which unmounts once status is `idle` — so the effect is only a wasted HTTP call. `deleteAll()` does not clear the pending timer.
- **Fix**: In `deleteAll()`, clear `analysisRetriggerTimeoutId` (as `ngOnDestroy` already does) before/after resetting, to cancel any in-flight debounced analysis load.
- **Decision**: FIXED (dashboard.component.ts:100-104 — clears the pending timer before delete/reset; frontend build green)
