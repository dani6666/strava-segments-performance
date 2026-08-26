<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Workout Data Fetching

- **Plan**: context/changes/workout-data-fetch/plan.md
- **Scope**: All phases (1–5 of 5)
- **Date**: 2026-08-10
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical, 3 warnings, 4 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | WARNING |
| Scope Discipline | WARNING |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | PASS |

Automated success criteria all green: backend `dotnet build` clean; `dotnet test` 8/8 pass; frontend `npm run build` clean. Manual Progress checkboxes all `[x]` with plausible supporting code (no rubber-stamping detected).

## Findings

### F1 — `Pending` status can wedge, permanently blocking re-triggers after a restart

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend/Program.cs:123-125
- **Detail**: The startup reset only maps `Running → Interrupted`. The work queue (`WorkoutFetchChannel`) is an in-memory unbounded channel whose contents are lost on restart. If the process restarts in the window after `POST /api/workouts/fetch` sets `Pending` + writes the user id to the channel but before the worker dequeues it, the queued message is gone yet the status row stays `Pending`. The trigger guard (`Program.cs:196`) excludes both `Pending` and `Running`, so `ExecuteUpdateAsync` matches 0 rows; `existing` is non-null, so the endpoint returns the stale `Pending` DTO (`Program.cs:222`) and never re-enqueues. The user is locked out of fetching with no recovery but a manual DB edit. This reintroduces exactly the hazard the plan's Critical Implementation Details called out for `Running` ("must be reset to `Interrupted`… otherwise the single-flight check would block the user from re-triggering") — the `Pending` redesign didn't extend the reset to cover it. Narrow timing window, but permanent effect if hit.
- **Fix**: Extend the startup reset filter to also cover `Pending`: `Where(s => s.Status == FetchStatusState.Running || s.Status == FetchStatusState.Pending)`, so an orphaned enqueue becomes `Interrupted` and re-triggerable.
  - Strength: One-line change at the same layer the plan already chose for the `Running` case; makes the invariant complete.
  - Tradeoff: A genuinely-just-enqueued `Pending` present at startup is also reset — harmless, since the channel is empty after restart anyway and the user simply re-triggers.
  - Confidence: HIGH — traced the full endpoint/worker/reset path; the guard and the reset are the only two places that touch this transition.
  - Blind spot: Whether a belt-and-suspenders re-enqueue on the trigger side (when it sees a stale `Pending`) is also wanted — the startup reset alone is sufficient for the identified window.
- **Decision**: FIXED — startup reset filter now covers `Pending` (Program.cs:124)

### F2 — `CyclingSportTypes` silently drops `EBikeRide` and `EMountainBikeRide`

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Plan Adherence
- **Location**: strava-segments-performance-backend/Services/StravaDtos.cs:40-48
- **Detail**: The plan (Phase 3, item 1) specified an 8-value `CyclingSportTypes` set including `"EBikeRide"` and `"EMountainBikeRide"`. The implementation has only 6 — both e-bike types are missing. Effect: e-bike activities are filtered out at listing and never cached or scored. This is the only functional drift with user-visible impact. It may even be intentional (motor assist skews the HR-vs-effort fitness signal S-03 will compute), but nothing in the diff or plan records that decision, so it reads as an accidental omission.
- **Fix A ⭐ Recommended**: Add `"EBikeRide"` and `"EMountainBikeRide"` back to the set to match the plan.
  - Strength: Restores the agreed scope; trivial, no other code touched.
  - Tradeoff: If e-bikes genuinely shouldn't count toward fitness, this re-includes noise for S-03 to handle later.
  - Confidence: HIGH — the plan is explicit about the 8 values.
  - Blind spot: Whether S-03's scoring intends to treat e-bike HR/effort as comparable to unassisted rides.
- **Fix B**: Keep e-bikes excluded, but document the deviation as a plan addendum with the fitness-scoring rationale.
  - Strength: Preserves a defensible product choice and records the reasoning for S-03.
  - Tradeoff: Diverges from the written plan; needs a real product call, not just a code edit.
  - Confidence: MEDIUM — depends on a scoring-domain decision not yet made.
  - Blind spot: User expectation — an e-bike owner clicking "Fetch my workouts" silently gets nothing.
- **Decision**: FIXED via Fix B — deviation documented as a plan addendum (plan.md "## Addenda"); code left as-is

### F3 — Frontend pollers are never torn down and can run in duplicate; no error handling

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance/src/app/workouts/workout-fetch.service.ts:52-64
- **Detail**: `startPolling()` creates an `interval(2000)` subscription that is never stored or unsubscribed; it only ends when `takeWhile` sees a terminal state. Two problems: (1) the service is `providedIn: 'root'`, so a poll started by `checkStatus()` (called from `DashboardComponent.ngOnInit`) survives component teardown and keeps hitting `/fetch-status` every 2s for the app's lifetime if the fetch never terminates; (2) calling `trigger()` while a `checkStatus`-initiated poll is already live spawns a *second* concurrent poller — both write the same signal, doubling request load, with no guard. Separately, neither `trigger()` (`:35`) nor `checkStatus()` (`:49`) supplies an error callback, unlike the sibling pattern in `auth.service.ts:31-40`; a 401/500 on the POST leaves the UI silently stuck with no failed state.
- **Fix**: Track the polling subscription in a field and guard against starting a second one (or drive polling from a single `Subject` via `switchMap`), and add an error branch on `trigger()`/`checkStatus()` that sets a `failed` status. Mirror the error-handling shape already used in `auth.service.ts`.
  - Strength: Removes both the leak and the duplicate-poller path; aligns the service with the established `AuthService` convention.
  - Tradeoff: Slightly more state in the service (a stored `Subscription` or a trigger subject).
  - Confidence: HIGH — the untracked `.subscribe()` and the two independent `startPolling()` entry points are visible in the file.
  - Blind spot: None significant.
- **Decision**: FIXED — tracked `pollingSub` + duplicate-poller guard + `setFailed()` error handlers on trigger/checkStatus/poll (workout-fetch.service.ts)

### F4 — Unplanned `Pending` state threaded through model, endpoint, worker, and frontend

- **Severity**: 🔵 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: strava-segments-performance-backend/Models/WorkoutFetchStatus.cs:6
- **Detail**: The plan's `FetchStatusState` enum was `Idle, Running, Completed, Failed, Interrupted`. The implementation adds a `Pending` value and uses it to mean "enqueued, worker not yet started," with the worker promoting `Pending → Running`. This is a coherent design improvement (cleanly separates "queued" from "in-flight" and makes the single-flight guard exclude both), but it's unplanned scope that spread across the DTO mapping, the trigger endpoint, the worker, and the frontend union type. It is also the state at the root of F1.
- **Fix**: Document the `Pending` state as a plan addendum (what it means and the `Pending → Running` promotion), so the plan stays the source of truth and F1's reset invariant is recorded alongside it.
- **Decision**: FIXED — documented in plan.md "## Addenda"

### F5 — `HttpResponseMessage` leaked on non-2xx responses other than 401/429

- **Severity**: 🔵 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend/Services/StravaApiClient.cs:53,71
- **Detail**: In `SendAsync`, the `response` obtained at line 53 is disposed only in the 401 and 429 branches. On the success path it is returned to the caller (which wraps it in `using`), but when `EnsureSuccessStatusCode()` throws for any other failure (500, 404, 403…) at line 71, that `response` is never disposed. Under sustained Strava 5xx this leaks response handles until finalization. (The retry/clone logic itself is correct — requests are rebuilt via `requestFactory()` and the 401 retry is correctly single-shot.)
- **Fix**: Guard the throw path — e.g. `using` the response inside the loop, or dispose in a `finally` before `EnsureSuccessStatusCode()`.
- **Decision**: FIXED — response disposed on the non-success throw path in `SendAsync` (StravaApiClient.cs)

### F6 — 429 wait-and-retry loop has no maximum attempt or total-backoff cap

- **Severity**: 🔵 OBSERVATION
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend/Services/StravaApiClient.cs:63-69
- **Detail**: The `TooManyRequests` branch waits `Retry-After` (fallback 15 min) and `continue`s with no retry ceiling — only cancellation breaks it. This largely matches the plan's stated intent ("waits and continues automatically instead of failing"), and the deliberately single-worker design means "blocking other users" during a wait is by design (Strava's limit is app-wide). The residual risk: if the daily limit is exhausted, the one worker parks in repeated 15-min waits for the rest of the day with its status stuck `Running`, and any other queued users wait behind it with no visibility. Flagging as a design edge to confirm, not a plan violation.
- **Fix**: Consider a cap on cumulative backoff (or attempts) after which the activity/user is marked `Failed` so the worker drains the queue, and/or log when a long wait is entered so the stall is observable.
- **Decision**: FIXED (differently, per user) — `DefaultRateLimitWait` 15min→1min; per-call retry capped at 1h via `SingleCallRetryTimeout` + `TimeProvider` deadline (StravaApiClient.cs); whole per-user fetch capped at 3h via linked-CTS `CancelAfter` in the worker, distinguishing shutdown-cancel from timeout-abort (WorkoutFetchWorker.cs). Added `SendAsync_WhenRateLimitWaitWouldExceedThePerCallCap_ThrowsTimeoutInsteadOfWaiting` covering the 1h cap; tests now 9/9.

### F7 — High-severity advisory on transitive `Microsoft.OpenApi` 2.0.0

- **Severity**: 🔵 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend (restore warning NU1903)
- **Detail**: `dotnet build`/`test` restore emits `NU1903: Package 'Microsoft.OpenApi' 2.0.0 has a known high severity vulnerability` (GHSA-v5pm-xwqc-g5wc). Pre-existing / transitive (pulled in via OpenAPI), not introduced by this change, but surfaced by the new test project's restore and worth clearing.
- **Fix**: Bump the OpenAPI package reference to a patched version and re-run restore to confirm the advisory clears.
- **Decision**: FIXED — pinned direct `Microsoft.OpenApi` 2.7.5 (first patched version) in backend csproj, overriding the transitive 2.0.0; `dotnet list package --vulnerable` clean, build + 8/8 tests pass.
