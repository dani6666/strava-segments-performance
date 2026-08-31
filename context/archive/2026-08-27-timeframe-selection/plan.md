# Timeframe Selection Implementation Plan

## Overview

Let the user bound a Strava workout fetch by a custom date window. Two optional dates (start / end) ride on the existing `POST /api/workouts/fetch` request and thread down to Strava's `after`/`before` query params on the activity listing. This narrows how much history the app pulls (easing rate-limit pressure) and establishes a date-range contract the future analysis slice (S-03) can reuse. No schema change; S-03 is not touched.

## Current State Analysis

- The fetch pipeline is: `POST /api/workouts/fetch` ([Program.cs:190](strava-segments-performance-backend/Program.cs)) sets status to `Pending` and writes the bare `user.Id` to `WorkoutFetchChannel` (a `Channel<int>`, [WorkoutFetchChannel.cs](strava-segments-performance-backend/Services/WorkoutFetchChannel.cs)). `WorkoutFetchWorker.ProcessUserAsync` ([WorkoutFetchWorker.cs:51](strava-segments-performance-backend/Services/WorkoutFetchWorker.cs)) reads the id, pages through `StravaApiClient.ListActivitiesPageAsync` ([StravaApiClient.cs:25](strava-segments-performance-backend/Services/StravaApiClient.cs)), skips already-cached activities by `StravaActivityId`, then fetches details.
- Strava's `athlete/activities` endpoint accepts `after` and `before` (epoch seconds, UTC, independently optional). The current call sends only `page` + `per_page` ([StravaApiClient.cs:29](strava-segments-performance-backend/Services/StravaApiClient.cs)).
- The "skip already-cached" guard ([WorkoutFetchWorker.cs:82](strava-segments-performance-backend/Services/WorkoutFetchWorker.cs)) means bounded fetches **accumulate** cleanly across windows — fetching window A then window B leaves both cached, and cached workouts are never re-fetched (matches PRD "cached workouts reused").
- The frontend dashboard ([dashboard.component.html](strava-segments-performance/src/app/dashboard/dashboard.component.html)) has only the fetch panel. `WorkoutFetchService.trigger()` ([workout-fetch.service.ts:33](strava-segments-performance/src/app/workouts/workout-fetch.service.ts)) POSTs an empty body. State is held in Angular signals.
- No analysis endpoint, scoring, or chart exists — **S-03 (`fitness-trend-chart`) is not built**. This slice deliberately does not depend on it.
- Backend test project exists with a request-capturing `StubHandler` ([StravaApiClientTests.cs](strava-segments-performance-backend-tests/StravaApiClientTests.cs)). Frontend has `ng test` (Karma/Jasmine) wired via `tsconfig.spec.json` but no `*.spec.ts` files yet.

## Desired End State

On the dashboard, above the fetch button, the user sees two optional date inputs ("From" / "To"). Leaving both blank fetches all history (unchanged default). Setting one or both narrows the fetch: only Strava activities within the window are listed, fetched, and cached. An invalid range (start after end) disables the fetch button with a message. When the user clicks Fetch / Resume / Check-for-new, the currently-selected window is sent every time. Verify by: (1) triggering a bounded fetch and confirming only in-window rides get cached; (2) the outgoing Strava request URL contains the expected `after`/`before` epoch seconds.

### Key Discoveries:

- Strava `after`/`before` are epoch seconds, UTC, and independently optional — a perfect match for "optional open-ended bounds" ([StravaApiClient.cs:29](strava-segments-performance-backend/Services/StravaApiClient.cs)).
- The channel payload is `int` today; carrying a range means changing it to a small record — no persistence needed because the frontend resends the selection on every trigger (including Resume) ([WorkoutFetchChannel.cs](strava-segments-performance-backend/Services/WorkoutFetchChannel.cs)).
- The skip-existing set already makes repeated bounded fetches additive and idempotent ([WorkoutFetchWorker.cs:64-87](strava-segments-performance-backend/Services/WorkoutFetchWorker.cs)).
- No EF migration required: filtering happens at the Strava API call, not against stored columns.

## What We're NOT Doing

- Not building or touching S-03 (scoring, analysis endpoint, or the fitness trend chart).
- Not adding an analysis/read endpoint over cached workouts — the timeframe bounds the **fetch**, not a query.
- Not persisting the selected range (no schema change, no migration, no localStorage/URL state) — it lives in memory and resets to All-time on reload.
- Not adding preset ranges (Last 30 days, etc.) — custom start/end pickers only.
- Not filtering the client-side skip logic by date — the whole cache is always additive.
- Not adding an end-to-end browser test (no e2e harness exists).

## Implementation Approach

Backend first so the contract exists, then the UI that drives it. Backend: widen the channel payload to a range record, accept optional `after`/`before` on the fetch endpoint with a start≤end guard, and append the two epoch-second params to the Strava listing call. Frontend: two date inputs bound to a signal, local-whole-day → UTC conversion, start≤end validation gating the button, and the range attached to the POST body on every trigger.

## Critical Implementation Details

- **Timezone & whole-day rounding**: The frontend owns the conversion. "From = YYYY-MM-DD" means the start of that day in the browser's local timezone; "To = YYYY-MM-DD" means the **end** of that day local (i.e. the start of the next day, exclusive) so the whole selected end-day is included. Both convert to UTC instants before sending. The backend stays timezone-agnostic — it receives UTC instants and forwards them to Strava as epoch seconds. Strava treats `after` as an exclusive lower bound and `before` as an exclusive upper bound; sending start-of-day (after) and start-of-next-day (before) yields an inclusive whole-day window.

## Phase 1: Backend — thread the date range through the fetch pipeline

### Overview

Carry an optional UTC date range from the fetch endpoint down to the Strava listing call, validating start≤end at the boundary.

### Changes Required:

#### 1. Fetch channel payload

**File**: `strava-segments-performance-backend/Services/WorkoutFetchChannel.cs`

**Intent**: Replace the bare user-id payload with a small value carrying the user id plus the optional window, so the worker knows what range to request.

**Contract**: Introduce a `record FetchRequest(int UserId, DateTime? AfterUtc, DateTime? BeforeUtc)` (both `DateTimeKind.Utc`); change the channel to `Channel<FetchRequest>` and expose `ChannelWriter<FetchRequest>` / `ChannelReader<FetchRequest>`.

#### 2. Fetch endpoint accepts and validates the range

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Let `POST /api/workouts/fetch` read an optional `after`/`before` from the request body, reject an inverted range, and pass the window to the channel. The empty-body case must keep working (all-time default).

**Contract**: Bind an optional request body (e.g. `FetchWorkoutsRequest { DateTime? After; DateTime? Before }`, both nullable, UTC). If both present and `After > Before`, return `Results.BadRequest` with a short message. Normalize each to `DateTimeKind.Utc`. Write `new FetchRequest(user.Id, after, before)` to the channel instead of `user.Id`. The status-row upsert logic and the returned status DTO are unchanged.

#### 3. Worker forwards the range to the Strava client

**File**: `strava-segments-performance-backend/Services/WorkoutFetchWorker.cs`

**Intent**: Read the new payload and pass the window into the activity-listing call; detail fetching is unchanged.

**Contract**: `ExecuteAsync` iterates `FetchRequest` values; `ProcessUserAsync` takes `(FetchRequest request, CancellationToken ct)` (or `userId` + range) and forwards `AfterUtc`/`BeforeUtc` to `ListActivitiesPageAsync`. `MarkFailedAsync` and the timeout/cancellation handling stay as-is (keyed by `request.UserId`).

#### 4. Strava client appends after/before

**File**: `strava-segments-performance-backend/Services/StravaApiClient.cs`

**Intent**: Add the optional epoch-second bounds to the `athlete/activities` query string only when provided.

**Contract**: `ListActivitiesPageAsync(User user, int page, int perPage, DateTime? afterUtc, DateTime? beforeUtc, CancellationToken ct)`. Append `&after=<epoch>` / `&before=<epoch>` only for non-null values, using `DateTimeOffset(...).ToUnixTimeSeconds()`. `GetActivityDetailAsync` and the retry/rate-limit `SendAsync` loop are untouched.

### Success Criteria:

#### Automated Verification:

- Solution builds: `dotnet build`
- Backend tests pass: `dotnet test`
- New test: `ListActivitiesPageAsync` includes `after=<epoch>&before=<epoch>` in the request URI when both bounds are given (assert against `StubHandler.Requests`).
- New test: no `after`/`before` appears when bounds are null (all-time default preserved).
- New test: `POST /api/workouts/fetch` with `After > Before` returns 400; with a valid or empty range returns Accepted/OK as before.

#### Manual Verification:

- Triggering a bounded fetch caches only rides whose start date falls in the window; a subsequent wider fetch adds the rest without re-fetching the already-cached rides.
- An all-time (empty-body) fetch behaves exactly as before this change.

**Implementation Note**: After this phase and all automated verification passes, pause for manual confirmation before starting Phase 2.

---

## Phase 2: Frontend — timeframe pickers wired into the fetch trigger

### Overview

Add two optional date inputs to the dashboard, validate the range, convert local whole-day boundaries to UTC, and send the window on every fetch trigger.

### Changes Required:

#### 1. Timeframe state + conversion

**File**: `strava-segments-performance/src/app/workouts/workout-fetch.service.ts`

**Intent**: Hold the selected range in memory and include it in the fetch POST body, converting local whole-day dates to UTC instants. The polling and status logic are unchanged.

**Contract**: Add `from`/`to` signals (nullable date strings) or accept a range argument on `trigger()`. Build the POST body as `{ after?: string, before?: string }` where `after` = start-of-local-`from`-day as UTC ISO and `before` = start-of-local-day-after-`to` as UTC ISO; omit each key when its date is blank. `checkStatus()` is unchanged (read-only). Every call path that triggers a fetch (initial fetch, Resume, Check-for-new — all route through `trigger()`) sends the current selection.

#### 2. Date pickers + validation UI

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.ts` and `.html`

**Intent**: Render "From"/"To" date inputs above the fetch controls, disable the trigger when the range is invalid, and show a short validation message.

**Contract**: Two `<input type="date">` bound to the service/component signals. A computed `invalidRange` = both set and `from > to`; when true, disable the Fetch/Resume/Check button and show an inline message. Blank inputs are valid (open-ended). No change to the existing `@switch` fetch-status rendering other than gating the trigger buttons.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm run build`
- Frontend tests pass: `npm test` (headless, single run)
- New spec: the service builds the POST body with correct UTC `after`/`before` for a given local range, and omits keys when a bound is blank.
- New spec: `invalidRange` is true when `from > to` and false for blank/valid ranges.

#### Manual Verification:

- Picking From/To then Fetch pulls only in-window rides (cross-checked with Phase 1 manual step).
- Leaving both blank and clicking Fetch pulls all history.
- Setting From after To disables the button and shows the message; clearing it re-enables.
- Reloading the page resets the range to blank (all-time) — expected.

**Implementation Note**: After this phase and all automated verification passes, pause for manual confirmation.

---

## Testing Strategy

### Unit Tests:

- Backend: `StravaApiClient` URL contains/omits `after`/`before` epoch seconds (extend `StravaApiClientTests.cs` using the existing `StubHandler`).
- Frontend: service POST-body construction (local→UTC, whole-day, key omission) and `invalidRange` computation.

### Integration Tests:

- Backend: `POST /api/workouts/fetch` accepts an optional range, returns 400 on `After > Before`, and preserves the empty-body all-time path.

### Manual Testing Steps:

1. Fetch with a narrow window; confirm only in-window rides cached.
2. Fetch a wider window afterward; confirm additive caching, no re-fetch of existing rides.
3. Fetch with both dates blank; confirm all-time behavior unchanged.
4. Set From > To; confirm the button disables with a message.

## Performance Considerations

Bounding the fetch reduces the number of Strava activity-listing pages and detail calls, directly lowering rate-limit exposure for users who only want a recent window — a net improvement over always fetching all history.

## Migration Notes

None. No schema change; the range is not persisted. The channel payload type changes from `int` to `FetchRequest`, which is an in-process, in-memory change with no data-migration impact.

## References

- Roadmap slice S-04: `context/foundation/roadmap.md:91`
- PRD FR-002 (nice-to-have): `context/foundation/prd.md:57`
- Fetch pipeline: `strava-segments-performance-backend/Program.cs:190`, `strava-segments-performance-backend/Services/WorkoutFetchWorker.cs:51`, `strava-segments-performance-backend/Services/StravaApiClient.cs:25`
- Test harness: `strava-segments-performance-backend-tests/StravaApiClientTests.cs`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend — thread the date range through the fetch pipeline

#### Automated

- [x] 1.1 Solution builds: `dotnet build` — 9d54e5e
- [x] 1.2 Backend tests pass: `dotnet test` — 9d54e5e
- [x] 1.3 Test: `ListActivitiesPageAsync` includes `after`/`before` epoch seconds when bounds given — 9d54e5e
- [x] 1.4 Test: no `after`/`before` in URL when bounds null (all-time preserved) — 9d54e5e
- [x] 1.5 Test: `POST /api/workouts/fetch` returns 400 on `After > Before`, Accepted/OK otherwise — 9d54e5e

#### Manual

- [x] 1.6 Bounded fetch caches only in-window rides; wider fetch adds rest without re-fetch — 9d54e5e
- [x] 1.7 Empty-body (all-time) fetch behaves as before — 9d54e5e

### Phase 2: Frontend — timeframe pickers wired into the fetch trigger

#### Automated

- [x] 2.1 Frontend builds: `npm run build` — 9d54e5e
- [x] 2.2 Frontend tests pass: `npm test` — 9d54e5e
- [x] 2.3 Spec: service builds correct UTC `after`/`before` body; omits blank bounds — 9d54e5e
- [x] 2.4 Spec: `invalidRange` true when `from > to`, false for blank/valid — 9d54e5e

#### Manual

- [x] 2.5 From/To then Fetch pulls only in-window rides — 9d54e5e
- [x] 2.6 Both blank + Fetch pulls all history — 9d54e5e
- [x] 2.7 From after To disables button with message; clearing re-enables — e9234f5
- [x] 2.8 Reload resets range to blank (all-time) — 9d54e5e
