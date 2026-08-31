# Fitness Trend Chart (S-03) Implementation Plan

## Overview

Score each cached cycling workout on a self-relative 0–100 fitness scale from its segment efforts (elapsed time + average HR), expose the per-workout series over a new authenticated endpoint, and render it as a line chart on the dashboard once workout data has been fetched. This is roadmap slice **S-03**, the north-star flow that proves the core product hypothesis (segment-level, HR-aware scoring surfaces fitness trends).

## Current State Analysis

- **Data is already sufficient — no schema change.** Per segment effort we persist `StravaSegmentId`, `ElapsedTimeSeconds`, `AverageHeartRate` (nullable), `StartDateUtc`, and `ActivityId` ([Models/SegmentEffort.cs](strava-segments-performance-backend/Models/SegmentEffort.cs)). Activities carry `UserId`, `SportType`, `StartDateUtc` ([Models/Activity.cs](strava-segments-performance-backend/Models/Activity.cs)). Activities are already filtered to cycling + has-heartrate at fetch time.
- **No scoring/analysis code exists** — greenfield. Existing endpoints in [Program.cs](strava-segments-performance-backend/Program.cs) follow a minimal-API pattern: inject `AppDbContext db` (and other services) into the handler lambda, resolve the current user from the `ClaimTypes.NameIdentifier` claim → `User.Id`, `.RequireAuthorization()` (see `Program.cs:190-239`).
- **No FK/nav props** — join `SegmentEfforts.ActivityId → Activities.Id` manually in LINQ; `SegmentEfforts` has no `UserId`, so user scoping goes through the join.
- **A pure xUnit test project exists** ([strava-segments-performance-backend-tests](strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj)) referencing the backend project — so scoring logic written as a pure class is directly unit-testable with in-memory fixtures, no DB.
- **Frontend has no chart code and no chart dependency** (deps: only `@angular/*` ^21.2.0, `rxjs`, `tslib`). The service/state idiom to mirror is [workouts/workout-fetch.service.ts](strava-segments-performance/src/app/workouts/workout-fetch.service.ts): `providedIn:'root'`, `HttpClient` with `{ withCredentials: true }`, state in a `signal<>()`, response interface declared in-file, errors routed to a private `setFailed()`. The dashboard renders a `@switch` on fetch status; the `@case ('completed')` branch ([dashboard/dashboard.component.html:34-39](strava-segments-performance/src/app/dashboard/dashboard.component.html)) is the chart's insertion point.

## Desired End State

After this plan: an authenticated user who has fetched workouts sees, in the dashboard's completed state, a line chart of their fitness score (0–100, y-axis fixed) over time — one point per scored workout, x-axis by workout date. The best workout in the window reads ~100, the worst ~0. The scoring algorithm is covered by unit tests asserting each pipeline stage and every edge case. The endpoint accepts optional `from`/`to` params (unused by the v1 UI, ready for S-04).

Verify: `dotnet test` passes (scoring + endpoint); `GET /api/analysis/fitness-trend` returns a plausible series for a user with cached repeated-segment workouts; the dashboard chart renders and hovering a point shows its date + score.

### Key Discoveries:

- Scoring inputs all persisted; nullable HR must be handled ([SegmentEffort.cs:11](strava-segments-performance-backend/Models/SegmentEffort.cs)).
- Current-user resolution pattern: `long.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value)` → `db.Users.FirstAsync(u => u.StravaAthleteId == stravaId)` ([Program.cs:192-193](strava-segments-performance-backend/Program.cs)).
- Service-to-mirror for the frontend: [workout-fetch.service.ts](strava-segments-performance/src/app/workouts/workout-fetch.service.ts) (signals + `withCredentials`).
- ng2-charts@10 requires `@angular/cdk` as a peer dep (not currently installed).

## What We're NOT Doing

- **No schema change / no new persisted fields.** Scores are recomputed per request, not stored. `moving_time`, power, grade, distance stay unpersisted.
- **No Efficiency-Factor / power-based formula.** `C = HR·t` is locked for v1 (see research DECISION); EF is a documented future iteration.
- **No HR-reserve baseline subtraction and no speed→power weighting** (declined for v1, self-mitigating — see research).
- **No smoothed/rolling-average trend overlay** — raw per-workout line only (PRD "one data point per workout").
- **No timeframe-selection UI** — that is S-04. This plan only makes the endpoint forward-compatible via optional params.
- **No HR-signature stop detector** — v1 ships the `t > k·median` drop only; the signature gate is a fast-follow.
- **No new DB indexes** — dataset is small; revisit only if the grouping query is slow.

## Implementation Approach

Front-load the core risk: implement and unit-test the scoring algorithm as a **pure, dependency-free class** over an in-memory list of effort records (Phase 1), before any HTTP/DB wiring. Phase 2 is thin glue — load user-scoped efforts, project to the scorer's input type, call it, return a DTO. Phase 3 mirrors the existing frontend service/signal idiom and adds the one chart component. Keep every pipeline stage (per-effort measure, per-segment normalization, stall hygiene, aggregation, rescale) as separately-testable functions so the formula can be iterated without reworking the wiring.

## Critical Implementation Details

- **Scoring must be a pure class, not inline in the endpoint.** The unit-test strategy (per-stage + edge cases) depends on invoking the algorithm with hand-built fixtures and no DB. The endpoint projects EF query results into the scorer's plain input records and calls it.
- **Window semantics.** Normalization (per-segment percentile and the final rescale) is a batch over the efforts *inside the window*. The `from`/`to` filter must be applied when loading efforts, before scoring — the same effort scores differently under different windows by design.
- **Determinism for percentile ties.** Equal `C` values on a segment must resolve to the same rank (average-rank on ties) so scores are stable across runs; assert this in tests.

## Phase 1: Fitness scoring algorithm (pure + unit-tested)

### Overview

A dependency-free scorer that turns a flat list of segment-effort records into a per-workout `(date, score)` series, implementing the locked pipeline, plus the full xUnit suite. No DB, no HTTP.

### Changes Required:

#### 1. Scorer input/output types

**File**: `strava-segments-performance-backend/Services/FitnessScoring.cs` (new)

**Intent**: Define the plain input record the scorer consumes (decoupled from EF entities so it is trivially testable) and the output point type.

**Contract**: An input record carrying, per effort: `long StravaSegmentId`, `int ElapsedTimeSeconds`, `double? AverageHeartRate`, `int ActivityId`, `DateTime WorkoutStartUtc`. An output record `FitnessTrendPoint(DateTime Date, double Score)`. Namespace `StravaSegmentsPerformanceBackend.Services`.

#### 2. The scoring pipeline

**File**: `strava-segments-performance-backend/Services/FitnessScoring.cs`

**Intent**: Implement the locked algorithm as a pure static (or injectable) method `IReadOnlyList<FitnessTrendPoint> Score(IEnumerable<effort-record>)`, composed of individually-testable internal steps so stages can be iterated independently.

**Contract**: Ordered pipeline, each step a private function:
1. **Ingest + HR filter**: drop efforts with null `AverageHeartRate`.
2. **Stall drop**: per `StravaSegmentId`, compute `medianElapsed`; drop efforts with `ElapsedTimeSeconds > K_STALL * medianElapsed` (`const double K_STALL = 2.0`).
3. **Per-effort cost**: `C = AverageHeartRate * ElapsedTimeSeconds`.
4. **Per-segment percentile**: group by `StravaSegmentId`; for segments with ≥2 efforts, `p_e = 100 * (count of efforts with C worse than e) / (N-1)`, average-rank on ties, best→100 worst→0. Segments with <2 efforts contribute nothing.
5. **Per-workout aggregation**: group scored efforts by `ActivityId`; `S_w = Σ(w_s · p_e) / Σ(w_s)` where `w_s` = that segment's median elapsed time (per-segment constant, not the effort's own time). Workouts with fewer than `MIN_SCORED_EFFORTS = 3` scored efforts produce no point (added post-implementation, per user decision: one or two repeated segments is too thin a sample to call a fitness trend).
6. **Window rescale**: `F_w = 100 * (S_w - Smin) / (Smax - Smin)` across all scored workouts; if `Smax == Smin` (single scored workout) emit that workout at score 50. Output sorted by workout date ascending.

Pure C#/LINQ, no I/O. No user-facing parameters; `K_STALL` is an internal constant.

#### 3. Unit tests

**File**: `strava-segments-performance-backend-tests/FitnessScoringTests.cs` (new)

**Intent**: Lock the algorithm's contract stage-by-stage and cover every edge case (the PRD's core risk).

**Contract**: xUnit `[Fact]`/`[Theory]` tests over hand-built fixtures asserting: (a) monotonicity — faster and/or lower-HR effort scores higher on a segment; (b) best workout in window → ~100, worst → ~0; (c) NULL-HR effort dropped; (d) N=1 segment contributes nothing but its workout survives on other segments; (e) workout with no repeated segments → absent from series (gap); (f) stall drop — an effort with `t > 2·median` is removed and does not drag its workout; (g) percentile tie → equal scores (determinism); (h) single scored workout → score 50; (i) segment-median weighting — a long segment outweighs a short one, and a stalled effort cannot buy outsized weight; (j) output sorted by date.

### Success Criteria:

#### Automated Verification:

- Backend builds: `dotnet build strava-segments-performance-backend`
- Scoring unit tests pass: `dotnet test strava-segments-performance-backend-tests`

#### Manual Verification:

- Spot-check a small hand-computed fixture against the test's expected scores to confirm the math matches intuition.

**Implementation Note**: After automated verification passes, pause for manual confirmation before Phase 2.

---

## Phase 2: Analysis API endpoint

### Overview

Expose the scorer over `GET /api/analysis/fitness-trend`, loading the current user's efforts (optionally windowed) and returning the series as JSON.

### Changes Required:

#### 1. Response DTO

**File**: `strava-segments-performance-backend/Program.cs` (or a small DTO alongside the scorer)

**Intent**: A serialization shape for the series the frontend consumes.

**Contract**: An array of `{ date: ISO-8601 string, score: number }`. Reuse `FitnessTrendPoint` if its JSON shape is acceptable, else a thin projection.

#### 2. The endpoint

**File**: `strava-segments-performance-backend/Program.cs` (insert before `app.Run();` at `Program.cs:241`)

**Intent**: Resolve the user, load their segment efforts joined to activities (optionally filtered by window), project to the scorer input, run the scorer, return the series.

**Contract**: `app.MapGet("/api/analysis/fitness-trend", async (HttpContext ctx, AppDbContext db, DateTime? from, DateTime? to) => {...}).RequireAuthorization();`
- Resolve user via the established `ClaimTypes.NameIdentifier` → `db.Users.FirstAsync(...)` pattern (`Program.cs:192-193`).
- Query: `from e in db.SegmentEfforts join a in db.Activities on e.ActivityId equals a.Id where a.UserId == user.Id` plus optional `a.StartDateUtc >= from` / `<= to`; select the scorer's input record (include `a.StartDateUtc` as `WorkoutStartUtc`).
- Call `FitnessScoring.Score(...)`, return `Results.Ok(series)`.
- Empty result (no scorable workouts) → `Results.Ok([])` (not an error); the frontend renders an empty state.

#### 3. Endpoint test

**File**: `strava-segments-performance-backend-tests/` (new test, following the existing test-project conventions)

**Intent**: Verify the endpoint's data path — user scoping and window filtering — over an in-memory/SQLite `AppDbContext` seeded with two users' efforts.

**Contract**: Assert the endpoint returns only the calling user's workouts, respects `from`/`to`, and returns `[]` for a user with no repeated-segment workouts. (If wiring an in-memory `AppDbContext` against the minimal-API handler is disproportionate, test the query+projection helper directly with a seeded context and keep the HTTP layer as manual verification — decide during implementation based on how the handler is factored.)

### Success Criteria:

#### Automated Verification:

- Backend builds: `dotnet build strava-segments-performance-backend`
- All backend tests pass: `dotnet test strava-segments-performance-backend-tests`

#### Manual Verification:

- With a logged-in session that has cached workouts, `GET /api/analysis/fitness-trend` returns a non-empty series of `{date, score}` with scores in [0,100] and the best/worst near 100/0.
- `?from=...&to=...` narrows the series and shifts scores (window-relative behavior visible).
- A user with no repeated segments gets `[]`.

**Implementation Note**: After automated verification passes, pause for manual confirmation before Phase 3.

---

## Phase 3: Frontend trend chart

### Overview

Add the charting dependency, an analysis service mirroring the fetch service, and a standalone chart component rendered in the dashboard's completed state.

### Changes Required:

#### 1. Charting dependency

**File**: `strava-segments-performance/package.json`

**Intent**: Add ng2-charts + Chart.js and the required peer dep.

**Contract**: `npm install ng2-charts chart.js @angular/cdk` (cdk pinned to ^21 to match Angular 21). Verify the app still builds after install.

#### 2. Analysis service

**File**: `strava-segments-performance/src/app/workouts/analysis.service.ts` (new)

**Intent**: Fetch the fitness-trend series and hold it in a signal, mirroring `WorkoutFetchService` exactly.

**Contract**: `@Injectable({ providedIn: 'root' })`. Declares `interface FitnessTrendPoint { date: string; score: number; }` in-file. Holds `series = signal<FitnessTrendPoint[] | null>(null)` and a `loadState = signal<'idle'|'loading'|'loaded'|'error'>('idle')` (or equivalent). A `load()` method GETs `${environment.apiBaseUrl}/api/analysis/fitness-trend` with `{ withCredentials: true }`, sets the signal on success, routes errors to a private `setFailed()`-style handler. No polling (synchronous single request).

#### 3. Chart component

**File**: `strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.ts` (+ `.html`, `.scss`) (new)

**Intent**: Render the series as a 0–100 line chart with hover tooltips, following the codebase's standalone + signals + separate-template conventions.

**Contract**: `standalone: true`, `imports: [BaseChartDirective]`, separate `templateUrl`/`styleUrl`. Takes the series via `input.required<FitnessTrendPoint[]>()` (or reads the service signal). Chart config: `type: 'line'`, y-axis `min: 0, max: 100`, x-axis by date (pre-formatted string labels to avoid a date-adapter dep), `pointRadius` small/0 with `tooltip { intersect: false, mode: 'index' }` so hover shows date + score, line color Strava orange `#fc4c02`. Register only the needed Chart.js pieces (`LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip`). Empty series → a simple "No fitness data yet" message instead of an empty chart.

#### 4. Dashboard integration

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.ts` + `dashboard.component.html`

**Intent**: Load the analysis when fetch is complete and render the chart in the completed branch.

**Contract**: Inject `AnalysisService` (public, per the existing DI-for-template idiom). Trigger `analysisService.load()` when status becomes `completed` — on `ngOnInit` if already completed, and after a successful fetch. In `dashboard.component.html`, inside `@case ('completed')` (`dashboard.component.html:34-39`), render `<app-fitness-trend-chart>` fed by the service's series signal (with the component's own empty/loading handling). Import the standalone chart component into the dashboard component's `imports`.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm run build` (in `strava-segments-performance/`)
- Existing unit tests pass: `npm test` (Karma)

#### Manual Verification:

- Log in with a session that has cached repeated-segment workouts → the dashboard completed state shows a line chart of score vs date.
- Y-axis is fixed 0–100; hovering a point shows its date and score.
- The best workout reads near 100 and the worst near 0.
- A user with no scorable workouts sees the empty-state message, not a broken chart.
- No console errors; chart is legible in the existing dashboard layout.

**Implementation Note**: Final phase — after automated + manual verification, the slice is complete.

---

## Testing Strategy

### Unit Tests:

- **Scoring pipeline (Phase 1)** — per-stage and per-edge-case assertions (monotonicity, endpoints, NULL HR, N=1, no-repeat workout, stall drop, tie determinism, single-workout window, segment-median weighting, sort order).
- **Endpoint data path (Phase 2)** — user scoping and window filtering over a seeded context.

### Integration Tests:

- Endpoint returns a valid `{date, score}[]` for a seeded multi-workout user; `[]` for a no-repeat user.

### Manual Testing Steps:

1. Fetch workouts, then load the dashboard → chart appears in the completed state.
2. Hover points → tooltips show date + score; axis fixed 0–100.
3. Confirm best/worst workouts sit near 100/0.
4. Hit the endpoint with `?from/&to` → series narrows and scores shift (window-relative).
5. Test a user/account with no repeated segments → empty state.

## Performance Considerations

Scoring is an in-memory `O(E log E)` batch (E = total efforts) recomputed per request — milliseconds for ~1000 workouts, well within the PRD's 30s budget. The `SegmentEfforts` grouping query has no supporting index; acceptable at current data volumes, revisit only if slow.

## Migration Notes

None — no schema change. Scores are computed on demand from existing cached data.

## References

- Research: `context/changes/fitness-trend-chart/research.md`
- PRD: `context/foundation/prd.md` (FR-003, FR-004, US-01, Business Logic)
- Roadmap: `context/foundation/roadmap.md` (S-03)
- Endpoint/auth pattern: `strava-segments-performance-backend/Program.cs:190-239`
- Service idiom to mirror: `strava-segments-performance/src/app/workouts/workout-fetch.service.ts`
- DTO mapping style: `strava-segments-performance-backend/Services/StravaDtos.cs:67-77`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Fitness scoring algorithm (pure + unit-tested)

#### Automated

- [x] 1.1 Backend builds: `dotnet build strava-segments-performance-backend` — a3ccad9
- [x] 1.2 Scoring unit tests pass: `dotnet test strava-segments-performance-backend-tests` — a3ccad9

#### Manual

- [x] 1.3 Spot-check a hand-computed fixture against the test's expected scores — a3ccad9

### Phase 2: Analysis API endpoint

#### Automated

- [x] 2.1 Backend builds: `dotnet build strava-segments-performance-backend` — 6add13f
- [x] 2.2 All backend tests pass: `dotnet test strava-segments-performance-backend-tests` — 6add13f

#### Manual

- [x] 2.3 `GET /api/analysis/fitness-trend` returns a non-empty [0,100] series with best/worst near 100/0
- [x] 2.4 `?from&to` narrows the series and shifts scores (window-relative)
- [x] 2.5 A user with no repeated segments gets `[]`

### Phase 3: Frontend trend chart

#### Automated

- [x] 3.1 Frontend builds: `npm run build` — 91f2eb4
- [x] 3.2 Existing unit tests pass: `npm test` — 91f2eb4

#### Manual

- [x] 3.3 Completed state shows a line chart of score vs date
- [x] 3.4 Y-axis fixed 0–100; hover shows date + score
- [x] 3.5 Best workout near 100, worst near 0
- [x] 3.6 No-scorable-data user sees the empty state, not a broken chart
- [x] 3.7 No console errors; chart legible in the dashboard layout
