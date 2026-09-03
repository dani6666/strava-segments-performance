# Vertical-Slice Happy-Path E2E Implementation Plan

## Overview

Land the single browser e2e that proves Risk #7 from [test-plan.md](context/foundation/test-plan.md): an authenticated user's picker filters the fitness-trend chart end-to-end, exercised through the real backend chain. To make the assertion honest, close the picker→analysis wiring gap surfaced by [research.md](context/changes/testing-vertical-slice-happy-path/research.md) (§Summary, §Open Question 1) so the picker's `from`/`to` actually reach `/api/analysis/fitness-trend`, and add an E2E-only seed/reset surface so the spec is deterministic and cleans up after itself.

## Current State Analysis

- **Playwright runner is inherited from Phase 4**. Three projects (`setup` + `chromium` + `chromium-noauth`) already wired at [playwright.config.ts:73-89](strava-segments-performance/playwright.config.ts:73); `chromium` auto-consumes the storageState written by `auth.setup.ts` via `dependencies: ['setup']`. Any new `*.spec.ts` under `e2e/` (other than `oauth-handshake.spec.ts`) runs authenticated by default.
- **`/auth/test-login` seam already exists** for tests that need a real cookie session without the OAuth handshake ([Program.cs:189-217](strava-segments-performance-backend/Program.cs:189)), env-gated to `IsEnvironment("E2E")`. Same env gate is used by `/e2e-stub/*` OAuth endpoints ([Program.cs:219-252](strava-segments-performance-backend/Program.cs:219)) — the precedent for a new `/e2e/*` seed/reset surface.
- **CI is wired**. [.github/workflows/e2e-ci.yml:1-86](.github/workflows/e2e-ci.yml:1) provisions Postgres 17, warms `dotnet build`, installs Playwright, and runs `npm run test:e2e` with `CI=true`. No workflow change needed for Phase 5.
- **The vertical slice today**: [dashboard.component.html:22-75](strava-segments-performance/src/app/dashboard/dashboard.component.html:22) mounts the chart only when `fetchService.status().status === 'completed'` **and** `analysisService.loadState() === 'loaded'` **and** `series().length > 0` ([fitness-trend-chart.component.html:1-5](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.html:1)). The dashboard's `effect` at [dashboard.component.ts:22-28](strava-segments-performance/src/app/dashboard/dashboard.component.ts:22) auto-calls `analysisService.load()` on fetch-status transition to `completed`.
- **The wiring gap**: [analysis.service.ts:20-31](strava-segments-performance/src/app/workouts/analysis.service.ts:20) calls `GET /api/analysis/fitness-trend` with **no query params** even though the backend accepts `from`/`to` at [Program.cs:344-351](strava-segments-performance-backend/Program.cs:344). The picker's dates today only narrow the fetch window, not the trend.
- **No seed hooks exist** — grep across the backend for `HasData|SeedAsync|EnsureCreated|Seed(|IHostedService`-seeder returned nothing (research §Section E).
- **No `data-testid` in the frontend** — grep confirmed. Locators must be role/label/text (research §Section F).
- **Fixture floor for a non-empty trend that clears scoring gates**: ≥ 2 activities × ≥ 3 shared segments × HR-populated efforts within 2× per-segment median (research §Section E, mirroring the already-proven shape at [FitnessTrendQueryTests.cs:19-72](strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs:19)).

## Desired End State

- `npm run test:e2e` in `strava-segments-performance/` runs the existing OAuth-handshake spec **and** a new vertical-slice spec; both pass locally and in CI.
- The new spec proves: authenticated user lands on `/dashboard` → auto-analysis yields a 2-point trend (all seeded activities) → user narrows the picker to a range covering only one activity → analysis re-runs with `from`/`to` → chart canvas re-renders with a 1-point trend, asserted via `page.waitForResponse` on the analysis endpoint.
- The seeded rows are wiped when the run ends; a subsequent run starts from a clean fixture. Local re-runs against the persistent `strava_segments_e2e` DB stay deterministic.
- `AnalysisService.load()` accepts optional ISO date strings and forwards them as `from`/`to`. Picker changes reactively re-trigger analysis without a fetch call.
- Two new backend endpoints exist: `POST /e2e/seed` (scoped wipe + insert) and `POST /e2e/reset` (scoped wipe only), both gated to `IsEnvironment("E2E")`.

### Key Discoveries

- Two hard mounting gates before the chart appears — seeding rows alone is not enough; a `WorkoutFetchStatuses` row with `status='completed'` for the seed user is required (research §Section E).
- `SegmentEfforts` has no `UserId` column — scoping happens only via the manual join to `Activities.UserId` at [FitnessTrendQuery.cs:11-13](strava-segments-performance-backend/Services/FitnessTrendQuery.cs:11); the seed must derive `Activity.UserId` from the freshly-upserted user row.
- Playwright's `page.request` shares the browser-context cookie jar, so a `page.request.post(...)` in `auth.setup.ts` uses the already-set session cookie without extra plumbing ([auth.setup.ts:18-20](strava-segments-performance/e2e/auth.setup.ts:18)).
- The backend endpoint at [Program.cs:344-351](strava-segments-performance-backend/Program.cs:344) already accepts nullable `DateTime? from, DateTime? to` — no backend change needed to make the picker filter the trend.
- Scoring-gate math (research §Section E, [FitnessScoring.cs:16-33](strava-segments-performance-backend/Services/FitnessScoring.cs:16)): 2 workouts × 3 shared segments × HR-populated efforts within 2× median → 2 trend points (min → 0, max → 100), deterministic. Narrowing to one activity yields 1 point (the `50.0` tie-case at [FitnessScoring.cs:41-54](strava-segments-performance-backend/Services/FitnessScoring.cs:41)).

## What We're NOT Doing

- **No new component/edge-case tests for the chart itself.** [test-plan.md](context/foundation/test-plan.md) §3 Phase 6 owns the empty/sparse/normal series matrix. Phase 5 asserts render-succeeded + expected point count only.
- **No pixel or styling assertions.** [test-plan.md](context/foundation/test-plan.md) §7 keeps chart styling as negative space.
- **No `data-testid` culture.** The frontend has none today; Phase 5 uses role/label/text/CSS locators only (research §Section F). If a repo-wide testid convention becomes desired later, it's a separate change.
- **No Strava data-API stub.** The picker re-trigger is analysis-only (no fetch), so the fetch worker's Strava calls never fire in this test path. Extending `/e2e-stub/*` to cover activities/segment-efforts is out of scope.
- **No changes to the existing fetch button (`Check for new rides` / `Fetch my workouts` / `Resume fetch` / `Retry`).** Its behavior stays unchanged; the spec doesn't click it.
- **No new setup project.** The seed call is added inside the existing `auth.setup.ts`; no changes to Playwright projects.
- **No changes to `.github/workflows/e2e-ci.yml`.** The workflow already runs `npm run test:e2e`; the new spec is picked up automatically.
- **No fixture-shape unit tests for the seed endpoint.** The endpoint is E2E-only; Phase 3's spec is its truest test.

## Implementation Approach

Three self-contained phases in dependency order. Phase 1 adds the E2E-only backend surface (seed + reset). Phase 2 closes the picker→analysis wiring gap in the frontend. Phase 3 wires the Playwright setup/teardown and writes the single spec — the first phase where the composed slice actually runs.

Phase 1 and Phase 2 are independent — they touch different projects and can land in either order — but pairing them behind Phase 3 keeps the plan sequential and reviewable. The seed endpoint's shape is designed to be idempotent-by-wipe (research §Open Question 4 answer), so any fixture change downstream is immediate without a manual DB drop.

## Critical Implementation Details

**Timing & lifecycle.** The dashboard's fetch-completion `effect` at [dashboard.component.ts:22-28](strava-segments-performance/src/app/dashboard/dashboard.component.ts:22) fires on **transition** from a non-`completed` status to `completed`. When the seed pre-inserts a `WorkoutFetchStatuses` row with `status='completed'`, the initial signal value is `idle` and flips to `completed` after `checkStatus()` returns — the transition still fires. Do not "optimize" this by initializing the signal to the last known status; the transition detection would break.

**State sequencing.** The picker-driven analysis re-trigger (Phase 2) must debounce (recommended ~300ms) so a user typing a date in an `<input type="date">` doesn't spam the endpoint. The e2e spec must synchronize on `page.waitForResponse(...)` rather than `waitForTimeout` — see research §Section G for the assertion style borrowed from prior phases.

## Phase 1: Backend E2E seed + reset endpoints

### Overview

Add two E2E-only backend endpoints that let the Playwright setup deterministically populate and clear the fitness-trend inputs for the seed user, matching the wipe-and-insert semantics chosen in questioning.

### Changes Required

#### 1. Seed and reset endpoints in `Program.cs`

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Extend the existing `if (app.Environment.IsEnvironment("E2E")) { ... }` block that already houses `/auth/test-login` ([Program.cs:189-217](strava-segments-performance-backend/Program.cs:189)) with two new endpoints — `POST /e2e/seed` and `POST /e2e/reset` — so Playwright's `auth.setup.ts` can populate the trend fixture after login and a `globalTeardown` can clear it after the run. The seed inserts the deterministic fixture the vertical-slice spec asserts against; reset wipes only what the seed writes.

**Contract**:
- `POST /e2e/seed` (no body): resolves the current user via the cookie session (same `ClaimTypes.NameIdentifier` → `Users.StravaAthleteId` lookup used at [Program.cs:347-350](strava-segments-performance-backend/Program.cs:347)). If the session isn't authenticated, return `401` (`.RequireAuthorization()`). Then, in a transaction:
  1. Delete all `SegmentEfforts` whose parent `Activity.UserId == user.Id` (join via `Activities`).
  2. Delete all `Activities` where `UserId == user.Id`.
  3. Delete the `WorkoutFetchStatuses` row for `user.Id` if any.
  4. Insert the fixture: 2 `Activities` (`StravaActivityId=9000001` at `StartDateUtc=2026-08-15T10:00:00Z`, `StravaActivityId=9000002` at `StartDateUtc=2026-08-22T10:00:00Z`), 12 `SegmentEfforts` (segments `10`/`11`/`12` × 2 activities × 2 efforts per (activity, segment) pair; Activity 1 pair uses `ElapsedTimeSeconds=200/210, AverageHeartRate=160`; Activity 2 pair uses `ElapsedTimeSeconds=100/110, AverageHeartRate=140`; `StravaSegmentEffortId` pinned in the range `9100001..9100012`), and one `WorkoutFetchStatuses` row for `user.Id` with `status='completed'` (values match whatever the existing entity requires — read `Models/WorkoutFetchStatus.cs` before writing). **Why two efforts per (activity, segment)**: one-per-segment would leave a single-activity narrowing with only 1 effort per segment, failing FitnessScoring's `survivors.Count >= 2` gate and producing an empty trend. Doubling keeps each segment's history ≥ 2 within a single activity, so narrowing still clears the gate and the tie case at [FitnessScoring.cs:53](strava-segments-performance-backend/Services/FitnessScoring.cs:53) emits one 50.0 trend point.
  5. `SaveChangesAsync`; return `200 OK` with `{ userId, activities: 2, efforts: 12 }`.
- `POST /e2e/reset` (no body): same auth gate, same delete steps 1-3, no insert. Returns `200 OK` with `{ userId, deleted: true }`.
- Both wrapped in `if (app.Environment.IsEnvironment("E2E"))`. Not mapped under Development or Production.

**Why this fixture**: 2 activities × 3 shared segments × HR-populated efforts within 2× per-segment median satisfies the scoring gates ([FitnessScoring.cs:16-33](strava-segments-performance-backend/Services/FitnessScoring.cs:16), research §Section E) and produces exactly 2 trend points — the count the spec asserts against.

### Success Criteria

#### Automated Verification

- Backend still builds: `dotnet build strava-segments-performance-backend/strava-segments-performance-backend.csproj`
- Existing tests still pass: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`
- Lint passes: `dotnet format --verify-no-changes strava-segments-performance-backend/strava-segments-performance-backend.csproj`

#### Manual Verification

- With backend booted under `ASPNETCORE_ENVIRONMENT=E2E` and a valid session cookie (obtained via `/auth/test-login`), `curl -X POST -b cookies.txt http://localhost:5000/e2e/seed` returns `200` and inserts 2+6+1 rows in the `strava_segments_e2e` DB.
- A second call to `/e2e/seed` returns `200` and the row counts remain 2+6+1 (wipe-and-insert is idempotent).
- `POST /e2e/reset` returns `200` and leaves `Activities`/`SegmentEfforts`/`WorkoutFetchStatuses` empty for that user.
- Under Development env, `GET/POST /e2e/seed` returns `404` (endpoint not mapped).

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human before proceeding to Phase 2.

---

## Phase 2: Frontend picker → analysis wiring

### Overview

Close the seam gap: make `AnalysisService.load()` accept optional dates and forward them as `from`/`to` query params, and add a debounced `effect` in `DashboardComponent` that re-triggers analysis whenever the picker signals change. No new UI element; the existing picker inputs become the analyze trigger.

### Changes Required

#### 1. Extend `AnalysisService.load` signature and query threading

**File**: `strava-segments-performance/src/app/workouts/analysis.service.ts`

**Intent**: Add optional `from?: string` and `to?: string` parameters to `load()` and forward them as `HttpParams` on `GET /api/analysis/fitness-trend`. Empty/omitted values must not add empty query params (backend interprets absent as "no filter"). Response handling and state signal transitions stay unchanged.

**Contract**:
- Signature: `load(from?: string, to?: string): void`. Params are ISO date-time strings (produced by the picker helpers below).
- URL: `${apiBaseUrl}/api/analysis/fitness-trend` with `HttpParams` carrying `from` and/or `to` only when defined and non-empty.
- Backend already accepts these ([Program.cs:344](strava-segments-performance-backend/Program.cs:344)); no backend change needed.

#### 2. Reactive picker-driven re-trigger + initial-load wiring in `DashboardComponent`

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.ts`

**Intent**: Add an Angular `effect` (sibling of the existing fetch-completion effect at [dashboard.component.ts:22-28](strava-segments-performance/src/app/dashboard/dashboard.component.ts:22)) that reads `fetchService.fromDate()` / `fetchService.toDate()` and calls `analysisService.load(from, to)` when they change — debounced ~300ms via `setTimeout` + a stored timeout id (or `rxjs` `debounceTime` if preferred; see `WorkoutFetchService`'s existing timing patterns before choosing). Guard against firing before the initial fetch-status is `completed` (skip when `fetchService.status().status !== 'completed'`). Update the fetch-completion effect to pass the current picker dates on its first call, mirroring the same date translation used by `WorkoutFetchService.trigger()` (helpers at [workout-fetch.service.ts:31-40](strava-segments-performance/src/app/workouts/workout-fetch.service.ts:31)).

**Contract**:
- Date translation: reuse `startOfLocalDayUtcIso` / `startOfNextLocalDayUtcIso` from `WorkoutFetchService` (export them if currently private). Blank picker inputs → `undefined` → no query params on the request.
- Debounce: ~300ms. Any single test-observable re-trigger must synchronize on `page.waitForResponse`, not on a timeout.
- Do NOT touch the existing fetch-trigger effect logic beyond passing the current picker dates on its call to `analysisService.load`.

### Success Criteria

#### Automated Verification

- Frontend builds: `npm run build` in `strava-segments-performance/`
- Type check clean: implicit in the build
- Unit tests pass: `npm test` in `strava-segments-performance/`
- Lint passes: `npm run lint` if such a script exists; otherwise `prettier --check` per repo convention

#### Manual Verification

- With backend under `E2E` env and seed populated, on `/dashboard` the chart mounts with 2 points, then narrowing the `To` field to `2026-08-15` causes the chart to re-render with 1 point (network tab shows a `GET /api/analysis/fitness-trend?from=...&to=...` call).
- Clearing both picker fields returns the chart to 2 points.
- No console errors on debounced re-trigger.

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human before proceeding to Phase 3.

---

## Phase 3: Playwright setup, teardown, and vertical-slice spec

### Overview

Extend the existing `auth.setup.ts` to seed after login, add a `globalTeardown` to Playwright config that clears the seed, and write the single vertical-slice spec that asserts the composed picker→analysis→chart path.

### Changes Required

#### 1. Extend `auth.setup.ts` to seed after login

**File**: `strava-segments-performance/e2e/auth.setup.ts`

**Intent**: After the existing `page.request.get('/auth/test-login?...')` call succeeds and before `page.context().storageState({ path })`, hit `POST /e2e/seed` with the same shared cookie jar. Fail the setup if the seed doesn't return `200`.

**Contract**:
- Location: between the `expect(res.ok()).toBeTruthy()` line (currently [auth.setup.ts:21](strava-segments-performance/e2e/auth.setup.ts:21)) and the `storageState()` save (currently [auth.setup.ts:23](strava-segments-performance/e2e/auth.setup.ts:23)).
- Call: `const seedRes = await page.request.post(`${BACKEND}/e2e/seed`); expect(seedRes.ok()).toBeTruthy();`
- No body payload — seed uses the authenticated user.

#### 2. Add `globalTeardown` to `playwright.config.ts`

**File**: `strava-segments-performance/playwright.config.ts`

**Intent**: Add `globalTeardown: require.resolve('./e2e/global-teardown.ts')` to the top-level config. The teardown authenticates via `/auth/test-login` (fresh session — the browser context is gone), then calls `POST /e2e/reset`, cleaning the fixture regardless of test outcome.

**Contract**:
- New file: `strava-segments-performance/e2e/global-teardown.ts` exporting a default async function.
- Inside: use Playwright's `request.newContext({ baseURL: process.env.E2E_API_BASE_URL ?? 'http://localhost:5000' })`, `GET /auth/test-login?athleteId=12345&name=Test Rider` (produces a cookie in the request context), then `POST /e2e/reset`. Log the outcome; do not throw on non-200 (a failed teardown must not mask test failures).
- Config addition: one line `globalTeardown: './e2e/global-teardown.ts',` at the top level of `defineConfig({...})`.

#### 3. Write the vertical-slice spec

**File**: `strava-segments-performance/e2e/vertical-slice.spec.ts` (new)

**Intent**: One authenticated spec (imports `test`/`expect` from `./fixtures` per the pattern at [seed.spec.ts:1](strava-segments-performance/e2e/seed.spec.ts:1)) that proves the composed slice. Land on `/dashboard` (fixture auto-navigates), await the initial analysis response and assert 2 points + canvas visible, then narrow the picker's `To` field, `waitForResponse` on the analysis endpoint, assert 1 point + canvas still visible. Uses role/label/text locators only.

**Contract**:
- Locators: `page.getByLabel('To')` for the picker input; `page.locator('app-fitness-trend-chart canvas')` for the chart; `page.getByRole('heading', { name: /welcome/i })` as a landing sanity anchor (matching [seed.spec.ts:8](strava-segments-performance/e2e/seed.spec.ts:8)).
- Initial response wait: `const initial = await page.waitForResponse(r => r.url().includes('/api/analysis/fitness-trend') && r.status() === 200);` — declared BEFORE `page.goto('/dashboard')` where possible, or awaited on first navigation. Assert `(await initial.json()).length === 2` and chart canvas visible.
- Narrowing: `await page.getByLabel('To').fill('2026-08-15');` (bracket the earlier seeded activity, exclude the later one). Note: `<input type="date">` `fill` behavior in Playwright uses ISO `YYYY-MM-DD`.
- Filtered response wait: same `waitForResponse` pattern; assert `body.length === 1`; assert `expect(page.locator('app-fitness-trend-chart canvas')).toBeVisible()`.
- Under the default `chromium` project (no config change needed — `dependencies: ['setup']` gives it a seeded, authenticated session).
- No `waitForTimeout` anywhere; no pixel assertions.

### Success Criteria

#### Automated Verification

- `npm run test:e2e` in `strava-segments-performance/` passes locally (all specs, including the new one and the existing OAuth handshake).
- CI job `e2e` in `.github/workflows/e2e-ci.yml` passes on the branch PR.
- The Playwright report shows the new spec's two response waits succeeded.

#### Manual Verification

- Re-run `npm run test:e2e` twice back-to-back against the local `strava_segments_e2e` DB — both runs pass, confirming the teardown cleaned up.
- Inspect the Playwright HTML report (locally: `npx playwright show-report`) for the new spec — the assertions on point count and canvas visibility appear.
- Force a fixture-shape mismatch (e.g., temporarily change Phase 1's seed to insert 3 activities and run the spec unchanged) — the spec fails on the point-count assertion, confirming the assertion is real.

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human before marking the change complete.

---

## Testing Strategy

### Unit Tests

- No new unit tests. The E2E-only endpoints don't warrant standalone tests (Phase 3's spec is their truest test). The frontend `effect` addition is covered by the e2e's picker-narrowing step. Existing unit tests (`FitnessScoringTests`, `FitnessTrendQueryTests`, frontend Vitest suites) must continue to pass unchanged.

### Integration Tests

- No new integration tests. The composed slice is what Phase 3's browser spec proves.

### Manual Testing Steps

1. Run `npm run test:e2e` locally against a fresh Postgres container matching CI's setup — the full suite (setup + oauth-handshake + seed + vertical-slice) passes.
2. Inspect the `strava_segments_e2e` DB after the run: `Activities`, `SegmentEfforts`, `WorkoutFetchStatuses` all empty for the seed user (teardown cleaned up).
3. Re-run the suite immediately — passes again (idempotency confirmed).
4. Manually curl `/e2e/seed` under Development env — returns 404 (env gate works).

## Performance Considerations

- Seed endpoint runs once per e2e run (in `auth.setup.ts`); reset runs once at teardown. Both operate on ≤ 9 rows scoped to one user. No perf budget concerns.
- Debounced picker effect (~300ms) prevents endpoint spam during typing. The e2e's `waitForResponse` synchronizes deterministically regardless of debounce value.

## Migration Notes

- No schema migrations. Seed uses existing entity types and unique-index columns already in place.
- No data migrations. `strava_segments_e2e` is ephemeral in CI (service container per job) and scoped-wiped in local runs.
- Rollback: reverting the three phases removes all E2E-only endpoints and returns `AnalysisService.load()` to its no-arg signature. No production data is touched.

## References

- Related research: [context/changes/testing-vertical-slice-happy-path/research.md](context/changes/testing-vertical-slice-happy-path/research.md)
- Test-plan phase spec: [context/foundation/test-plan.md](context/foundation/test-plan.md) §3 Phase 5, §6.5
- Prior Phase 4 archive (Playwright + `/auth/test-login` origin): [context/archive/2026-09-02-testing-oauth-roundtrip/plan.md](context/archive/2026-09-02-testing-oauth-roundtrip/plan.md)
- Chart + analysis endpoint origin: [context/archive/2026-08-27-fitness-trend-chart/plan.md](context/archive/2026-08-27-fitness-trend-chart/plan.md)
- Timeframe picker origin (date threading): [context/archive/2026-08-27-timeframe-selection/plan.md](context/archive/2026-08-27-timeframe-selection/plan.md)
- Playwright fixture-shape reference: [strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs:19-72](strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs:19)

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend E2E seed + reset endpoints

#### Automated

- [x] 1.1 Backend still builds: `dotnet build strava-segments-performance-backend/strava-segments-performance-backend.csproj` — 99a0cd6
- [x] 1.2 Existing tests still pass: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj` — 99a0cd6
- [x] 1.3 Lint passes: `dotnet format --verify-no-changes strava-segments-performance-backend/strava-segments-performance-backend.csproj` — 99a0cd6

#### Manual

- [x] 1.4 With backend booted under `ASPNETCORE_ENVIRONMENT=E2E` and a valid session cookie (obtained via `/auth/test-login`), `curl -X POST -b cookies.txt http://localhost:5000/e2e/seed` returns `200` and inserts 2+6+1 rows in the `strava_segments_e2e` DB. — 99a0cd6
- [x] 1.5 A second call to `/e2e/seed` returns `200` and the row counts remain 2+6+1 (wipe-and-insert is idempotent). — 99a0cd6
- [x] 1.6 `POST /e2e/reset` returns `200` and leaves `Activities`/`SegmentEfforts`/`WorkoutFetchStatuses` empty for that user. — 99a0cd6
- [x] 1.7 Under Development env, `GET/POST /e2e/seed` returns `404` (endpoint not mapped). — 99a0cd6

### Phase 2: Frontend picker → analysis wiring

#### Automated

- [x] 2.1 Frontend builds: `npm run build` in `strava-segments-performance/` — 7e7b8a2
- [x] 2.2 Type check clean: implicit in the build — 7e7b8a2
- [x] 2.3 Unit tests pass: `npm test` in `strava-segments-performance/` — 7e7b8a2
- [x] 2.4 Lint passes: `npm run lint` if such a script exists; otherwise `prettier --check` per repo convention — 7e7b8a2

#### Manual

- [x] 2.5 With backend under `E2E` env and seed populated, on `/dashboard` the chart mounts with 2 points, then narrowing the `To` field to `2026-08-15` causes the chart to re-render with 1 point (network tab shows a `GET /api/analysis/fitness-trend?from=...&to=...` call). — 7e7b8a2
- [x] 2.6 Clearing both picker fields returns the chart to 2 points. — 7e7b8a2
- [x] 2.7 No console errors on debounced re-trigger. — 7e7b8a2

### Phase 3: Playwright setup, teardown, and vertical-slice spec

#### Automated

- [x] 3.1 `npm run test:e2e` in `strava-segments-performance/` passes locally (all specs, including the new one and the existing OAuth handshake). — e382f73
- [ ] 3.2 CI job `e2e` in `.github/workflows/e2e-ci.yml` passes on the branch PR.
- [x] 3.3 The Playwright report shows the new spec's two response waits succeeded. — e382f73

#### Manual

- [x] 3.4 Re-run `npm run test:e2e` twice back-to-back against the local `strava_segments_e2e` DB — both runs pass, confirming the teardown cleaned up. — e382f73
- [x] 3.5 Inspect the Playwright HTML report (locally: `npx playwright show-report`) for the new spec — the assertions on point count and canvas visibility appear. — e382f73
- [x] 3.6 Force a fixture-shape mismatch (e.g., temporarily change Phase 1's seed to insert 3 activities and run the spec unchanged) — the spec fails on the point-count assertion, confirming the assertion is real. — e382f73
