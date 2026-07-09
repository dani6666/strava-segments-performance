# Workout Data Fetching — Implementation Plan

## Overview

Implement S-02: an authenticated user can trigger fetching of their cycling activities and segment efforts from Strava, see staged progress while it runs (potentially hours, given rate limits and a full-history fetch), and have the results cached in PostgreSQL so repeat triggers never re-fetch data already stored. This replaces the dashboard's empty state (built in S-01 for exactly this purpose) and unblocks S-03's fitness scoring.

## Current State Analysis

- **Backend** has OAuth (S-01) but zero Strava API integration — no `HttpClient` wrapper, no background job infrastructure (`Program.cs:1-156`), and only a `User` entity (`Models/User.cs`) with encrypted access/refresh tokens (`Services/TokenEncryptionService.cs`).
- **Token refresh is unimplemented.** S-01 never called the Strava API, so the ~6h access token lifetime was never exercised. S-02 is the first feature to make real outbound calls, and a full-history fetch under rate limits can run for hours — refresh must be built now.
- **No background job infra exists** — no `IHostedService`, no queue, no job library.
- **Frontend dashboard** has a clearly marked empty-state slot (`dashboard.component.html:7-9`) and an established HTTP/signal pattern in `AuthService` (`withCredentials: true`, signals for reactive state, no interceptors) that a new fetch service should mirror.
- **No test infrastructure exists in either project.** Angular scaffolding has `skipTests: true`; there's no backend test project.

### Key Discoveries:

- Strava's activity list endpoint (`GET /athlete/activities`) returns activity summaries including `has_heartrate` (bool) — segment efforts require a separate detail call per activity (`GET /activities/{id}?include_all_efforts=true`). Activities without HR data are filtered out during listing since fitness scoring (S-03) requires HR. Summary-level fields (name, sport type, date, distance, times) are available in the list response and should be saved during listing to avoid re-extracting them from the detail response. For a full-history fetch this means roughly 2x the API calls of the (HR-filtered) activity count, which is the real driver of multi-hour fetch times under a ~100 req/15min limit.
- The PRD (`context/foundation/prd.md`, FR-003) explicitly anticipates long fetch times and calls for progress UX separate from the trigger — a multi-hour fetch is an accepted, not a surprising, outcome.
- `AppDbContext` (`Data/AppDbContext.cs`) currently has one `DbSet<User>`; adding entities follows the same `OnModelCreating` index pattern already established for `StravaAthleteId`.
- The existing auth endpoint pattern (`Program.cs:137-147`) shows how to read the authenticated user's claims and load their `User` row — new endpoints reuse this exact pattern.

## Desired End State

An authenticated user with no cached workouts sees a "Fetch my workouts" button in place of the current empty-state text. Clicking it starts a backend job and the UI polls and displays staged progress (e.g., "Fetching segment details... 12 of 340"). On completion, the UI shows a count of cached cycling activities and a way to trigger again. Fetched data persists in PostgreSQL (`Activities`, `SegmentEfforts` tables) and is never re-fetched once an activity's details are cached. If the backend restarts mid-fetch, re-triggering resumes without losing already-cached data. If Strava rate-limits the app, the job waits and continues automatically instead of failing.

**Verification**: `docker compose up`, log in, click "Fetch my workouts", watch staged progress update, confirm `Activities`/`SegmentEfforts` rows appear in Postgres, restart the backend container mid-fetch, re-trigger, and confirm it resumes rather than re-fetching everything.

## What We're NOT Doing

- Bounded/date-limited first fetch — full history is fetched on first trigger (user decision)
- SignalR/WebSocket push for progress — polling only
- Hangfire/Quartz or any external job scheduling library — a single in-process `BackgroundService` + channel
- Segment-level fitness scoring/analysis — that's S-03
- Timeframe filtering UI — that's S-04 (FR-002)
- Parallel fetching across multiple users — Strava's rate limit is shared per application (not per athlete), so a single sequential worker is correct, not just simpler
- Angular unit/component tests — explicitly deferred, consistent with S-01 and the disabled test scaffolding
- Progress bar with ETA — staged text + count only
- Reconciling activities deleted or edited on Strava after being cached — cache is additive only; no delete/update sync
- A dedicated "resume" endpoint or UI affordance — resumption is a side effect of the idempotent trigger (see Critical Implementation Details)

## Implementation Approach

Build order deliberately front-loads visible feedback instead of building the riskiest, invisible piece first. Phase 2 builds the frontend UI and polling service against the endpoint contract, before any backend for it exists. Phase 3 wires a single in-process `Channel<int>` (user IDs) feeding a singleton `BackgroundService`, the real trigger/status endpoints, and a deliberately minimal Strava client (direct token use, no refresh, no rate-limit backoff) — enough to show real end-to-end progress in the Phase 2 UI for small test fetches. Phase 4 hardens that same Strava client with token refresh and 429 backoff, since those failure modes mainly bite on large, long-running fetches that are impractical to trigger by hand on every iteration. Each fetch run: (1) lists all activities via paginated `GET /athlete/activities`, filtering to cycling sport types with heart-rate data (`has_heartrate == true`) and inserting new `Activity` rows with full summary data (name, sport type, date, distance, times) saved from the list response; (2) fetches details + segment efforts for every activity not yet marked `DetailsFetched`, committing after each one. Because listing saves all summary data upfront, the total count of activities needing detail-fetching is known before that stage begins, enabling "X of Y" progress display. A `WorkoutFetchStatus` row per user tracks stage/counts/errors and is what the frontend polls.

## Critical Implementation Details

### Build order front-loads visible feedback (Phase 2 → 3 → 4)

The UI (Phase 2) and the wiring (Phase 3) are built before the Strava client is hardened (Phase 4), so every later phase is verified by watching real progress in the browser instead of inspecting raw HTTP responses. Phase 3's `StravaApiClient` is intentionally a minimal, un-hardened first version (bare bearer-token calls, no refresh, no backoff) — good enough for the small manual test fetches used during development. Phase 4 edits the *same file* to add token refresh and 429 backoff; it's a hardening pass, not new functionality, and must not change the public method signatures the Phase 3 worker already depends on.

### Resumability is a side effect of the chosen re-list-and-diff strategy, not separate logic

Because every trigger re-lists all activities and diffs against cached `StravaActivityId`s (user's chosen incremental strategy), and detail-fetching separately tracks per-activity `DetailsFetched`, a crash or restart mid-fetch requires no explicit checkpoint. Re-triggering re-lists (cheap), skips activities already fully cached, and only fetches details for whatever is still `DetailsFetched = false` — including partially-processed activities from the interrupted run. On startup, any `WorkoutFetchStatus` row left in `Running` from before the restart must be reset to `Interrupted` (not left as `Running`), otherwise the single-flight check in the trigger endpoint would block the user from re-triggering. Do not build a separate "resume" code path — the normal trigger flow already resumes correctly.

### Rate-limit handling (Phase 4): react to 429 + `Retry-After`, don't pre-compute from usage headers

Strava exposes `X-RateLimit-Usage`/`X-RateLimit-Limit` headers (format `15min,daily`), but the exact header set has changed over Strava API versions (a separate read-specific family was added at one point). Rather than pre-emptively parsing usage headers to predict when a call would exceed the limit, react to an actual `429` response: read `Retry-After` if present, otherwise fall back to a fixed conservative wait (e.g. 15 minutes), then retry the same call. This is simpler and doesn't depend on header formats that may drift.

### Rate-limit and token-refresh waits must use an injectable clock (Phase 4), not `Thread.Sleep`/bare `Task.Delay`

Tests need to simulate a 429-triggered wait without actually sleeping. Inject `TimeProvider` (registered as `builder.Services.AddSingleton(TimeProvider.System)`) into the Strava client and use the `Task.Delay(TimeSpan, TimeProvider, CancellationToken)` overload. Tests substitute `TimeProvider` with `FakeTimeProvider` (or a hand-rolled fake) to advance time instantly.

### Background service needs its own DI scope per fetch run (Phase 3)

`WorkoutFetchWorker` is a singleton (`BackgroundService`); `AppDbContext` is scoped. Each dequeued user ID must be processed inside a fresh `IServiceScopeFactory`-created scope, not by injecting `AppDbContext` directly into the worker's constructor.

---

## Phase 1: Data Model & Migrations

### Overview

Add the entities that store fetched Strava data and per-user fetch status, and generate the migration.

### Changes Required:

#### 1. Activity entity

**File**: `strava-segments-performance-backend/Models/Activity.cs`

**Intent**: Represents one cached Strava cycling activity belonging to a user. Acts as both the cache row and the resumability marker (`DetailsFetched`).

**Contract**: Class `Activity` in namespace `StravaSegmentsPerformanceBackend.Models`. Properties: `Id` (int, PK), `UserId` (int, FK to `User`), `StravaActivityId` (long), `Name` (string), `SportType` (string), `StartDateUtc` (DateTime), `DistanceMeters` (double), `MovingTimeSeconds` (int), `ElapsedTimeSeconds` (int), `DetailsFetched` (bool, default false), `FetchedAtUtc` (DateTime).

#### 2. SegmentEffort entity

**File**: `strava-segments-performance-backend/Models/SegmentEffort.cs`

**Intent**: Represents one segment effort within a cached activity — the elapsed-time + heart-rate pair that S-03's scoring will consume.

**Contract**: Class `SegmentEffort` in namespace `StravaSegmentsPerformanceBackend.Models`. Properties: `Id` (int, PK), `ActivityId` (int, FK to `Activity`), `StravaEffortId` (long), `StravaSegmentId` (long), `SegmentName` (string), `ElapsedTimeSeconds` (int), `AverageHeartRate` (double?, nullable — not every athlete/activity has HR data), `StartDateUtc` (DateTime).

#### 3. WorkoutFetchStatus entity

**File**: `strava-segments-performance-backend/Models/WorkoutFetchStatus.cs`

**Intent**: One row per user tracking the state of the most recent (or in-progress) fetch job — what the frontend polls and what the trigger endpoint's single-flight check reads.

**Contract**: Class `WorkoutFetchStatus` in namespace `StravaSegmentsPerformanceBackend.Models`. Properties: `UserId` (int, PK and FK to `User`), `Status` (enum `FetchStatusState`: `Idle`, `Running`, `Completed`, `Failed`, `Interrupted`), `Stage` (enum `FetchStage`: `ListingActivities`, `FetchingDetails`, nullable), `ActivitiesProcessed` (int), `TotalToProcess` (int?, nullable — unknown until listing completes), `ErrorMessage` (string?, nullable), `StartedAtUtc` (DateTime?), `CompletedAtUtc` (DateTime?).

#### 4. DbContext updates

**File**: `strava-segments-performance-backend/Data/AppDbContext.cs`

**Intent**: Register the three new entities and their indexes/relationships.

**Contract**: Add `DbSet<Activity> Activities`, `DbSet<SegmentEffort> SegmentEfforts`, `DbSet<WorkoutFetchStatus> WorkoutFetchStatuses`. In `OnModelCreating`: unique composite index on `Activity(UserId, StravaActivityId)`; unique index on `SegmentEffort(StravaEffortId)`; `WorkoutFetchStatus.UserId` as primary key (one-to-one with `User`).

#### 5. Migration

**Intent**: Generate and apply the EF Core migration for the three new tables.

**Contract**: Run `dotnet ef migrations add AddWorkoutFetching` from the backend directory; verify it creates `Activities`, `SegmentEfforts`, and `WorkoutFetchStatuses` tables with the indexes above.

### Success Criteria:

#### Automated Verification:

- Project builds cleanly: `dotnet build`
- Migration file generated in `Migrations/`
- `dotnet ef database update` succeeds against the running PostgreSQL container

#### Manual Verification:

- Connect to PostgreSQL and confirm `Activities`, `SegmentEfforts`, `WorkoutFetchStatuses` tables exist with the expected columns and indexes

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 2: Frontend Fetch UX

### Overview

Build the dashboard fetch UI and polling service against the endpoint contract, ahead of the backend that implements it. This gives the rest of the plan a fast, visible target — Phase 3 and Phase 4 are verified by watching this UI, not by inspecting raw HTTP responses.

### Changes Required:

#### 1. Workout fetch service

**File**: `strava-segments-performance/src/app/workouts/workout-fetch.service.ts`

**Intent**: Encapsulate the trigger call and status polling, exposing reactive state via signals — mirrors `AuthService`'s structure.

**Contract**: Injectable service with a `status` signal holding `{ status: 'idle'|'running'|'completed'|'failed'|'interrupted', stage: 'listing'|'fetching_details'|null, activitiesProcessed: number, totalToProcess: number|null, errorMessage: string|null }`. `trigger()` — `POST {apiBaseUrl}/api/workouts/fetch` with `withCredentials: true`, sets `status` from the response, and starts polling. Internal polling: RxJS `interval(2000)` + `switchMap` to `GET {apiBaseUrl}/api/workouts/fetch-status`, updating the `status` signal, `takeWhile` the status is `running`, stopping (inclusive) once `completed`/`failed`/`interrupted`. `checkStatus()` — one-shot status fetch, called on component init so a page refresh mid-fetch resumes polling instead of showing a stale idle state. These endpoints don't exist yet (Phase 3 adds them) — the service is written against the contract now so no frontend code changes are needed once the backend lands.

#### 2. Dashboard integration

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.ts` (+ `.html`, `.scss`)

**Intent**: Replace the empty-state placeholder with the fetch trigger UI, driven by `WorkoutFetchService`.

**Contract**: Inject `WorkoutFetchService`; call `checkStatus()` on init. Template renders conditionally on `status()`: `idle`/`interrupted` → "Fetch my workouts" button calling `trigger()` (label reads "Resume fetch" when `interrupted`, since the same call resumes); `running` → spinner + staged text ("Listing your rides..." during `listing`, "Fetching segment details... {{activitiesProcessed}} of {{totalToProcess}}" during `fetching_details`); `completed` → summary text ("{{activitiesProcessed}} cycling activities cached") + a "Check for new rides" button (calls `trigger()` again); `failed` → error banner with `errorMessage` + retry button.

### Success Criteria:

#### Automated Verification:

- Frontend builds cleanly: `npm run build`
- TypeScript strict mode passes

**Implementation Note**: This phase has no live backend to exercise yet — `checkStatus()`/`trigger()` will 404 or connection-error against a backend that doesn't have these routes until Phase 3. Automated build/typecheck is the gate here; verify visual states by temporarily hardcoding the `status` signal's value in the browser console if you want to eyeball each UI state early. Full interactive manual verification happens in Phase 3, once the endpoints and worker exist and this UI can be exercised end-to-end with zero further UI changes. After the build passes, proceed to Phase 3 without a manual pause.

---

## Phase 3: Background Fetch Worker & Trigger Endpoints (minimal Strava client)

### Overview

Wire up the channel-fed `BackgroundService`, the trigger/status endpoints, single-flight protection, and the startup reset of stale `Running` statuses — backed by a deliberately minimal Strava client (no refresh, no rate-limit backoff yet, hardened in Phase 4). This is where the Phase 2 UI first shows real, live progress end-to-end.

### Changes Required:

#### 1. Strava response DTOs and cycling-type filter

**File**: `strava-segments-performance-backend/Services/StravaDtos.cs`

**Intent**: Model the subset of Strava's JSON responses this feature needs, and define which `sport_type` values count as cycling.

**Contract**: Records/classes `StravaActivitySummary` (id, name, sport_type, start_date, distance, moving_time, elapsed_time, has_heartrate) and `StravaActivityDetail` (extends summary + `segment_efforts` array of `StravaSegmentEffort` with id, segment.id, segment.name, elapsed_time, average_heartrate). A `static readonly HashSet<string> CyclingSportTypes` containing `"Ride"`, `"MountainBikeRide"`, `"GravelRide"`, `"EBikeRide"`, `"EMountainBikeRide"`, `"VirtualRide"`, `"Handcycle"`, `"Velomobile"`. Mapping extension methods to `Activity`/`SegmentEffort` entities. The `ToActivity()` mapping populates all summary-level fields (Name, SportType, StartDateUtc, DistanceMeters, MovingTimeSeconds, ElapsedTimeSeconds) from the list response so that the detail fetch only needs to add segment efforts.

#### 2. Minimal Strava API client

**File**: `strava-segments-performance-backend/Services/StravaApiClient.cs`

**Intent**: First working version of the Strava HTTP layer. Deliberately skips token refresh and rate-limit backoff (added in Phase 4) — decrypts and uses the user's current stored access token directly, and lets non-success responses (401, 429, etc.) propagate as exceptions the worker catches and records as a `Failed` status. This gets a real, visible end-to-end fetch working quickly for small test cases; reliability at production scale is hardened once the happy path is proven.

**Contract**: Typed client (`AddHttpClient<StravaApiClient>`) with base address `https://www.strava.com/api/v3/`. Methods: `Task<IReadOnlyList<StravaActivitySummary>> ListActivitiesPageAsync(User user, int page, int perPage, CancellationToken ct)` and `Task<StravaActivityDetail> GetActivityDetailAsync(User user, long stravaActivityId, CancellationToken ct)`. Both attach `Authorization: Bearer {token}` using `TokenEncryptionService.Decrypt(user.AccessToken)` directly (no refresh check), and call `EnsureSuccessStatusCode()` so any failure (expired token, rate limit, etc.) throws and surfaces as a `Failed` fetch status. Phase 4 will change the internals of these same methods without changing their signatures.

#### 3. Fetch channel

**File**: `strava-segments-performance-backend/Services/WorkoutFetchChannel.cs`

**Intent**: Decouple the trigger endpoint (producer) from the background worker (single consumer) via an in-process queue.

**Contract**: Singleton class wrapping `System.Threading.Channels.Channel<int>` (unbounded), exposing `Writer` and `Reader`.

#### 4. Background worker

**File**: `strava-segments-performance-backend/Services/WorkoutFetchWorker.cs`

**Intent**: The core orchestration loop: dequeue a user ID, list all their cycling activities (inserting new stub rows), then fetch details for everything not yet `DetailsFetched`, updating `WorkoutFetchStatus` throughout. Runs one user at a time, per Implementation Approach.

**Contract**: `WorkoutFetchWorker : BackgroundService`. `ExecuteAsync` reads from `WorkoutFetchChannel.Reader` in a loop; for each user ID, creates an `IServiceScopeFactory` scope (per Critical Implementation Details) and runs: set status `Running`/stage `ListingActivities`; paginate `StravaApiClient.ListActivitiesPageAsync` until a short page is returned, filtering to `CyclingSportTypes` **and** `has_heartrate == true` (activities without HR data are skipped — they cannot contribute to fitness scoring which requires HR), inserting new `Activity` rows with full summary data from the list response (skip existing `StravaActivityId`s), updating `ActivitiesProcessed` as a running count of newly discovered activities; set stage `FetchingDetails`, reset `ActivitiesProcessed` to 0, set `TotalToProcess` to the count of `DetailsFetched == false` rows for the user; for each, call `GetActivityDetailAsync`, map segment efforts, save, mark `DetailsFetched = true`, increment `ActivitiesProcessed`, `SaveChangesAsync` per activity (per the incremental-commit decision); on success set status `Completed`; on unhandled exception, set status `Failed` with `ErrorMessage`, log, and continue the outer loop for the next queued user (one user's failure must not stop the worker).

#### 5. Startup reset of stale Running status

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: A `Running` status left over from before an app restart would otherwise permanently block that user from re-triggering (per Critical Implementation Details).

**Contract**: In the existing startup scope block (alongside `Database.MigrateAsync()`), bulk-update any `WorkoutFetchStatus` rows with `Status == Running` to `Status == Interrupted`.

#### 6. Trigger and status endpoints

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Expose the two endpoints the frontend needs, following the existing auth-endpoint pattern (`ctx.User.FindFirst(ClaimTypes.NameIdentifier)`).

**Contract**: Both endpoints read `ClaimTypes.NameIdentifier` from the cookie to get the `StravaAthleteId`, then look up the `User` row via `db.Users.FirstAsync(u => u.StravaAthleteId == stravaId)` to obtain the DB `User.Id` (int PK) — the existing `/api/auth/me` pattern only reads claims, but these endpoints need the DB entity.
- `POST /api/workouts/fetch` — requires authorization; loads the user's `WorkoutFetchStatus` by `User.Id`; uses an atomic conditional update (`ExecuteUpdateAsync` with a `WHERE Status != Running` filter, or equivalent single-statement upsert) so that concurrent requests (double-click, multiple tabs) cannot both enqueue — if the atomic update affects 0 rows, the status is already `Running` and the endpoint returns 200 with the current status unchanged; otherwise it sets `Running`/`ListingActivities`/resets counts, writes the user ID to `WorkoutFetchChannel.Writer`, and returns 202 with the new status.
- `GET /api/workouts/fetch-status` — requires authorization; returns the user's `WorkoutFetchStatus` (or an `Idle` default if no row exists yet) as `{ status, stage, activitiesProcessed, totalToProcess, errorMessage }`.

#### 7. Service registration

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Register the channel, hosted service, and the minimal Strava client.

**Contract**: `builder.Services.AddHttpClient<StravaApiClient>()`; `builder.Services.AddSingleton<WorkoutFetchChannel>()`; `builder.Services.AddHostedService<WorkoutFetchWorker>()`.

### Success Criteria:

#### Automated Verification:

- Project builds cleanly: `dotnet build`

#### Manual Verification:

- `POST /api/workouts/fetch` returns 401 when unauthenticated (verify via `curl -X POST http://localhost:5000/api/workouts/fetch -w '%{http_code}'`)
- `GET /api/workouts/fetch-status` returns 401 when unauthenticated (verify via `curl http://localhost:5000/api/workouts/fetch-status -w '%{http_code}'`)
- With a real connected Strava account (reusing the OAuth session from S-01), click "Fetch my workouts" in the Phase 2 UI and watch staged progress update live, end-to-end, for the first time
- Confirm `Activities` and `SegmentEfforts` rows appear in PostgreSQL with plausible data
- Triggering again while `Running` does not create a second concurrent job (status stays consistent)
- Stop (`Ctrl+C`) `dotnet run` mid-fetch, restart, confirm the status resets to `Interrupted` in the UI, then re-trigger and confirm already-`DetailsFetched` activities are not re-fetched (verify via row counts / no duplicate Strava calls in logs)
- Keep the test account's activity count small for this phase — token refresh and 429 handling aren't implemented until Phase 4, so a long-running fetch here is expected to fail rather than self-heal; that's expected, not a bug to chase down now

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 4: Strava Reliability Hardening — Token Refresh & Rate-Limit Backoff

### Overview

Harden the Phase 3 minimal Strava client with proactive/reactive token refresh and 429 wait-and-retry, so a full-history fetch survives the hours it can legitimately take. The UI (Phase 2) and worker (Phase 3) need no changes — this phase only edits `StravaApiClient.cs` and adds the token service behind it.

### Changes Required:

#### 1. Token service

**File**: `strava-segments-performance-backend/Services/StravaTokenService.cs`

**Intent**: Own the logic for getting a currently-valid decrypted access token for a user, proactively refreshing via Strava's OAuth token endpoint when expired, and persisting the refreshed (re-encrypted) tokens.

**Contract**: `Task<string> GetValidAccessTokenAsync(User user, CancellationToken ct)` — if `user.TokenExpiresAtUtc` is within a short buffer of now, POST to `https://www.strava.com/oauth/token` with `grant_type=refresh_token`, update `user.AccessToken`/`RefreshToken`/`TokenExpiresAtUtc` (encrypted via `TokenEncryptionService`), save via `AppDbContext`, and return the new decrypted access token; otherwise decrypt and return the current one. Also exposes `Task<string> ForceRefreshAsync(User user, CancellationToken ct)` for the 401-triggered fallback path.

#### 2. Harden the API client

**File**: `strava-segments-performance-backend/Services/StravaApiClient.cs` (edit — existing file from Phase 3)

**Intent**: Replace the Phase 3 shortcut of using the user's stored access token directly with `StravaTokenService`-backed retrieval (proactive refresh), add a 401-triggered forced-refresh-and-retry fallback, and add 429 wait-and-retry via an injected `TimeProvider`. Method signatures are unchanged, so the Phase 3 worker requires no modification.

**Contract**: `ListActivitiesPageAsync`/`GetActivityDetailAsync` now get their bearer token from `StravaTokenService.GetValidAccessTokenAsync`; on `401`, call `ForceRefreshAsync` once and retry; on `429`, wait per the Critical Implementation Details rate-limit strategy and retry the same call — sample contract for the retry-on-429 loop:

```csharp
while (true)
{
    var response = await _httpClient.SendAsync(request, ct);
    if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;
    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(15);
    await Task.Delay(wait, _timeProvider, ct);
    request = CloneRequest(request); // HttpRequestMessage can't be resent
}
```

#### 3. Registration

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Wire the new pieces into DI.

**Contract**: `builder.Services.AddSingleton(TimeProvider.System)`; `builder.Services.AddScoped<StravaTokenService>()`.

### Success Criteria:

#### Automated Verification:

- Project builds cleanly: `dotnet build`

#### Manual Verification:

- Re-run the same trigger flow from Phase 3 (the button already exists) and confirm no regression on the happy path
- Force a refresh: manually set a test user's `TokenExpiresAtUtc` to a past timestamp in PostgreSQL, then trigger a fetch and confirm it completes without a "please reconnect" failure — the `Users` row should show a refreshed token/expiry afterward
- Reliably triggering a real Strava `429` by hand is impractical — backoff correctness is primarily verified by the automated tests in Phase 5; this phase's manual check is limited to the refresh scenario above

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 5: Testing & Integration Verification

### Overview

Add automated coverage for the highest-risk logic (the Strava client), and run full end-to-end verification through Docker Compose.

### Changes Required:

#### 1. Backend test project

**File**: `strava-segments-performance-backend.Tests/strava-segments-performance-backend.Tests.csproj`

**Intent**: Stand up the first backend test project (none currently exists), referencing the main project.

**Contract**: xUnit test project (`dotnet new xunit`) targeting `net10.0`, with a project reference to `strava-segments-performance-backend.csproj` and a package reference to `Microsoft.Extensions.TimeProvider.Testing` (for `FakeTimeProvider`).

#### 2. Strava API client tests

**File**: `strava-segments-performance-backend.Tests/StravaApiClientTests.cs`

**Intent**: Cover the non-obvious behaviors flagged in Critical Implementation Details, since they're the hardest to verify manually.

**Contract**: Using a fake `HttpMessageHandler` and `FakeTimeProvider`: (1) pagination stops correctly when a page shorter than `perPage` is returned; (2) a `401` response triggers exactly one token refresh and one retry, after which the refreshed token is used; (3) a `429` response with `Retry-After` causes the client to await via `TimeProvider` (advanced instantly by the fake, not a real wait) and retry the same request; (4) mapping filters out non-cycling `sport_type` values; (5) mapping filters out activities with `has_heartrate == false`; (6) mapping handles a `null average_heartrate` without throwing.

### Success Criteria:

#### Automated Verification:

- New test project builds: `dotnet build`
- All tests pass: `dotnet test`

#### Manual Verification:

- Full Docker flow: `docker compose up`, log in, trigger fetch, watch staged progress in the UI, confirm data lands in PostgreSQL
- `docker compose restart backend` mid-fetch, confirm the UI surfaces the interrupted state and re-triggering resumes without re-fetching already-cached activity details
- Triggering a second time after a completed fetch (with no new Strava activities) completes quickly and processes zero new activities

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Testing Strategy

### Unit Tests:

- Strava client: pagination termination, 401-refresh-retry, 429-backoff-retry, cycling-type filtering, HR-data filtering, null HR handling

### Integration Tests:

- Backend: `POST /api/workouts/fetch` / `GET /api/workouts/fetch-status` return 401 when unauthenticated

### Manual Testing Steps:

1. `docker compose up`, log in via Strava
2. Click "Fetch my workouts" on the dashboard
3. Observe staged progress text update (listing → fetching details X of Y → completed count)
4. Check PostgreSQL: `Activities` and `SegmentEfforts` tables have rows for cycling activities only
5. `docker compose restart backend` mid-fetch
6. Reload the dashboard — status shows interrupted; click to resume
7. Confirm already-`DetailsFetched` activities are not re-fetched (row counts don't reset, logs show no duplicate detail calls)
8. Trigger again after completion with no new Strava rides — completes quickly, zero new rows

## Performance Considerations

- A single sequential background worker deliberately caps Strava API concurrency at 1 in-flight fetch job app-wide, which matches Strava's app-level (not per-athlete) rate limit — this is correctness, not just simplicity.
- Per-activity `SaveChangesAsync` (incremental commit) trades a small amount of DB round-trip overhead for crash-safety; acceptable given fetch is already rate-limit-bound, not DB-bound.

## Migration Notes

- New tables (`Activities`, `SegmentEfforts`, `WorkoutFetchStatuses`) are additive — no changes to the existing `Users` table or migration history.
- No backfill needed; every user starts with zero cached activities until they trigger their first fetch.

## References

- Roadmap slice: `context/foundation/roadmap.md` (S-02)
- PRD: `context/foundation/prd.md` (FR-003, business logic section)
- Prior implementation (S-01): `context/changes/strava-oauth-login/plan.md`
- Backend entry point: `strava-segments-performance-backend/Program.cs`
- Existing auth pattern: `strava-segments-performance-backend/Program.cs:137-147`
- Frontend dashboard empty state: `strava-segments-performance/src/app/dashboard/dashboard.component.html:7-9`
- Frontend HTTP/signals pattern: `strava-segments-performance/src/app/auth/auth.service.ts`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Data Model & Migrations

#### Automated

- [x] 1.1 Project builds cleanly: `dotnet build` — 574b951
- [x] 1.2 Migration file generated in `Migrations/` — 574b951
- [x] 1.3 `dotnet ef database update` succeeds — 574b951

#### Manual

- [x] 1.4 `Activities`, `SegmentEfforts`, `WorkoutFetchStatuses` tables exist with correct columns/indexes — 574b951

### Phase 2: Frontend Fetch UX

#### Automated

- [x] 2.1 Frontend builds cleanly: `npm run build`
- [x] 2.2 TypeScript strict mode passes

### Phase 3: Background Fetch Worker & Trigger Endpoints (minimal Strava client)

#### Automated

- [ ] 3.1 Project builds cleanly: `dotnet build`

#### Manual

- [ ] 3.2 `POST /api/workouts/fetch` returns 401 when unauthenticated (curl)
- [ ] 3.3 `GET /api/workouts/fetch-status` returns 401 when unauthenticated (curl)
- [ ] 3.4 Trigger via the Phase 2 UI shows live staged progress end-to-end for the first time
- [ ] 3.5 `Activities`/`SegmentEfforts` rows appear in PostgreSQL
- [ ] 3.6 Re-triggering while running does not create a duplicate job
- [ ] 3.7 Restart mid-fetch → status becomes Interrupted in the UI → re-trigger resumes without re-fetching cached details

### Phase 4: Strava Reliability Hardening — Token Refresh & Rate-Limit Backoff

#### Automated

- [ ] 4.1 Project builds cleanly: `dotnet build`

#### Manual

- [ ] 4.2 Happy-path fetch still works with no regression
- [ ] 4.3 Forcing an expired token still completes the fetch (refresh works)

### Phase 5: Testing & Integration Verification

#### Automated

- [ ] 5.1 Test project builds: `dotnet build`
- [ ] 5.2 All tests pass: `dotnet test`

#### Manual

- [ ] 5.3 Full Docker flow: login → trigger → progress → cached data in Postgres
- [ ] 5.4 `docker compose restart backend` mid-fetch → UI shows interrupted → resume works
- [ ] 5.5 Re-trigger after completion with no new activities completes quickly, zero new rows
