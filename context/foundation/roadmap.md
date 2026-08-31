---
project: "Strava Segments Performance"
version: 1
status: draft
created: 2026-06-10
updated: 2026-08-31
prd_version: 1
main_goal: speed
top_blocker: external
---

# Roadmap: Strava Segments Performance

> Derived from `context/foundation/prd.md` (v1) + auto-researched codebase baseline.
> Edit-in-place; archive when superseded.
> Slices below are listed in dependency order. The "At a glance" table is the index.

## Vision recap

Cyclists who repeat the same Strava segments cannot tell whether their fitness is improving or declining. Strava shows raw workout-level data, and third-party tools compare absolute times, but neither accounts for the relationship between time and heart rate on specific segments. By comparing elapsed time and average heart rate on the same segment over time, the app detects fitness gains invisible to time-only comparisons — same time at lower heart rate means improved fitness.

## North star

**S-03: User can see a fitness trend chart (0-100 score) computed from real Strava segment data** — this is the smallest end-to-end flow that proves the core product hypothesis: segment-level, HR-aware scoring surfaces fitness trends that neither Strava nor time-only tools reveal. It is placed as early as its prerequisites (auth + data fetching) allow because everything else only matters if this works.

## At a glance

| ID   | Change ID            | Outcome (user can ...)                                        | Prerequisites | PRD refs            | Status   |
| ---- | -------------------- | ------------------------------------------------------------- | ------------- | ------------------- | -------- |
| S-01 | strava-oauth-login   | authenticate via Strava OAuth and land on an authenticated UI | —             | FR-001              | done     |
| S-02 | workout-data-fetch   | trigger workout fetching from Strava with progress indication | S-01          | FR-003              | done     |
| S-03 | fitness-trend-chart  | see a fitness trend chart (0-100 score over time)             | S-02          | FR-003, FR-004, US-01 | done     |
| S-04 | timeframe-selection  | filter analysis by a selected timeframe                       | S-02          | FR-002              | done |

## Baseline

What's already in place in the codebase as of 2026-06-10 (auto-researched + user-confirmed).
Foundations below assume these are present and do NOT re-scaffold them.

- **Frontend:** partial — Angular 21 scaffold with routing framework, build tooling, and environment config (`apiBaseUrl` set); no custom components or Strava-related code (`strava-segments-performance/src/`)
- **Backend / API:** partial — .NET 10 minimal API shell with `/health` endpoint and OpenAPI support; no domain logic or Strava integration (`strava-segments-performance-backend/Program.cs`)
- **Data:** absent — no ORM, no migrations, no caching or storage mechanism in either project
- **Auth:** absent — no OAuth configuration, no session/token handling, no auth middleware
- **Deploy / infra:** partial — Dockerfiles for both projects, `docker-compose.yml`, GitHub Actions CI workflows (`.github/workflows/backend-ci.yml`, `frontend-ci.yml`); no IaC
- **Observability:** partial — default ASP.NET Core logging + `/health` endpoint; no structured logging, error tracking, or metrics

## Foundations

None. All technical elements are introduced in the vertical slices that first need them: auth scaffold in S-01, data persistence in S-02, chart rendering in S-03. The existing partial baseline (Angular scaffold, .NET shell, Docker, CI) provides sufficient infrastructure without additional cross-cutting prerequisites.

## Slices

### S-01: Strava OAuth authentication

- **Outcome:** user can authenticate via Strava OAuth and land on an authenticated UI
- **Change ID:** strava-oauth-login
- **PRD refs:** FR-001
- **Prerequisites:** —
- **Parallel with:** —
- **Blockers:** —
- **Unknowns:**
  - Is the Strava API application already registered (client ID + secret available)? — Owner: user. Block: no.
- **Risk:** Strava OAuth is the external dependency gate; if API access is denied or delayed, every downstream slice is blocked. Sequenced first to surface this risk as early as possible.
- **Status:** done

### S-02: Workout data fetching with progress and caching

- **Outcome:** user can trigger workout fetching from Strava, see progress as data loads, and have fetched workouts cached for reuse
- **Change ID:** workout-data-fetch
- **PRD refs:** FR-003
- **Prerequisites:** S-01
- **Parallel with:** —
- **Blockers:** —
- **Unknowns:**
  - Strava API rate limits for bulk historical activity + segment effort fetching — what's the practical ceiling per 15-minute window? — Owner: user. Block: no.
- **Risk:** Rate limits may throttle large historical fetches significantly; the progress UX must handle minutes-long waits gracefully. This is where the external dependency (#1 blocker) is most felt operationally.
- **Status:** done

### S-03: Fitness scoring and trend chart

- **Outcome:** user can see a fitness trend chart showing a 0-100 score per workout, computed from segment elapsed time + average heart rate against personal history
- **Change ID:** fitness-trend-chart
- **PRD refs:** FR-003, FR-004, US-01
- **Prerequisites:** S-02
- **Parallel with:** S-04
- **Blockers:** —
- **Unknowns:** —
- **Risk:** The scoring formula is the core intellectual risk (PRD FR-004: "formula validation is the core risk to iterate on"). If the formula produces nonsensical scores, the chart is useless — but this can only be validated with real data, so it's correctly sequenced after data fetching.
- **Status:** done

### S-04: Timeframe selection

- **Outcome:** user can select a timeframe to narrow the analysis window instead of analyzing all workouts
- **Change ID:** timeframe-selection
- **PRD refs:** FR-002
- **Prerequisites:** S-02
- **Parallel with:** S-03
- **Blockers:** —
- **Unknowns:** —
- **Risk:** Nice-to-have (PRD priority). With speed as the sequencing goal, this is the first candidate to cut if the deadline pressures. The chart already serves as a visual timeframe — users can eyeball trends without explicit filtering. Building alongside S-03 requires agreeing on a date-range filter contract (e.g. query params on the cached-workout data) up front so the two integrate cleanly once S-03's analysis endpoint lands.
- **Status:** done

## Backlog Handoff

| Roadmap ID | Change ID           | Suggested issue title                              | Ready for `/10x-plan` | Notes                          |
| ---------- | ------------------- | -------------------------------------------------- | --------------------- | ------------------------------ |
| S-01       | strava-oauth-login  | Implement Strava OAuth login flow                  | done                  | Completed 2026-06-30               |
| S-02       | workout-data-fetch  | Fetch and cache workout data from Strava           | no                    | Blocked by S-01                |
| S-03       | fitness-trend-chart | Score workouts and display fitness trend chart      | no                    | Blocked by S-02                |
| S-04       | timeframe-selection | Add timeframe filter for analysis                  | no                    | Nice-to-have; blocked by S-02, can run parallel with S-03 |

## Open Roadmap Questions

None carried from PRD. The primary external risk (Strava API access and rate limits) is tracked per-slice in S-01 and S-02 unknowns.

## Parked

- **Weather/surface conditions in scoring** — Why parked: PRD Non-Goals. Segment comparison already controls for elevation; weather is too complex for v1.
- **Other fitness platforms (Garmin, Wahoo, Polar)** — Why parked: PRD Non-Goals. Strava is the sole data source.
- **Social features (sharing, leaderboards, user comparison)** — Why parked: PRD Non-Goals. Flat user model, each user sees only their own data.
- **User-configurable scoring parameters** — Why parked: PRD Non-Goals. Scoring formula ships as-is; no user knobs.

## Done

- **S-02: user can trigger workout fetching from Strava, see progress as data loads, and have fetched workouts cached for reuse** — Archived 2026-08-26 → `context/archive/2026-07-10-workout-data-fetch/`. Lesson: —.
- **S-03: user can see a fitness trend chart showing a 0-100 score per workout, computed from segment elapsed time + average heart rate against personal history** — Archived 2026-08-31 → `context/archive/2026-08-27-fitness-trend-chart/`. Lesson: —.
- **S-04: user can select a timeframe to narrow the analysis window instead of analyzing all workouts** — Archived 2026-08-31 → `context/archive/2026-08-27-timeframe-selection/`. Lesson: —.

