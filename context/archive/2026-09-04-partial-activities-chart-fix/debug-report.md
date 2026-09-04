---
title: Partial activity fetch on the fitness chart
type: debug-report
status: fixed, verified in prod
date: 2026-09-04
pr: https://github.com/dani6666/strava-segments-performance/pull/16
files_changed:
  - strava-segments-performance-backend/Services/WorkoutFetchWorker.cs
  - strava-segments-performance-backend-tests/WorkoutFetchWorkerTests.cs (new)
---

## Ticket

> I have a problem on the prod of my app. I choose a range to get activities, i
> see that it is trying to fetch activities but only a part of the show up on
> the chart.

**Brief:**
- **Repro steps:** pick a date range on the dashboard to fetch activities.
- **Expected vs. actual:** all activities in the range should end up on the
  fitness chart; only some did.
- **Scope:** partial/silent — the fetch "succeeds" (no error shown), the
  result is incomplete.
- **Frequency:** every time, for any range containing more than 10 new
  activities.
- **Consistency:** re-selecting the same range (or refreshing) surfaced more
  activities each time, eventually converging on the full set.

## Investigation

Two clarifying facts from the user shaped where to look: the bug was
**deterministic** (not data/timing-dependent) and **recovered on retry**
(more data appeared each time, not random). That combination pointed away
from a flaky rate-limit/network issue and toward a fixed per-run limit
somewhere in the fetch pipeline, paired with a status that doesn't reflect
"more work left."

Traced the fetch path end to end:

1. `POST /api/workouts/fetch` (`Program.cs`) enqueues a `FetchRequest` onto
   an in-memory channel and returns `202 Accepted` immediately — fetching
   happens asynchronously in a hosted background service.
2. `WorkoutFetchWorker.ProcessUserAsync` (`Services/WorkoutFetchWorker.cs`)
   runs in two stages:
   - **Listing** — pages through `GET /athlete/activities` and persists a
     summary row per activity (`DetailsFetched = false`). This stage is
     unbounded and correct.
   - **FetchingDetails** — for every activity with `DetailsFetched == false`,
     calls `GET /activities/{id}` and stores its `SegmentEfforts`, which is
     what makes the activity eligible for the chart.
3. The frontend polls `GET /api/workouts/fetch-status` until it sees
   `status: 'completed'`, then loads `/api/analysis/fitness-trend`.
4. `FitnessTrendQuery` builds the chart series by inner-joining
   `SegmentEfforts` to `Activities` — an activity with no segment efforts
   simply doesn't appear, with no error anywhere in the chain.

Also checked the Strava rate-limit retry/deadline logic in
`StravaApiClient.SendAsync` (relevant since a recent commit, `f15bec3`, had
reverted an inverted deadline comparison). Ruled it out: a rate-limit
exhaustion there throws `TimeoutException`, which propagates up to
`WorkoutFetchWorker.ExecuteAsync`'s catch block and marks the job `Failed`
with a visible error — not a silent partial success. It also wouldn't
explain the deterministic "every time, over 10 activities" pattern.

## Root cause

`WorkoutFetchWorker.cs` (detail-fetching loop, before the fix):

```csharp
foreach (var activity in pending)
{
    var detail = await stravaClient.GetActivityDetailAsync(user, activity.StravaActivityId, ct);
    db.SegmentEfforts.AddRange(detail.ToSegmentEfforts(activity.Id));
    activity.DetailsFetched = true;

    status.ActivitiesProcessed++;
    await db.SaveChangesAsync(ct);

    if(status.ActivitiesProcessed == 10)
        break;
}

status.Status = FetchStatusState.Completed;
status.CompletedAtUtc = DateTime.UtcNow;
await db.SaveChangesAsync(ct);
```

A hardcoded cap stopped the detail-fetch loop after exactly 10 activities,
but the status was unconditionally marked `Completed` immediately after —
with no check for whether `pending` had more than 10 items. Activities past
the tenth kept `DetailsFetched = false` and no `SegmentEfforts`, so they
were invisible to the chart query, with the job reporting success.

Because already-`DetailsFetched` activities are correctly skipped on the
next run (the intended caching behavior), re-triggering a fetch for the
same range picked up the next batch of up to 10 previously-skipped
activities — exactly matching "more shows up on retry."

Introduced in commit `9d273cf` ("fixing tests"), alongside an unrelated
legitimate fix in the same loop. Not covered by any test — no
`WorkoutFetchWorkerTests.cs` existed before this investigation.

## Fix

Removed the hardcoded `== 10` break so the worker processes every pending
activity before marking the job `Completed`
([WorkoutFetchWorker.cs](../../../strava-segments-performance-backend/Services/WorkoutFetchWorker.cs)).
This is safe under the pipeline's existing bounds: each job already has a
3-hour hard timeout (`WholeFetchTimeout`), and `StravaApiClient` already
handles Strava 429s with its own per-call retry/deadline logic — nothing
was relying on the cap to protect the rate limit.

## Verification

- **Red → green test:** added
  [`WorkoutFetchWorkerTests.cs`](../../../strava-segments-performance-backend-tests/WorkoutFetchWorkerTests.cs),
  seeding 12 pending activities and asserting all 12 end up with fetched
  details when the job reports `Completed`. Failed against the pre-fix code
  (10 fetched, not 12); passed after removing the cap.
- **Full suite:** ran the backend test suite (excluding the
  Testcontainers/Postgres OAuth tests, which need Docker) — 36/36 passing.
- **Production:** shipped via
  [PR #16](https://github.com/dani6666/strava-segments-performance/pull/16),
  merged to `master`. User confirmed on prod: selecting a range now shows
  the full set of activities on the fitness chart.
