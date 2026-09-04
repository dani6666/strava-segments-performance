# Delete All Activities & Reset to Initial State — Plan Brief

> Full plan: `context/changes/delete-all-activities/plan.md`

## What & Why

Give users a way to wipe all their cached Strava data. A "Delete all my data" button removes the current user's activities, segment efforts, and fetch-status row, returning the dashboard to its empty initial state (only the "Fetch my workouts" button). Useful for starting a clean re-analysis or clearing stale data.

## Starting Point

The dashboard shows cached workouts as a count plus a fitness-trend chart once a fetch completes. There is no production delete anywhere — only an E2E-only `/e2e/reset` handler that does exactly this delete for tests. The page's whole view is driven by `WorkoutFetchService.status().status`; `idle` renders just the fetch button.

## Desired End State

A user with cached data sees a danger-styled delete button in the completed panel. Confirming a native browser prompt wipes their data server-side in one transaction and collapses the dashboard back to the fetch-only idle view — and it stays idle across reloads because the data is really gone.

## Key Decisions Made

| Decision | Choice | Why | Source |
| --- | --- | --- | --- |
| Confirmation UX | Native `confirm()` | Zero net-new UI, ships fast, no dialog component exists | Plan |
| In-flight fetch race | Hide button during active fetch | Button lives only in the `completed` branch, so the race is avoided by construction — no backend guard | Plan |
| Delete scope | Activities + efforts + fetch-status row | Deleting the status row makes the page naturally return to idle (mirrors `/e2e/reset`) | Plan |
| Endpoint | `DELETE /api/workouts` | RESTful, fits existing `/api/workouts/*` namespace | Plan |
| Button placement | Completed panel, danger color `#c0392b` | Only visible when there's data to delete | Plan |
| State reset | Reset signals client-side | Instant, no extra round-trip; `@switch` collapses to fetch button | Plan |
| Tests | None | Explicit user decision for this change | Plan |

## Scope

**In scope:** new authenticated `DELETE /api/workouts`; `deleteAll()` on the fetch service; `reset()` on the analysis service; danger button + confirm in the dashboard; signal reset to idle.

**Out of scope:** confirm modal component; soft-delete/undo/audit; backend concurrency guard; user/session deletion; automated tests.

## Architecture / Approach

Two thin layers. **Backend:** one minimal-API route reusing the existing `/e2e/reset` transactional 3-step `ExecuteDeleteAsync` (efforts via activity-join → activities → fetch-status), scoped by resolved `user.Id`, returns 204. **Frontend:** button → native confirm → `WorkoutFetchService.deleteAll()` (DELETE call, then `status = IDLE_STATUS`) + `AnalysisService.reset()` (`series = null`, `loadState = 'idle'`). The three signals settling to empty makes the template's `@switch` render only the fetch button.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Backend delete endpoint | `DELETE /api/workouts` wiping the user's data in a transaction | Effort/activity delete ordering (no cascade — must delete efforts via join first) |
| 2. Frontend action + reset | Confirm-guarded button that deletes and resets to idle | Completion `effect` re-triggering an analysis load if signals reset in the wrong order |

**Prerequisites:** none — no schema changes, reuses an existing delete pattern.
**Estimated effort:** ~1 session across 2 phases.

## Open Risks & Assumptions

- Assumes the completion `effect` in the dashboard won't re-fire an analysis load once status is set back to `idle` (its guard requires `completed`) — sequence the reset accordingly.
- Native `confirm()` is acceptable UX for now; can be upgraded to a styled dialog later.

## Success Criteria (Summary)

- Confirming the delete removes all the user's cached data and returns the dashboard to the fetch-only idle view.
- The idle state persists across a page reload (data is truly deleted, per-user scoped).
- The button never appears during idle/pending/running.
