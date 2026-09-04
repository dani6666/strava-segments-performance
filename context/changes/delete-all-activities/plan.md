# Delete All Activities & Reset to Initial State — Implementation Plan

## Overview

Add a user-facing "delete all my data" action. A danger-styled button in the dashboard's completed panel, guarded by a native `confirm()`, calls a new authenticated `DELETE /api/workouts` endpoint that wipes the current user's activities, segment efforts, and fetch-status row. On success the frontend resets its signals so the dashboard collapses back to its empty initial state (only the "Fetch my workouts" button).

## Current State Analysis

- **No production delete exists.** The only per-user delete lives in the E2E-only `/e2e/reset` handler ([Program.cs:347](strava-segments-performance-backend/Program.cs:347)), gated behind `app.Environment.IsEnvironment("E2E")`. It is the exact reference for the delete logic.
- **`SegmentEffort` has no FK/navigation to `Activity`** ([Models/SegmentEffort.cs](strava-segments-performance-backend/Models/SegmentEffort.cs), AppDbContext.cs), so deleting activities does **not** cascade to efforts. Efforts must be deleted first via a subquery join on `ActivityId → Activity.UserId`.
- **User scoping pattern** is uniform across authenticated handlers: parse `ClaimTypes.NameIdentifier` → `db.Users.FirstAsync(u => u.StravaAthleteId == stravaId)` → scope rows by `user.Id` (e.g. Program.cs:439-440, 481-482).
- **Frontend "initial state"** is driven entirely by `WorkoutFetchService.status().status` via the `@switch` at [dashboard.component.html:22](strava-segments-performance/src/app/dashboard/dashboard.component.html). `status === 'idle'` renders only the "Fetch my workouts" button. `IDLE_STATUS` is the default constant ([workout-fetch.service.ts:17](strava-segments-performance/src/app/workouts/workout-fetch.service.ts)).
- **The fitness chart** is gated inside the `completed` branch on `analysisService.loadState()`. The delete button lives in that same `completed` branch, so it is never visible during `pending`/`running` — the in-flight-fetch race is avoided by construction (no backend guard needed).
- **State is held in `providedIn:'root'` signal services** the template reads directly: `WorkoutFetchService.status`, `AnalysisService.series` / `loadState`. No component-local copies to reset.
- **No delete/reset/confirm UI patterns exist** anywhere in the app. Danger color already in use for error text is `#c0392b` ([dashboard.component.scss](strava-segments-performance/src/app/dashboard/dashboard.component.scss)).

## Desired End State

A logged-in user who has cached workouts sees a danger-styled "Delete all my data" button in the completed panel. Clicking it shows a native browser confirm; on OK, the backend deletes all their activities, efforts, and fetch-status row in one transaction, and the dashboard returns to showing only the "Fetch my workouts" button (idle state) with no fitness chart. Verify by: fetch workouts → chart appears → delete → page shows only the fetch button; a subsequent page reload still shows the idle state (data really gone).

### Key Discoveries:

- Reuse the transactional 3-step `ExecuteDeleteAsync` from `/e2e/reset` ([Program.cs:352-360](strava-segments-performance-backend/Program.cs:352)) verbatim in the new handler.
- Deleting the `WorkoutFetchStatus` row is what makes the reset clean: with no row, `GET /api/workouts/fetch-status` returns the idle default, so the page is idle even after a reload.
- Frontend reset must touch **three signals**: `WorkoutFetchService.status` → `IDLE_STATUS`, `AnalysisService.series` → `null`, `AnalysisService.loadState` → `'idle'`.

## What We're NOT Doing

- No confirmation modal/dialog component — using native `confirm()`.
- No soft-delete, undo, or audit trail — the delete is permanent and immediate on confirm.
- No backend 409/guard for in-flight fetches — the button is UI-gated to the `completed` branch only.
- No automated tests for this change (explicit user decision).
- No deletion of the `User` row or auth session — the user stays logged in.
- No new `/api/activities` namespace — staying within existing `/api/workouts`.

## Implementation Approach

Two thin vertical layers. Backend adds one authenticated minimal-API route mirroring an existing handler. Frontend adds one service method plus a button and a small orchestration method on the dashboard component that calls the endpoint and resets the shared signal state. No schema or model changes.

## Phase 1: Backend delete endpoint

### Overview

Add an authenticated `DELETE /api/workouts` route that deletes the current user's efforts, activities, and fetch-status row in a single transaction.

### Changes Required:

#### 1. New delete endpoint

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Register a production `DELETE /api/workouts` handler, placed alongside the other `/api/workouts/*` routes (near Program.cs:431-486), that wipes all cached data for the authenticated user. Not environment-gated.

**Contract**: `app.MapDelete("/api/workouts", async (HttpContext ctx, AppDbContext db) => {...}).RequireAuthorization();`
- Resolve the user via the standard pattern: `long.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value)` → `db.Users.FirstAsync(u => u.StravaAthleteId == stravaId)`.
- Inside a `BeginTransactionAsync`, run the three `ExecuteDeleteAsync` calls in order (efforts via `db.Activities.Any(a => a.Id == e.ActivityId && a.UserId == user.Id)`, then activities by `UserId`, then `WorkoutFetchStatuses` by `UserId`) — identical to Program.cs:352-360 — then `CommitAsync`.
- Return `Results.NoContent()` (204).

### Success Criteria:

#### Automated Verification:

- Backend builds: `dotnet build` (in `strava-segments-performance-backend/`)

#### Manual Verification:

- With cached data present, `DELETE /api/workouts` (authenticated) returns 204 and removes all the user's activity, effort, and fetch-status rows from Postgres.
- A second `DELETE` on already-empty data still returns 204 (idempotent, no error).
- Another user's rows are untouched by the delete.

**Implementation Note**: After this phase and the build passes, pause for manual confirmation before proceeding to Phase 2.

---

## Phase 2: Frontend delete action + state reset

### Overview

Add a `deleteAll()` method to `WorkoutFetchService`, a reset orchestration on the dashboard component, and a danger-styled confirm-guarded button in the completed panel.

### Changes Required:

#### 1. Delete method on the fetch service

**File**: `strava-segments-performance/src/app/workouts/workout-fetch.service.ts`

**Intent**: Add a `deleteAll()` method that calls the new endpoint and, on success, resets the fetch status to idle. Also cancel any live polling subscription defensively.

**Contract**: `deleteAll()` → `this.http.delete(`${environment.apiBaseUrl}/api/workouts`, { withCredentials: true })`; on success `this.pollingSub?.unsubscribe()` then `this.status.set(IDLE_STATUS)`. Return the subscription/observable so the caller can chain the analysis reset. Follow the existing `trigger()`/`checkStatus()` subscribe-with-error-handler shape.

#### 2. Analysis reset

**File**: `strava-segments-performance/src/app/workouts/analysis.service.ts`

**Intent**: Add a `reset()` method that clears the fitness-trend state so the chart disappears.

**Contract**: `reset()` → `this.series.set(null); this.loadState.set('idle');`

#### 3. Dashboard orchestration + button wiring

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.ts`

**Intent**: Add a `deleteAll()` handler that confirms with the user, then calls the service delete and clears analysis state on success. Must avoid the completion `effect` immediately re-triggering an analysis load (the effect fires on `completed → …`; resetting to `idle` first means the guard `status === 'completed'` won't re-run).

**Contract**: `deleteAll()` → `if (!confirm('Delete all your cached activities and efforts? This cannot be undone.')) return;` then call `fetchService.deleteAll()`, and on success `analysisService.reset()`. Sequencing: reset status to idle (in the service) and call `analysisService.reset()` so both signals settle to the empty state together.

#### 4. Delete button in the completed panel

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.html`

**Intent**: Add the button inside the `@case ('completed')` block's `.fetch-complete` div (near dashboard.component.html:49-52), so it only shows when there is data to delete.

**Contract**: `<button class="delete-all" (click)="deleteAll()">Delete all my data</button>` — a new element in the completed branch. No `[disabled]` binding needed.

#### 5. Danger styling

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.scss`

**Intent**: Style `.delete-all` as a destructive action using the existing danger color.

**Contract**: `.delete-all { background: #c0392b; ... }` with a darker hover, mirroring the structure of the existing `.fetch-panel button` rule.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm run build` (in `strava-segments-performance/`)

#### Manual Verification:

- After fetching workouts (chart visible), the "Delete all my data" button appears in the completed panel.
- Clicking it shows the native confirm; Cancel does nothing.
- Confirming deletes the data, the fitness chart disappears, and the page shows only the "Fetch my workouts" button.
- Reloading the page keeps the idle state (no residual data).
- The button is not visible during `idle`, `pending`, or `running` states.

**Implementation Note**: After this phase and the build passes, pause for manual confirmation.

---

## Testing Strategy

Per the change decision, no automated tests are added. Verification is manual (see each phase's Manual Verification) plus the framework build checks.

## Performance Considerations

`ExecuteDeleteAsync` issues bulk SQL DELETEs with no change-tracking — appropriate and efficient for a full per-user wipe.

## Migration Notes

None — no schema or model changes.

## References

- Delete pattern reference: [Program.cs:347-363](strava-segments-performance-backend/Program.cs:347) (`/e2e/reset`)
- Fetch endpoints for placement: [Program.cs:431-486](strava-segments-performance-backend/Program.cs:431)
- Frontend state/services: [workout-fetch.service.ts](strava-segments-performance/src/app/workouts/workout-fetch.service.ts), [analysis.service.ts](strava-segments-performance/src/app/workouts/analysis.service.ts)
- Dashboard template `@switch`: [dashboard.component.html:22](strava-segments-performance/src/app/dashboard/dashboard.component.html)

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles.

### Phase 1: Backend delete endpoint

#### Automated

- [x] 1.1 Backend builds: `dotnet build` — 9586882

#### Manual

- [x] 1.2 Authenticated DELETE returns 204 and removes the user's activities, efforts, and fetch-status rows — 9586882
- [x] 1.3 Second DELETE on empty data still returns 204 (idempotent) — 9586882
- [x] 1.4 Another user's rows are untouched — 9586882

### Phase 2: Frontend delete action + state reset

#### Automated

- [x] 2.1 Frontend builds: `npm run build`

#### Manual

- [x] 2.2 Delete button appears in the completed panel after a fetch
- [x] 2.3 Native confirm shows; Cancel is a no-op
- [x] 2.4 Confirming clears data, hides the chart, and returns to the fetch-only idle view
- [x] 2.5 Reload preserves the idle state (data really gone)
- [x] 2.6 Button is hidden during idle/pending/running
