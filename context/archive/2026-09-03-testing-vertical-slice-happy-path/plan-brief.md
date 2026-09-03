# Vertical-Slice Happy-Path E2E — Plan Brief

> Full plan: [context/changes/testing-vertical-slice-happy-path/plan.md](context/changes/testing-vertical-slice-happy-path/plan.md)
> Research: [context/changes/testing-vertical-slice-happy-path/research.md](context/changes/testing-vertical-slice-happy-path/research.md)

## What & Why

Prove Risk #7 from [test-plan.md](context/foundation/test-plan.md) with one Playwright browser spec: an authenticated user's picker filters the fitness-trend chart end-to-end through the real backend chain — the seams between picker, analysis API, and chart component actually compose. Along the way, close a wiring gap surfaced by research so the assertion is honest: the picker's `from`/`to` today do not reach `/api/analysis/fitness-trend`, so the current shipped product cannot support the risk's stated seam.

## Starting Point

The Playwright runner and cookie-session seam are already inherited from Phase 4: three projects (`setup` + `chromium` + `chromium-noauth`) at [playwright.config.ts:73-89](strava-segments-performance/playwright.config.ts:73), `/auth/test-login` env-gated to `E2E`, a Postgres 17 CI service, and a green `.github/workflows/e2e-ci.yml`. The fitness-trend endpoint at [Program.cs:344-351](strava-segments-performance-backend/Program.cs:344) already accepts nullable `from`/`to` — the frontend just doesn't send them. There are zero seed hooks in the codebase (grep for `HasData|SeedAsync|EnsureCreated|IHostedService`-seeder returned nothing) and zero `data-testid` attributes anywhere in the frontend.

## Desired End State

`npm run test:e2e` runs the existing OAuth-handshake spec **and** a new vertical-slice spec; both pass locally and in CI. The new spec: lands on `/dashboard` → initial analysis renders a 2-point trend → narrows the picker's `To` field → analysis re-runs filtered by picker dates → chart canvas re-renders with a 1-point trend, asserted via `page.waitForResponse` on the analysis endpoint. The seeded rows are wiped at teardown; a re-run starts clean.

## Key Decisions Made

| Decision | Choice | Why | Source |
| --- | --- | --- | --- |
| Picker→analysis seam scope | Wire `from`/`to` through as part of this change | Testing Risk #7 honestly requires the picker to actually filter the trend; the backend already accepts the params | Plan |
| Re-trigger mechanism | Debounced Angular `effect` on picker signals; no new button | Reactive, consistent with existing fetch-completion `effect`; the picker itself becomes the analyze trigger | Plan |
| Seed mechanism | `POST /e2e/seed` env-gated in `Program.cs` | Same precedent as `/auth/test-login` and `/e2e-stub/*`; ships zero data to dev/prod; scope-limited by env gate | Plan |
| Seed hygiene | Scoped wipe-and-insert on every call + `POST /e2e/reset` at teardown | Fixture-shape changes take effect immediately; local re-runs stay deterministic on the persistent `strava_segments_e2e` DB | Plan |
| Setup organization | Extend existing `e2e/auth.setup.ts` — no second project | One HTTP call added between `/auth/test-login` and `storageState.save`; simplest wiring | Plan |
| Locator convention | Role/label/text only — no new `data-testid` | Matches the style set by `seed.spec.ts` and `oauth-handshake.spec.ts`; zero prod-code churn for a test-scoped change | Plan |
| Assertion strictness | Canvas visible + exact point count from API response | `page.waitForResponse` on the analysis endpoint asserts both the composed path AND that the response shape is what the chart consumes | Plan |
| Strava calls under E2E | Not needed — picker re-trigger is analysis-only, no fetch | Avoids adding a Strava data-API stub; fetch button behavior is untouched | Plan |
| Fixture shape | 2 activities × 3 shared segments × HR-populated efforts within 2× median | Clears scoring gates deterministically; mirrors `FitnessTrendQueryTests`; narrowing to one activity → 1 trend point | Research |
| Chart mounting gates | Seed must include a `WorkoutFetchStatuses` row with `status='completed'` | The dashboard's auto-load `effect` fires on transition to `completed` — seeding rows alone isn't enough | Research |

## Scope

**In scope:**
- Two new backend endpoints: `POST /e2e/seed` (scoped wipe + insert) and `POST /e2e/reset` (scoped wipe only), env-gated to `IsEnvironment("E2E")`.
- Frontend: extend `AnalysisService.load(from?, to?)` to forward query params; add a debounced `effect` in `DashboardComponent` that re-triggers analysis on picker changes.
- Playwright: extend `auth.setup.ts` with a seed call; add `globalTeardown` calling reset; write one spec `e2e/vertical-slice.spec.ts`.

**Out of scope:**
- Chart component's empty/sparse/normal series matrix (owned by [test-plan.md](context/foundation/test-plan.md) §3 Phase 6).
- Pixel or styling assertions (§7 negative space).
- Repo-wide `data-testid` convention.
- A Strava data-API stub for `/e2e-stub/*`.
- Changes to `.github/workflows/e2e-ci.yml`, the OAuth handshake spec, the fetch-trigger button, or any Playwright project structure.

## Architecture / Approach

Three self-contained phases in dependency order. Phase 1 (backend) adds the E2E-only surface Phase 3 needs. Phase 2 (frontend) closes the picker→analysis wiring gap. Phase 3 (e2e) wires the Playwright setup/teardown and writes the single spec — the first phase where the composed slice actually runs.

Data flow the spec exercises: `auth.setup.ts` → `POST /auth/test-login` (mints cookie) → `POST /e2e/seed` (fixture inserted) → `storageState.save` → spec runs under `chromium` project, already authenticated + seeded → `page.goto('/dashboard')` → dashboard's fetch-completion `effect` fires → `AnalysisService.load(undefined, undefined)` → 2-point trend → picker `To` narrows → new debounced `effect` fires → `AnalysisService.load(undefined, iso)` → 1-point trend → `globalTeardown` → `POST /e2e/reset`.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Backend E2E seed + reset endpoints | `POST /e2e/seed` and `POST /e2e/reset` in `Program.cs`, env-gated | Getting the fixture shape wrong (must clear per-segment ≥2 and per-workout ≥3 gates) — mitigated by mirroring `FitnessTrendQueryTests` |
| 2. Frontend picker → analysis wiring | `AnalysisService.load(from?, to?)` + debounced re-trigger `effect` in `DashboardComponent` | Debounce timing making the e2e flaky — mitigated by `waitForResponse` synchronization (no `waitForTimeout` allowed) |
| 3. Playwright setup, teardown, and vertical-slice spec | Extended `auth.setup.ts`, new `global-teardown.ts`, new `vertical-slice.spec.ts` | Response-wait race on initial navigation — mitigated by declaring the wait BEFORE `page.goto`; teardown swallows errors so it can't mask test failures |

**Prerequisites:** Phase 4 (OAuth handshake) already landed with the runner, CI workflow, `/auth/test-login`, and `strava_segments_e2e` DB conventions in place. Local Postgres from docker-compose or the CI service container is needed to run the suite.
**Estimated effort:** ~1-2 sessions across 3 phases. Each phase is small (< 100 lines of new code) and independently reviewable.

## Open Risks & Assumptions

- **Assumption**: The dashboard's fetch-completion `effect` fires on the initial `checkStatus() → completed` transition even when the seed pre-inserts `status='completed'`. Verified during research — initial signal is `idle` and flips to `completed` after the first `checkStatus()` returns, so the transition detection still fires. (See [plan.md](context/changes/testing-vertical-slice-happy-path/plan.md) "Critical Implementation Details".)
- **Assumption**: Playwright's `page.request.post` inside `auth.setup.ts` uses the cookie set by the immediately-preceding `/auth/test-login` call. Confirmed by the Phase 4 archive pattern — `page.request` shares the browser-context cookie jar ([auth.setup.ts:18-20](strava-segments-performance/e2e/auth.setup.ts:18)).
- **Risk**: If the debounced picker `effect` fires with different timing than the response-wait expects, the spec could race. Mitigation: register `waitForResponse` before triggering the picker change; assert on the awaited promise's `.json()`.
- **Risk (small)**: The `WorkoutFetchStatus` entity may have required fields not yet enumerated in this brief. The plan calls out "read `Models/WorkoutFetchStatus.cs` before writing" for Phase 1 — a minor implementation-time verification, not a design gap.

## Success Criteria (Summary)

- `npm run test:e2e` passes locally and in CI, including the new vertical-slice spec.
- Re-running the suite twice back-to-back on the persistent local `strava_segments_e2e` DB works — teardown cleans up.
- Forcing a fixture-shape mismatch (temporarily changing seed to 3 activities) makes the spec fail on the point-count assertion — the assertion is real, not vacuous.
