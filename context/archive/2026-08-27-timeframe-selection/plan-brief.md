# Timeframe Selection — Plan Brief

> Full plan: `context/changes/timeframe-selection/plan.md`

## What & Why

Let the user bound a Strava workout fetch by a custom date window (start / end). Today every fetch pulls all history; a bounded fetch pulls only rides in the window, easing Strava rate-limit pressure and giving the user control over how much history the app processes. Implements PRD FR-002 (nice-to-have, roadmap S-04).

## Starting Point

The fetch pipeline (`POST /api/workouts/fetch` → `WorkoutFetchChannel` → `WorkoutFetchWorker` → `StravaApiClient.ListActivitiesPageAsync`) lists all activities and skips ones already cached. The dashboard shows only a fetch button; the trigger sends an empty body. The analysis/chart slice (S-03) does not exist yet — so this slice bounds the **fetch**, not any analysis.

## Desired End State

Two optional "From"/"To" date inputs sit above the fetch button. Both blank = fetch all history (unchanged default). Setting one or both narrows the fetch to that window; an inverted range (start after end) disables the button with a message. The selection is sent on every trigger (Fetch / Resume / Check-for-new) and bounded fetches accumulate cleanly on top of whatever is already cached.

## Key Decisions Made

| Decision | Choice | Why | Source |
| --- | --- | --- | --- |
| What this slice delivers | Dates on the **fetch** endpoint; S-03 untouched | Nothing to analyze yet; bounding the fetch is self-contained and eases rate limits | Plan |
| Input style | Custom start + end date pickers | User wants arbitrary windows, not fixed presets | Plan |
| Default | All time (both blank) | Matches PRD "default to all data for v1" | Plan |
| Timezone | Browser-local whole-day boundaries → UTC | Matches user's mental model; backend stays timezone-agnostic | Plan |
| Validation | Optional open-ended bounds; block only if start > end | Maps to Strava's independently-optional after/before | Plan |
| Range plumbing | Channel payload record; no persistence | No schema change; resume resends current selection | Plan |
| Persistence | In-memory only (resets on reload) | Simplest, proportionate to a nice-to-have | Plan |
| Testing | Backend + focused frontend units | Covers the contract and risky bits; no e2e harness exists | Plan |

## Scope

**In scope:** optional `after`/`before` on `POST /api/workouts/fetch`; range record through the channel/worker; `after`/`before` epoch params on the Strava listing call; From/To date pickers with start≤end validation; local→UTC whole-day conversion; backend + frontend unit/integration tests.

**Out of scope:** S-03 (scoring, analysis endpoint, chart); any read/query endpoint over cached workouts; range persistence (URL/localStorage/DB); preset ranges; e2e tests; schema/migration changes.

## Architecture / Approach

Frontend computes the window in local time, rounds to whole days, converts to UTC, and attaches `after`/`before` to the fetch POST body. The endpoint validates start≤end and passes a `FetchRequest(userId, afterUtc, beforeUtc)` record through the existing background channel to the worker, which forwards the bounds to `StravaApiClient` as epoch-second `after`/`before` query params on `athlete/activities`. The existing skip-already-cached guard keeps repeated bounded fetches additive and idempotent.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Backend | Fetch endpoint + channel + worker + Strava client carry an optional UTC window | Getting Strava's exclusive after/before boundaries right so whole-day windows are inclusive |
| 2. Frontend | From/To pickers, validation, local→UTC conversion, sent on every trigger | Timezone/whole-day conversion off-by-one at day boundaries |

**Prerequisites:** S-02 (workout-data-fetch) is done — the pipeline this builds on exists. S-03 is not required.
**Estimated effort:** ~1–2 sessions across 2 phases; small, no migration.

## Open Risks & Assumptions

- Assumes Strava `after`/`before` semantics (epoch seconds, UTC, exclusive bounds) — verified against the API; the plan sends start-of-day / start-of-next-day to make windows inclusive.
- Resume after an app restart uses the currently-selected window, not necessarily the original (accepted tradeoff of no-persistence).
- No frontend `*.spec.ts` files exist yet; Phase 2 adds the first ones (Karma/Jasmine already configured).

## Success Criteria (Summary)

- A bounded fetch caches only in-window rides; a later wider fetch adds the rest without re-fetching.
- Both dates blank fetches all history exactly as before.
- An inverted range disables the fetch button with a clear message.
