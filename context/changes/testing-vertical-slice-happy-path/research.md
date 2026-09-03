---
date: 2026-09-03T14:22:30Z
researcher: Daniel Włudarczyk
git_commit: 5de873f411b13e9cffe57b0325c80fb697a95882
branch: feature/e2e-tests
repository: strava-segments-performance
topic: "Phase 5 — Vertical-slice happy-path e2e (Risk #7): authenticated date-range → analysis → chart"
tags: [research, e2e, playwright, risk-7, vertical-slice, phase-5, fitness-trend]
status: complete
last_updated: 2026-09-03
last_updated_by: Daniel Włudarczyk
---

# Research: Phase 5 — Vertical-slice happy-path e2e

**Date**: 2026-09-03T14:22:30Z
**Researcher**: Daniel Włudarczyk
**Git Commit**: 5de873f411b13e9cffe57b0325c80fb697a95882
**Branch**: feature/e2e-tests
**Repository**: strava-segments-performance

## Research Question

Ground Phase 5 (from [test-plan.md](context/foundation/test-plan.md) §3): one Playwright browser e2e that proves an **authenticated date-range → analysis → chart** vertical slice renders end-to-end (Risk #7). The test must reuse the Playwright runner installed by Phase 4, drive a seeded date range, and assert render-succeeded + expected point count — deterministic, no pixels, no state matrix, and **never real Strava**.

Concretely: what infrastructure already exists (Playwright projects, `/auth/test-login`, E2E env, CI); what the vertical slice looks like today (picker → service → API → chart); how to seed a deterministic non-empty trend that clears the scoring gates; where the seams actually are and which of them a single browser e2e can genuinely prove.

## Summary

**The runner and auth seam are done.** [strava-segments-performance/playwright.config.ts:73-89](strava-segments-performance/playwright.config.ts:73) already declares three projects (`setup`, `chromium`, `chromium-noauth`) with `chromium` depending on `setup` for `storageState`. [auth.setup.ts:1-24](strava-segments-performance/e2e/auth.setup.ts:1) hits `/auth/test-login?athleteId=12345&name=Test Rider` and saves the real cookie session; every authenticated spec added to the default `chromium` project inherits it. `.github/workflows/e2e-ci.yml` wires a Postgres 17 service container, warms `dotnet build`, and runs Playwright with `CI=true`. Phase 5 does not touch any of that — it lands a single new spec in the same project.

**One material surprise for the plan.** `AnalysisService.load()` in [analysis.service.ts:20-31](strava-segments-performance/src/app/workouts/analysis.service.ts:20) calls `GET /api/analysis/fitness-trend` **with no query string** — the picker's `from`/`to` only narrow the *workouts-fetch* window, not the analysis window. The analysis endpoint accepts `from`/`to` ([Program.cs:344-351](strava-segments-performance-backend/Program.cs:344)) but the frontend never sends them. So the current "date-range → analysis" thread is really "date-range → fetch → cached rows → analysis of all cached rows". A Phase 5 e2e that pretends the picker filters the trend would be lying; the honest happy path is: seeded rows exist → chart renders. The plan-brief should call this seam gap out (either scope Phase 5 to the honest slice, or add wiring the picker to the analysis endpoint as an in-scope change).

**Two hard gates before the chart mounts.** The dashboard only reaches the chart when both (a) `fetchService.status().status === 'completed'` — an outer `@case` guard ([dashboard.component.html:22-75](strava-segments-performance/src/app/dashboard/dashboard.component.html:22)) — and (b) `analysisService.loadState() === 'loaded'` with `series().length > 0` (guarded inside the chart template at [fitness-trend-chart.component.html:1-5](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.html:1)). Seeding rows is not enough; the `WorkoutFetchStatuses` row for the user must also read `completed`, or the frontend never auto-calls the analysis endpoint. `FitnessTrendQuery` itself ignores fetch-status, so an end-run around the dashboard (calling the API from Playwright directly) would technically pass — but that's not the composed slice Risk #7 is about.

**Seeding: no hooks exist; one HTTP seed endpoint is the cheapest right answer.** Grep across the backend for `HasData|SeedAsync|Seed(|EnsureCreated|IHostedService`-seeder returned nothing — the only `AddHostedService` is `WorkoutFetchWorker`. The E2E DB is a real Postgres (`strava_segments_e2e`, ephemeral in CI, persistent locally), and `Program.cs:141-149` runs `MigrateAsync` on every boot. The strongest fit is a **`POST /e2e/seed` endpoint gated to `IsEnvironment("E2E")`** — same shape and same env gate as the already-shipped `/auth/test-login` and `/e2e-stub/*` handlers, called once from `auth.setup.ts` (or a sibling setup spec), inserting a minimum fixture that clears the scoring gates: **≥ 2 activities × ≥ 3 shared segments × HR-populated efforts**, all elapsed times inside the per-segment `2× median` stall window. That yields ≥ 2 trend points deterministically — enough for a non-empty assertion. A `WorkoutFetchStatuses` row for the same user must be seeded with `status='completed'` in the same call.

**No `data-testid` exists anywhere in the frontend** (grep confirmed). Locators must be role/label/text — which matches the style already set by [seed.spec.ts:8](strava-segments-performance/e2e/seed.spec.ts:8) and [oauth-handshake.spec.ts:20-39](strava-segments-performance/e2e/oauth-handshake.spec.ts:20). The strongest deterministic "loaded, non-empty" anchor is `page.locator('app-fitness-trend-chart canvas')` — the `<canvas>` only appears when both gates pass.

## Detailed Findings

### A. Playwright runner (inherited from Phase 4)

**Config.** Single file [strava-segments-performance/playwright.config.ts:43-90](strava-segments-performance/playwright.config.ts:43).

- `testDir: ./e2e`, `baseURL: 'http://localhost:4200'` ([playwright.config.ts:44](strava-segments-performance/playwright.config.ts:44), [:46](strava-segments-performance/playwright.config.ts:46)).
- `webServer` is an array of two ([playwright.config.ts:53-72](strava-segments-performance/playwright.config.ts:53)): `npm start` for Angular on `:4200`, and `dotnet run` in `../strava-segments-performance-backend` for the backend on `:5000/health`, with env `ASPNETCORE_ENVIRONMENT=E2E` and `ConnectionStrings__DefaultConnection` computed by `resolveE2eDbConnection()` ([playwright.config.ts:32-41](strava-segments-performance/playwright.config.ts:32)). `reuseExistingServer: !process.env.CI` — always fresh in CI.
- Three projects ([playwright.config.ts:73-89](strava-segments-performance/playwright.config.ts:73)):
  - `setup` — `testMatch: /auth\.setup\.ts/`.
  - `chromium` — `Desktop Chrome`, `storageState: 'playwright/.auth/user.json'`, `dependencies: ['setup']`, `testIgnore: /oauth-handshake\.spec\.ts/`. **This is where Phase 5's spec belongs — its default `testMatch` picks any `*.spec.ts` in `e2e/` up automatically.**
  - `chromium-noauth` — fresh context, `testMatch: /oauth-handshake\.spec\.ts/`. Not used by Phase 5.

**storageState.** Written by [auth.setup.ts:9,23](strava-segments-performance/e2e/auth.setup.ts:9) to `playwright/.auth/user.json`; read by the `chromium` project ([playwright.config.ts:78](strava-segments-performance/playwright.config.ts:78)). Gitignored at [strava-segments-performance/.gitignore:34](strava-segments-performance/.gitignore:34) (`/playwright/.auth/`).

**Auth setup spec.** [auth.setup.ts:1-24](strava-segments-performance/e2e/auth.setup.ts:1) — GETs `${E2E_API_BASE_URL ?? http://localhost:5000}/auth/test-login` with `SEED_USER` (`stravaAthleteId=12345`, `displayName='Test Rider'`, [fixtures.ts:9-12](strava-segments-performance/e2e/fixtures.ts:9)), asserts `res.ok()`, then `page.context().storageState({ path })`. Uses `page.request` deliberately so the response's `Set-Cookie` lands in the browser-context cookie jar.

**Fixture wrapper for authenticated specs.** [fixtures.ts:1-25](strava-segments-performance/e2e/fixtures.ts:1) re-exports a `test` that auto-navigates `page.goto('/dashboard')` before every spec. The comment ([:15-17](strava-segments-performance/e2e/fixtures.ts:15)) is explicit: **no mocking of `/api/auth/me`** — specs hit the real backend already authenticated. Phase 5's spec should `import { test, expect } from './fixtures';` (same as [seed.spec.ts:1](strava-segments-performance/e2e/seed.spec.ts:1)).

**Existing specs (style anchors).**
- [seed.spec.ts:1-9](strava-segments-performance/e2e/seed.spec.ts:1) — 10 lines. Landing anchor with `getByRole('heading', { name: /welcome/i })`. Explicitly a style example.
- [oauth-handshake.spec.ts:1-47](strava-segments-performance/e2e/oauth-handshake.spec.ts:1) — imports from `@playwright/test` directly (needs unauthenticated context). Uses role locators + `page.waitForURL('**/dashboard')` — state, not time. **Never** `waitForTimeout`.

### B. Backend E2E env, DB, and existing test-only seams

**Env branching.** `var isE2E = builder.Environment.IsEnvironment("E2E");` at [Program.cs:25](strava-segments-performance-backend/Program.cs:25). Used for:
- Cookie policy: `SameSite=Lax`, `SecurePolicy=SameAsRequest` under E2E ([Program.cs:26,42-47](strava-segments-performance-backend/Program.cs:26)) — needed because the cookie must survive plain-http localhost.
- OAuth handler repoint to `/e2e-stub/oauth/*` ([Program.cs:103-118](strava-segments-performance-backend/Program.cs:103)).
- `/auth/test-login` mapping ([Program.cs:189-217](strava-segments-performance-backend/Program.cs:189)).
- `/e2e-stub/*` endpoints ([Program.cs:219-252](strava-segments-performance-backend/Program.cs:219)).

**DB provider.** Unconditional Postgres — `builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString))` at [Program.cs:16-18](strava-segments-performance-backend/Program.cs:16). No SQLite / in-memory branch anywhere. The **only** thing E2E differs on for the DB is the connection string, injected by Playwright's `resolveE2eDbConnection()` → always database name `strava_segments_e2e`. Distinct from dev's `strava_segments` ([appsettings.Development.json:9](strava-segments-performance-backend/appsettings.Development.json:9)).

**Schema init.** `await db.Database.MigrateAsync()` on every boot ([Program.cs:141-149](strava-segments-performance-backend/Program.cs:141)) — real EF migrations under `strava-segments-performance-backend/Migrations/`.

**`appsettings.E2E.json`.** [strava-segments-performance-backend/appsettings.E2E.json:1-21](strava-segments-performance-backend/appsettings.E2E.json:1) — overrides `Frontend:Origin`, canned `Strava:ClientId/Secret`, a base64 test `TokenEncryption:Key`, and `E2E:StubBaseUrl=http://localhost:5000`. **Does not** ship a connection string.

**`/auth/test-login` endpoint.** [Program.cs:189-217](strava-segments-performance-backend/Program.cs:189). `GET /auth/test-login?athleteId=<long>&name=<string>`, guarded by `if (app.Environment.IsEnvironment("E2E"))`. Handler upserts a `User` by `StravaAthleteId`, `SignInAsync`s with a `ClaimsPrincipal` under `CookieAuthenticationDefaults.AuthenticationScheme`, returns 200 with `{ stravaAthleteId, displayName }`. **Precedent for the seed endpoint Phase 5 needs.**

**No existing seed hooks.** Grep for `HasData|SeedAsync|EnsureCreated|Seed(|IHostedService`-seeder → nothing. The only `AddHostedService` is `WorkoutFetchWorker` ([Program.cs:125](strava-segments-performance-backend/Program.cs:125)).

### C. CI wiring (`.github/workflows/e2e-ci.yml`)

- Triggers on push/PR to `master` when either app or the workflow file changes ([e2e-ci.yml:6-15](.github/workflows/e2e-ci.yml:6)).
- `services.postgres` on `postgres:17`, DB `strava_segments_e2e`, healthcheck via `pg_isready` ([e2e-ci.yml:23-36](.github/workflows/e2e-ci.yml:23)).
- Exports `E2E_DB_CONNECTION` — priority 1 in `resolveE2eDbConnection()` ([e2e-ci.yml:41](.github/workflows/e2e-ci.yml:41), [playwright.config.ts:33](strava-segments-performance/playwright.config.ts:33)).
- `dotnet 10.0.x`, `node 22`, `dotnet build --configuration Debug` to warm the webServer, `npm ci`, `npx playwright install --with-deps chromium`, `npm run test:e2e` with `CI: "true"` ([e2e-ci.yml:51-77](.github/workflows/e2e-ci.yml:51)).
- On failure only: `actions/upload-artifact@v4` uploads `strava-segments-performance/playwright-report/`, 7-day retention ([e2e-ci.yml:79-85](.github/workflows/e2e-ci.yml:79)).

### D. The vertical slice as it exists today

**Route.** Only `/dashboard` renders both the picker and the chart ([app.routes.ts:6](strava-segments-performance/src/app/app.routes.ts:6)), guarded by `authGuard`. `''` and `**` redirect to `/dashboard` ([app.routes.ts:7-8](strava-segments-performance/src/app/app.routes.ts:7)).

**Picker.** Template at [dashboard.component.html:7-19](strava-segments-performance/src/app/dashboard/dashboard.component.html:7) — a `<section class="timeframe-panel">` with two `<input type="date">` fields wrapped in `<label>From ...</label>` / `<label>To ...</label>`, plus a `<p class="range-error">`. On change, the values are written into `WorkoutFetchService.fromDate` / `toDate` signals ([dashboard.component.ts:45-53](strava-segments-performance/src/app/dashboard/dashboard.component.ts:45)).

**Trigger.** Buttons rendered per state at [dashboard.component.html:24, 27, 51, 72](strava-segments-performance/src/app/dashboard/dashboard.component.html:24) all call `(click)="trigger()"` → `WorkoutFetchService.trigger()` at [workout-fetch.service.ts:52-62](strava-segments-performance/src/app/workouts/workout-fetch.service.ts:52). Body assembled at [:64-71](strava-segments-performance/src/app/workouts/workout-fetch.service.ts:64) as `{ after?, before? }` via `startOfLocalDayUtcIso` / `startOfNextLocalDayUtcIso` ([:31-40](strava-segments-performance/src/app/workouts/workout-fetch.service.ts:31)). Status polling loop at [:73-104](strava-segments-performance/src/app/workouts/workout-fetch.service.ts:73), 2 s interval.

**Auto-load analysis on completion.** An Angular `effect` in the `DashboardComponent` constructor at [dashboard.component.ts:22-28](strava-segments-performance/src/app/dashboard/dashboard.component.ts:22): `if (status === 'completed' && previous !== 'completed') this.analysisService.load()`. This is the seam that couples fetch state → analysis.

**Analysis endpoint (backend).** `GET /api/analysis/fitness-trend` at [Program.cs:344-351](strava-segments-performance-backend/Program.cs:344), `.RequireAuthorization()`. Handler resolves the athlete via `ClaimTypes.NameIdentifier` and delegates to [`FitnessTrendQuery.GetForUserAsync`](strava-segments-performance-backend/Services/FitnessTrendQuery.cs:8) with optional `from`/`to`. Query manually joins `SegmentEfforts × Activities` filtered by `Activity.UserId` ([FitnessTrendQuery.cs:11-32](strava-segments-performance-backend/Services/FitnessTrendQuery.cs:11)). Note: `SegmentEfforts` has **no `UserId` column** — Risk #5 scoping is enforced only through the manual join.

**Response DTO.** `public sealed record FitnessTrendPoint(DateTime Date, double Score)` at [FitnessScoring.cs:10](strava-segments-performance-backend/Services/FitnessScoring.cs:10). JSON: array of `{ date, score }`. Empty case returns `[]` ([FitnessScoring.cs:41-44](strava-segments-performance-backend/Services/FitnessScoring.cs:41)). Frontend mirror at [analysis.service.ts:6-9](strava-segments-performance/src/app/workouts/analysis.service.ts:6).

**Frontend consumption.** [`AnalysisService.load()`](strava-segments-performance/src/app/workouts/analysis.service.ts:20) calls the endpoint **with no query params** — `withCredentials: true` only. The `loadState` signal takes `'idle' | 'loading' | 'loaded' | 'error'`. `series()` is set from the response.

**Chart component.** Selector `app-fitness-trend-chart`, standalone, ng2-charts on top of chart.js ([fitness-trend-chart.component.ts:1-45](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.ts:1)). Takes `input.required<FitnessTrendPoint[]>()`; template guards `series().length === 0` → empty-state text `"No fitness data yet"`, else renders `<canvas baseChart [data]="chartData()" [options]="chartOptions" [type]="'line'">` ([fitness-trend-chart.component.html:1-5](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.html:1)).

**Mounting rules (both gates must pass).**
1. Outer `@switch` on `fetchService.status().status` — chart section only inside `@case ('completed')` block ([dashboard.component.html:22-75](strava-segments-performance/src/app/dashboard/dashboard.component.html:22)).
2. Inside that block, an `@switch` on `analysisService.loadState()` — `<app-fitness-trend-chart>` only under `@case ('loaded')` ([dashboard.component.html:54-67](strava-segments-performance/src/app/dashboard/dashboard.component.html:54)).
3. Inside the chart component, `<canvas>` only when `series().length > 0`.

### E. Seeding a deterministic non-empty trend

**Tables the trend endpoint reads.** Traced from the query:
- `Users` — provides `Id` used for scoping. Unique index on `StravaAthleteId` ([AppDbContext.cs:17-19](strava-segments-performance-backend/Data/AppDbContext.cs:17)).
- `Activities` — must have `UserId = user.Id`, `StartDateUtc` inside the picked range, unique `(UserId, StravaActivityId)` ([AppDbContext.cs:21-23](strava-segments-performance-backend/Data/AppDbContext.cs:21)).
- `SegmentEfforts` — must reference `Activity.Id`, carry `StravaSegmentId`, `ElapsedTimeSeconds`, non-null `AverageHeartRate`, unique `StravaSegmentEffortId` ([AppDbContext.cs:25-27](strava-segments-performance-backend/Data/AppDbContext.cs:25)).
- `WorkoutFetchStatuses` — **not read by `FitnessTrendQuery`** but must exist with `status='completed'` for the dashboard's `effect` to trigger auto-load ([dashboard.component.ts:22-28](strava-segments-performance/src/app/dashboard/dashboard.component.ts:22)).

**Scoring gates seeded data must clear.** From [FitnessScoring.cs:16-33, 60-79](strava-segments-performance-backend/Services/FitnessScoring.cs:16):
- Per segment: drop efforts where `ElapsedTimeSeconds > 2 × median(elapsed_on_segment)`; require **≥ 2 survivors**.
- Per workout: **≥ 3 scored efforts** across all segments (`MinScoredEffortsPerWorkout = 3`).
- Every effort must have `AverageHeartRate.HasValue = true`.

**Minimum fixture** (mirrors [FitnessTrendQueryTests.cs:19-72](strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs:19) as an already-proven shape):

```
User:  StravaAthleteId=12345 (SEED_USER), Id=<upsert>
Activities: 2, both UserId=<user.Id>
   A1: StartDateUtc = 2026-08-15
   A2: StartDateUtc = 2026-08-22
SegmentEfforts: 6 (3 shared segments × 2 activities)
   Segment 10:  A1 → 200s@160hr,  A2 → 100s@140hr
   Segment 11:  A1 → 200s@160hr,  A2 → 100s@140hr
   Segment 12:  A1 → 200s@160hr,  A2 → 100s@140hr
WorkoutFetchStatuses: 1 row for the user, status='completed'
```

Per segment: median 150 s, both efforts ≤ 2 × 150 → 2 survivors ✓. Per workout: 3 scored efforts ✓. Two distinct workouts scored → **2 trend points**, order-stable via `OrderBy(w => w.Date)`. Deterministic across runs.

**Recommended seeding mechanism: `POST /e2e/seed` gated to `IsEnvironment("E2E")`.**

| Option | Verdict |
|---|---|
| (a) HTTP `POST /e2e/seed` env-gated | **Recommended.** Same pattern as `/auth/test-login` and `/e2e-stub/*` — one more precedent-consistent handler in `Program.cs`. Idempotent via upsert on unique keys. Zero data ships to Development or Production. One `page.request.post` from a setup spec. |
| (b) `IHostedService` seed on env==E2E | Fires once per boot, not per test. Forces backend restart to reset. Awkward scope-per-singleton dance. |
| (c) Playwright global setup opens `AppDbContext` from Node | Requires a JS Postgres client, duplicates schema knowledge, drifts on migration — a Risk #7-style seam we're trying to avoid. |
| (d) EF `HasData` in a migration | Data ships into every environment. `HasData` isn't env-conditional. Ugliest to keep deterministic. |

**Idempotency.** Local runs re-use the same `strava_segments_e2e` DB, so the seed must be upsert-shaped: `FirstOrDefaultAsync` by unique key → update-or-add → `SaveChangesAsync`. Pin `StravaActivityId` (e.g. `9000001`, `9000002`) and `StravaSegmentEffortId` (e.g. `9100001..9100006`). The user upsert can reuse `SEED_USER.stravaAthleteId = 12345` (already used by `/auth/test-login`) — one athlete for the entire E2E suite.

**Isolation.** In CI the DB is ephemeral per job (service container). Locally the DB name `strava_segments_e2e` is distinct from dev's `strava_segments` — dev data cannot leak into E2E and vice versa. No collision with OAuth stub's `id=99999L` ([Program.cs:246](strava-segments-performance-backend/Program.cs:246)).

### F. Locators for the Phase 5 spec

No `data-testid` anywhere in the frontend (grep confirmed). All locators must be role/label/text.

**Landing:** `page.getByRole('heading', { name: /welcome/i })` — the pattern already used by [seed.spec.ts:8](strava-segments-performance/e2e/seed.spec.ts:8); heading template at [dashboard.component.html:3](strava-segments-performance/src/app/dashboard/dashboard.component.html:3).

**Picker:** `page.getByLabel('From')` / `page.getByLabel('To')` — the `<label>` wraps the input ([dashboard.component.html:8-15](strava-segments-performance/src/app/dashboard/dashboard.component.html:8)).

**Trigger:** `page.getByRole('button', { name: /fetch my workouts/i })` (idle state, [dashboard.component.html:24](strava-segments-performance/src/app/dashboard/dashboard.component.html:24)). Other button texts for other states: `"Resume fetch"`, `"Check for new rides"`, `"Retry"`.

**Chart-loaded anchor (strongest):** `page.locator('app-fitness-trend-chart canvas')` — the `<canvas>` mounts only when all three gates pass. Its presence is the deterministic signal.

**Empty-state anchor (for negative controls, not needed by Phase 5):** `page.getByText('No fitness data yet')` ([fitness-trend-chart.component.html:2](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.html:2)).

**Fetch-completed banner (side signal):** `page.getByText(/cycling activities cached/)` ([dashboard.component.html:50](strava-segments-performance/src/app/dashboard/dashboard.component.html:50)).

### G. Assertion style (borrowed from prior phases)

From [context/archive/2026-09-01-testing-scoring-coverage/plan.md:65-67](context/archive/2026-09-01-testing-scoring-coverage/plan.md:65): **coarse band + ordinal**, never exact-value pinning — that lives in the unit tests. Applied to Phase 5:

- Series is a non-empty array of the expected point count (2 for the minimum fixture).
- Every point has an ISO-parsable `date` and `score ∈ [0, 100]`.
- Points are sorted by date.
- Chart `<canvas>` is `toBeVisible()`.

Do **not** re-assert the scoring formula in the e2e — [`FitnessScoringTests.cs`](strava-segments-performance-backend-tests/FitnessScoringTests.cs) owns that. Do **not** assert pixels or styling — [test-plan.md](context/foundation/test-plan.md) §7 keeps chart styling as negative space.

## Code References

Backend
- [strava-segments-performance-backend/Program.cs:16-18](strava-segments-performance-backend/Program.cs:16) — unconditional Npgsql DbContext registration.
- [strava-segments-performance-backend/Program.cs:25-58](strava-segments-performance-backend/Program.cs:25) — E2E env branching for cookie policy and auth events.
- [strava-segments-performance-backend/Program.cs:141-149](strava-segments-performance-backend/Program.cs:141) — `MigrateAsync` on every boot (no `EnsureCreated`, no `HasData`).
- [strava-segments-performance-backend/Program.cs:189-217](strava-segments-performance-backend/Program.cs:189) — `/auth/test-login` (precedent for the seed endpoint).
- [strava-segments-performance-backend/Program.cs:219-252](strava-segments-performance-backend/Program.cs:219) — `/e2e-stub/*` endpoints.
- [strava-segments-performance-backend/Program.cs:344-351](strava-segments-performance-backend/Program.cs:344) — `GET /api/analysis/fitness-trend` (`from`/`to` supported).
- [strava-segments-performance-backend/Services/FitnessTrendQuery.cs:11-32](strava-segments-performance-backend/Services/FitnessTrendQuery.cs:11) — the manual join used by the trend endpoint.
- [strava-segments-performance-backend/Services/FitnessScoring.cs:10-113](strava-segments-performance-backend/Services/FitnessScoring.cs:10) — scoring gates and DTO.
- [strava-segments-performance-backend/Data/AppDbContext.cs:17-27](strava-segments-performance-backend/Data/AppDbContext.cs:17) — unique indexes seeder must upsert on.
- [strava-segments-performance-backend/appsettings.E2E.json:1-21](strava-segments-performance-backend/appsettings.E2E.json:1) — E2E-only overrides (no DB conn).
- [strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs:19-72](strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs:19) — already-proven fixture shape to mirror.

Frontend
- [strava-segments-performance/src/app/app.routes.ts:1-10](strava-segments-performance/src/app/app.routes.ts:1) — route config; `/dashboard` guarded.
- [strava-segments-performance/src/app/dashboard/dashboard.component.ts:22-28](strava-segments-performance/src/app/dashboard/dashboard.component.ts:22) — `effect` that auto-loads analysis on fetch completion.
- [strava-segments-performance/src/app/dashboard/dashboard.component.html:22-75](strava-segments-performance/src/app/dashboard/dashboard.component.html:22) — outer `@switch` guarding the analysis section.
- [strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.ts:1-45](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.ts:1) — chart component and mapping.
- [strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.html:1-5](strava-segments-performance/src/app/dashboard/fitness-trend-chart.component.html:1) — canvas visibility rule.
- [strava-segments-performance/src/app/workouts/analysis.service.ts:20-31](strava-segments-performance/src/app/workouts/analysis.service.ts:20) — `load()` calls the endpoint with no query params (the seam gap).
- [strava-segments-performance/src/app/workouts/workout-fetch.service.ts:31-104](strava-segments-performance/src/app/workouts/workout-fetch.service.ts:31) — date-range helpers + status polling.

E2E
- [strava-segments-performance/playwright.config.ts:32-90](strava-segments-performance/playwright.config.ts:32) — projects, webServer, DB conn resolver.
- [strava-segments-performance/e2e/auth.setup.ts:1-24](strava-segments-performance/e2e/auth.setup.ts:1) — the setup project.
- [strava-segments-performance/e2e/fixtures.ts:1-25](strava-segments-performance/e2e/fixtures.ts:1) — auto-navigate wrapper + `SEED_USER`.
- [strava-segments-performance/e2e/seed.spec.ts:1-9](strava-segments-performance/e2e/seed.spec.ts:1) — style anchor.
- [strava-segments-performance/e2e/oauth-handshake.spec.ts:1-47](strava-segments-performance/e2e/oauth-handshake.spec.ts:1) — Phase 4 spec, unauthenticated project.

CI
- [.github/workflows/e2e-ci.yml:1-86](.github/workflows/e2e-ci.yml:1) — service Postgres, dotnet warmup, Playwright install, upload-artifact on failure.

## Architecture Insights

- **Two-gate chart mounting** is the single most load-bearing thing to internalize for Phase 5. Seeding rows alone is not enough — a `WorkoutFetchStatuses` row with `status='completed'` for the seed user is required for the dashboard's auto-load `effect` to fire. Skip that and the composed slice is never exercised: the chart never mounts, the analysis endpoint is never called, and the test either times out or silently passes for the wrong reason.
- **The date-range seam is currently vestigial for analysis.** [analysis.service.ts:20-31](strava-segments-performance/src/app/workouts/analysis.service.ts:20) omits `from`/`to`; the backend accepts them. So the "date-range → analysis" language in Risk #7 today reduces to "date-range → fetch → cached rows → analysis of all cached rows". This is an honest description of the shipped product, and Phase 5 should test what's actually there rather than pretending the picker filters the trend.
- **All three E2E-only endpoints share one gate pattern** — `if (app.Environment.IsEnvironment("E2E"))` around a `MapGet`/`MapPost` block in `Program.cs`. Extending that pattern for `POST /e2e/seed` keeps the surface consistent and the review small.
- **`SegmentEfforts` has no `UserId`** — scoping is only via the manual join to `Activities.UserId` ([FitnessTrendQuery.cs:11-13](strava-segments-performance-backend/Services/FitnessTrendQuery.cs:11)). Seeded fixtures must derive `Activity.UserId` from the freshly-upserted user row; hard-coding a UserId that doesn't match the currently-authenticated athlete would seed data that never renders.
- **Never real Strava, never `page.route` for backend flows.** From Phase 4's plan ([context/archive/2026-09-02-testing-oauth-roundtrip/plan.md](context/archive/2026-09-02-testing-oauth-roundtrip/plan.md)): `page.route` can only stub browser-observable HTTP; the backend→Strava exchange happens server-side, so browser interception cannot touch it. Corollary for Phase 5: seed by hitting the backend's own E2E-only surface (`/e2e/seed`), not by intercepting Strava calls from the browser.
- **No `data-testid` culture.** The frontend has zero testids across `src/`. Phase 5 can either (a) stay in-line with the current convention and use role/label/text locators, or (b) begin an in-repo convention by adding `data-testid` to the chart canvas + picker inputs. (a) is smaller and faster; (b) is an architectural bet worth flagging in the plan-brief for the user to decide.

## Historical Context (from prior changes)

- [context/archive/2026-09-02-testing-oauth-roundtrip/plan.md:15](context/archive/2026-09-02-testing-oauth-roundtrip/plan.md:15) — the `/auth/test-login` seam was deliberately built for exactly this case: tests that need a session but do not test login. Phase 5 is its intended second consumer.
- [context/archive/2026-09-02-testing-oauth-roundtrip/plan.md:140-142](context/archive/2026-09-02-testing-oauth-roundtrip/plan.md:140) — the Playwright config shape (projects, webServer, storageState, `dependencies: ['setup']`) was designed to be extensible. Phase 5 adds a single spec to the `chromium` project.
- [context/archive/2026-08-27-fitness-trend-chart/plan.md:74-75, 125-129, 181-186](context/archive/2026-08-27-fitness-trend-chart/plan.md:74) — froze the scoring gates, endpoint shape, and chart component structure Phase 5 must accept as given.
- [context/archive/2026-08-27-timeframe-selection/plan.md:42, 117](context/archive/2026-08-27-timeframe-selection/plan.md:42) — locked the picker's tz-aware `after`/`before` semantics for the *fetch* body. The analysis endpoint's `from`/`to` names diverge (`from`/`to` vs `after`/`before`) — a real naming inconsistency worth flagging if Phase 5 wires the picker through to analysis.
- [context/archive/2026-09-01-testing-scoring-coverage/plan.md:65-67](context/archive/2026-09-01-testing-scoring-coverage/plan.md:65) — coarse-band + ordinal assertion pattern; the model for Phase 5's "non-empty series" check.
- [context/foundation/lessons.md](context/foundation/lessons.md) — no `main`-branch assumption (use `master`); no `-i` on `npm ci` for docker; production Angular routes go through nginx; secrets in `.env` propagated to CI. None directly gate this phase, but the CI-secrets pattern is worth remembering if Phase 5 introduces any new env var.

## Related Research

- [context/archive/2026-09-02-testing-oauth-roundtrip/plan.md](context/archive/2026-09-02-testing-oauth-roundtrip/plan.md) — full context for the Playwright runner Phase 5 inherits.
- [context/archive/2026-08-27-fitness-trend-chart/plan.md](context/archive/2026-08-27-fitness-trend-chart/plan.md) — chart and endpoint design.
- [context/archive/2026-08-27-timeframe-selection/plan.md](context/archive/2026-08-27-timeframe-selection/plan.md) — date-range picker design.
- [context/foundation/test-plan.md](context/foundation/test-plan.md) §2 Risk #7, §3 Phase 5, §6.5 — the frame this phase executes on.

## Open Questions

1. **Wire the picker through to analysis, or scope Phase 5 to the honest current slice?** Currently the picker only narrows fetch. Options for the plan-brief:
   - **(A) Scope down** — Phase 5 asserts "authenticated user lands on `/dashboard` with cached workouts → chart renders a non-empty seeded trend end-to-end". The picker is present in the flow but doesn't filter the trend. Matches shipped behavior. Smallest change.
   - **(B) Wire `AnalysisService.load(from, to)` through to the endpoint's already-accepted `from`/`to` params** as part of this change, so Phase 5 can genuinely assert "picker filters the trend". Larger scope; touches production code that today is untested at the seam. Risk #7 gets stronger coverage.
   The user should decide before `/10x-plan`.

2. **Seed by HTTP endpoint or by seeding rows directly through EF in a Playwright setup helper?** Section E recommends the HTTP endpoint. If the user prefers a pure-Playwright seed (option (c) above) to keep the backend surface minimal, that's a viable alternative — the plan must weigh a JS Postgres client + duplicated schema against a small env-gated endpoint. Recommend deciding at plan time.

3. **Add `data-testid` culture now, or defer?** The frontend has none today. Adding a testid on the chart canvas + picker inputs would produce more resilient locators; not adding keeps the change minimal. Non-blocking for Phase 5 — role/label/text locators are sufficient — but a repo-level decision worth surfacing.

4. **Cleanup discipline between local re-runs.** Local runs re-use the same `strava_segments_e2e` DB. Idempotent upsert handles the same-shape case, but if the fixture changes between commits, stale rows persist. Should the seed endpoint accept a `{ reset: true }` flag (delete-then-insert scoped to `SEED_USER.stravaAthleteId`), or should we document a `dotnet ef database drop` step for local hygiene? Small decision, worth calling explicitly in the plan.
