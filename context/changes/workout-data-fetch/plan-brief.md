# Workout Data Fetching — Plan Brief

> Full plan: `context/changes/workout-data-fetch/plan.md`

## What & Why

Implement S-02 on the roadmap: an authenticated user can trigger fetching of their cycling activities and segment efforts from Strava, watch staged progress while it runs, and have the results cached so repeat triggers never re-fetch already-stored data. This is the data-gathering prerequisite for S-03's fitness scoring, and it directly replaces the empty-state placeholder S-01 already built on the dashboard.

## Starting Point

S-01 shipped OAuth: a `User` entity with encrypted Strava tokens, cookie auth, and a dashboard with an empty-state slot waiting for this feature. No Strava API integration, background job infrastructure, or additional entities exist yet — S-02 is the first feature to actually call the Strava API.

## Desired End State

The dashboard shows a "Fetch my workouts" button. Clicking it starts a backend job; the UI polls and shows staged progress ("Fetching segment details... 340 so far"). On completion, it shows a cached-activity count and a way to check for new rides. Data lives in PostgreSQL and is never re-fetched once cached, even across backend restarts mid-fetch.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Fetch execution | Background `BackgroundService` + channel | HTTP requests can't stay open for the hours a full-history fetch may take under rate limits. | Plan |
| Progress delivery | Polling `GET /api/workouts/fetch-status` | Reuses the exact pattern `AuthService` already established; no new frontend infra. | Plan |
| Rate-limit handling | Wait-and-retry on 429 within the same job | Simplest model; avoids building a separate scheduler. | Plan |
| First-fetch scope | Full history, not a bounded window | User explicitly chose completeness over a faster first run. | Plan |
| Activity filter | Cycling sport types with HR data only | Matches the cyclist-only persona; activities without HR data can't contribute to fitness scoring (which requires HR). Avoids wasting rate-limit budget on unscorable activities. | Plan |
| Data storage | Normalized `Activity`/`SegmentEffort` tables | Matches the existing EF Core + PostgreSQL pattern; typed queries are simpler for S-03. | Plan |
| Token refresh | Implemented now (proactive + 401 fallback) | A multi-hour fetch will routinely outlive the ~6h access token. | Plan |
| Failure recovery | Incremental per-activity commit, resumable | Required so "cached workouts are never re-fetched" holds even after a crash. | Plan |
| Repeat-trigger strategy | Full re-list, diff against cached IDs | User's choice; this diff also doubles as the crash-resume mechanism. | Plan |
| Trigger UX | Manual button only, no auto-trigger | Matches FR-003's "user triggers" framing; avoids surprising users with an hours-long job. | Plan |
| Test coverage | Unit tests on the Strava client only | Focuses effort on the riskiest new logic (pagination, refresh, backoff) within a short timeline. | Plan |
| Progress detail | Staged text + "X of Y" count, no ETA | Total known after listing completes; honest feedback without fragile ETA math. | Plan |

## Scope

**In scope:**
- `Activity`, `SegmentEffort`, `WorkoutFetchStatus` entities + migration
- Strava API client: pagination, cycling + HR filtering, proactive + reactive token refresh, 429 backoff (via injectable `TimeProvider`)
- Single-worker `BackgroundService` fed by an in-process channel, one user's fetch at a time
- `POST /api/workouts/fetch` (single-flight, idempotent) and `GET /api/workouts/fetch-status`
- Startup reset of stale `Running` status to `Interrupted` (crash recovery)
- Dashboard fetch button, staged progress display, completion/error states
- xUnit tests for the Strava client (pagination, refresh, backoff, cycling + HR filtering, mapping)

**Out of scope:**
- SignalR/push progress, Hangfire/Quartz job scheduling
- Bounded/date-limited fetch, timeframe filtering UI (S-04)
- Segment scoring/analysis (S-03)
- Parallel multi-user fetching
- Angular component tests
- Progress bar with ETA
- Syncing activities deleted/edited on Strava after caching

## Architecture / Approach

`Channel<int>` (user IDs) → single-consumer `BackgroundService`. Each run: list all cycling activities with HR data (paginated), insert new `Activity` rows with full summary data from the list response, then fetch + persist segment-effort details for every activity not yet `DetailsFetched`, committing per activity. A `WorkoutFetchStatus` row is the single source of truth the frontend polls and the trigger endpoint's single-flight check reads. Resumability falls out of the re-list-and-diff strategy rather than needing separate checkpoint logic.

Build order is deliberately front-loaded for visibility: the UI is built first (Phase 2, against the endpoint contract), then wired to a working-but-minimal backend (Phase 3, no refresh/backoff yet — the first point real progress is visible end-to-end), and only then hardened for reliability (Phase 4, token refresh + 429 backoff, edited into the same client file with no signature changes). This way each phase after the first is verified by watching the browser, not by inspecting raw HTTP responses.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Data Model & Migrations | `Activity`, `SegmentEffort`, `WorkoutFetchStatus` tables | Low — follows the established `User` entity pattern |
| 2. Frontend Fetch UX | Button, staged progress, completion/error states | Built against endpoints that don't exist yet — no live manual verification until Phase 3 |
| 3. Background Worker & Endpoints (minimal Strava client) | Trigger/status endpoints, single-flight, crash-reset on startup, first real end-to-end progress in the UI | Minimal client has no refresh/backoff — keep test fetches small or expect failures until Phase 4 |
| 4. Strava Reliability Hardening | Token refresh, 429 backoff (same file as Phase 3, no signature changes) | Real 429s are impractical to trigger by hand — mostly validated by Phase 5's automated tests |
| 5. Testing & Integration | Strava client unit tests, full Docker E2E incl. restart-mid-fetch | Real Strava rate limits make full E2E validation slow to iterate on |

**Prerequisites:** S-01 (done) — a connected Strava account with OAuth tokens in PostgreSQL.
**Estimated effort:** ~2-3 sessions across 5 phases.

## Open Risks & Assumptions

- The hardcoded cycling `sport_type` set (`Ride`, `MountainBikeRide`, `GravelRide`, `EBikeRide`, `EMountainBikeRide`, `VirtualRide`, `Handcycle`, `Velomobile`) should be spot-checked against current Strava API docs during implementation — Strava's type taxonomy has evolved before.
- A full-history first fetch for a very active athlete could take multiple hours; this is accepted per the user's explicit choice and the PRD's tolerance for long fetch times, but real end-to-end validation against Strava's live rate limit will be slow to iterate on.
- Strava's rate-limit header format has changed across API versions — the plan deliberately reacts to `429` + `Retry-After` rather than parsing usage headers, to avoid depending on a format that may drift.

## Success Criteria (Summary)

- User can click a button and see their cycling workouts and segment efforts populate in the cache, with honest staged progress feedback throughout
- Re-triggering after a completed fetch is fast and fetches only new activities
- A backend restart mid-fetch never loses already-cached data, and the user can resume by simply clicking the same button again
