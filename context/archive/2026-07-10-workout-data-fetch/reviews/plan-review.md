<!-- PLAN-REVIEW-REPORT -->
# Plan Review: Workout Data Fetching (S-02)

- **Plan**: context/changes/workout-data-fetch/plan.md
- **Mode**: Deep
- **Date**: 2026-07-10
- **Verdict**: SOUND (after fixes)
- **Findings**: [0 critical] [2 warnings] [2 observations]

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| End-State Alignment | PASS |
| Lean Execution | PASS |
| Architectural Fitness | PASS |
| Blind Spots | WARNING |
| Plan Completeness | WARNING |

## Grounding

6/6 paths confirmed, 4/4 symbols confirmed, brief-plan consistency confirmed.

## Findings

### F1 — Trigger endpoint's User lookup step is implicit

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Plan Completeness
- **Location**: Phase 3, Section 6 — Trigger and status endpoints
- **Detail**: The plan says both endpoints follow "the existing auth-endpoint pattern" which only reads claims from the cookie — it never queries the Users table. But the trigger endpoint needs the DB User.Id (int PK) to upsert WorkoutFetchStatus and enqueue into the channel. The plan omitted the Users.FirstAsync(u => u.StravaAthleteId == stravaId) lookup step.
- **Fix**: Added the User lookup step to Phase 3.6's contract.
- **Decision**: FIXED

### F2 — No consideration of concurrent fetch triggers from multiple browser tabs

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Blind Spots
- **Location**: Phase 3, Section 6 — Trigger endpoint
- **Detail**: The trigger endpoint's single-flight check-then-write was not atomic — two near-simultaneous requests could both read Status != Running, both upsert, and both enqueue.
- **Fix**: Replaced with an atomic conditional update (ExecuteUpdateAsync with WHERE Status != Running filter).
- **Decision**: FIXED

### F3 — ActivitiesProcessed counter meaning shifts between stages

- **Severity**: 💡 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Blind Spots
- **Location**: Phase 3, Section 4 — Background worker
- **Detail**: During ListingActivities, ActivitiesProcessed counts newly discovered activities. During FetchingDetails, it counts details fetched. Without a reset, the detail-fetch progress would start at the listing count.
- **Fix**: Added explicit counter reset to 0 at the ListingActivities → FetchingDetails transition.
- **Decision**: FIXED

### F4 — Phase 3 auth-check success criteria classified as automated but require manual curl

- **Severity**: 💡 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Plan Completeness
- **Location**: Phase 3, Success Criteria
- **Detail**: Items 3.2/3.3 ("returns 401 when unauthenticated") were listed as automated verification but no test infrastructure exists until Phase 5.
- **Fix**: Reclassified to manual verification with explicit curl commands. Progress section updated to match.
- **Decision**: FIXED
