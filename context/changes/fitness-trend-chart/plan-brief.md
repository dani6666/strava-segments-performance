# Fitness Trend Chart (S-03) — Plan Brief

> Full plan: `context/changes/fitness-trend-chart/plan.md`
> Research: `context/changes/fitness-trend-chart/research.md`

## What & Why

Score each cached cycling workout on a self-relative 0–100 fitness scale from its segment efforts (elapsed time + average HR), and show the trend as a line chart. This is the roadmap's north-star slice (S-03): it proves the core hypothesis that segment-level, HR-aware scoring surfaces fitness trends neither Strava nor time-only tools reveal.

## Starting Point

S-02 already fetches and caches cycling workouts and their segment efforts (segment id, elapsed time, nullable avg HR, date). No scoring or analysis code exists yet, and the frontend has no chart component or chart dependency. Everything the formula needs is already persisted — no schema change required.

## Desired End State

A logged-in user who has fetched workouts sees, in the dashboard's "completed" state, a line chart of their fitness score (0–100) over time, one point per scored workout, with the window's best workout near 100 and worst near 0. Hovering a point shows its date and score. The scoring algorithm is covered by stage-by-stage unit tests.

## Key Decisions Made

| Decision | Choice | Why | Source |
| --- | --- | --- | --- |
| Per-effort measure | `C = HR × t` (heartbeat cost), unchanged | Parameter-free; leaving HR+time untransformed is self-mitigating (raw HR offsets the missing time-power law) | Research |
| Normalization pipeline | per-segment percentile → segment-median-weighted aggregation → window min–max rescale | Robust to outliers, self-relative, delivers exact 0/100 endpoints | Research |
| Score storage | Recompute per request, persist nothing | Stateless, no migration; scores are window-dependent so persistence buys little | Plan |
| Window API | Optional `from`/`to` params now, default = all history | Forward-compatible with S-04 at ~zero cost | Plan |
| Chart library | ng2-charts + Chart.js | Free tooltips + fixed 0–100 axis, least custom code to own | Plan |
| Presentation | Raw per-workout line only (no smoothing) | Matches PRD "one point per workout"; smoothing is a later iteration | Plan |
| Stop handling | percentile floor + segment-median weight + `t > 2·median` drop | Actually removes egregious stalls; parameter-free to the user | Plan |
| Test depth | Targeted per-stage + edge-case unit tests | Locks the "core risk" algorithm; fast, no brittle golden dataset | Plan |

## Scope

**In scope:** pure scoring algorithm + unit tests; `GET /api/analysis/fitness-trend` endpoint (optional window params); frontend analysis service + line chart in the dashboard completed state.

**Out of scope:** any schema change / persisted scores; Efficiency-Factor / power formula; HR-reserve baseline; smoothed overlay; timeframe-selection UI (S-04); HR-signature stop detector; new DB indexes.

## Architecture / Approach

Pure, dependency-free scorer class over an in-memory list of effort records (unit-tested in isolation, front-loading the core risk) → a thin minimal-API endpoint that loads user-scoped `SegmentEfforts ⋈ Activities` (optionally windowed), projects to the scorer input, and returns a `{date, score}[]` series → an Angular `AnalysisService` (mirroring `WorkoutFetchService`: signals + `withCredentials`) feeding a standalone ng2-charts component rendered in the dashboard `@case ('completed')` branch.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Scoring algorithm (pure + unit-tested) | The full pipeline as a testable class + xUnit suite | Formula correctness — the PRD's core risk; validated in isolation before wiring |
| 2. Analysis API endpoint | `GET /api/analysis/fitness-trend` with optional window | User-scoping/window correctness over the manual join |
| 3. Frontend trend chart | ng2-charts line chart in the dashboard completed state | New dependency (@angular/cdk peer) + first chart in the app |

**Prerequisites:** S-02 done (workouts cached) — met. A logged-in session with repeated-segment workouts for manual verification.
**Estimated effort:** ~3 sessions, one per phase.

## Open Risks & Assumptions

- **Formula plausibility on real data.** Unit tests assert mechanics, not real-world fitness sense; average HR is noisy, so per-workout points will be jumpy. Mitigation is deferred (smoothing / EF metric are documented future iterations).
- **`K_STALL = 2.0` is an unvalidated constant** — may drop too much/little until tuned against real rides.
- **Window-relative scores can confuse** — the same effort scores differently under different windows by design; matters once S-04 lands.

## Success Criteria (Summary)

- After fetching workouts, the user sees a 0–100 fitness line chart over time on the dashboard, best/worst near 100/0, with hover tooltips.
- The endpoint returns a valid window-relative series (and `[]` for a user with no repeated segments).
- The scoring algorithm's stages and edge cases are locked by passing unit tests.
