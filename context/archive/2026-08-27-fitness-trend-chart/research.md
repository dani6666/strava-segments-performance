---
date: 2026-08-27T13:47:06+02:00
researcher: Daniel Włudarczyk
git_commit: 7ad7d119ed63ae863847e8ace1016d329c14b221
branch: claude/10x-new-fitness-trend-chart-b65134
repository: strava-segments-performance
topic: "Fitness scoring formula and trend chart (S-03) — algorithm design + rendering plumbing"
tags: [research, codebase, scoring, fitness-score, angular-chart, segment-effort, backend-analysis]
status: complete
last_updated: 2026-08-27
last_updated_by: Daniel Włudarczyk
---

# Research: Fitness trend chart (S-03) — scoring formula + chart plumbing

**Date**: 2026-08-27T13:47:06+02:00
**Researcher**: Daniel Włudarczyk
**Git Commit**: 7ad7d119ed63ae863847e8ace1016d329c14b221
**Branch**: claude/10x-new-fitness-trend-chart-b65134
**Repository**: strava-segments-performance

## Research Question

For roadmap slice **S-03 (`fitness-trend-chart`)**: how should the app compute a self-relative 0–100 fitness score per workout from cached Strava segment data (elapsed time + average HR), and how should that score be surfaced as a trend chart in the Angular frontend? Research emphasis was **formula-first** (the PRD flags the scoring formula as "the core risk to iterate on"), plus a **charting-library survey**.

## Summary

- **Everything the scoring formula needs is already persisted.** Per segment effort we have `StravaSegmentId`, `ElapsedTimeSeconds`, `AverageHeartRate` (nullable), `StartDateUtc`, and a link to the parent `Activity` (which carries `UserId` and workout `StartDateUtc`). No schema change is required to ship a first formula. ([SegmentEffort.cs](strava-segments-performance-backend/Models/SegmentEffort.cs), [Activity.cs](strava-segments-performance-backend/Models/Activity.cs))
- **No analysis/scoring code exists yet** — this is greenfield. A new `.RequireAuthorization()` minimal-API endpoint slots into `Program.cs` alongside the workout endpoints, using the established `AppDbContext`-in-lambda + current-user-from-claim pattern. ([Program.cs](strava-segments-performance-backend/Program.cs))
- **Recommended formula (parameter-free, honoring PRD "no user knobs"):** per-effort **heartbeat cost** `C = ElapsedTime × AvgHR` → **per-segment percentile rank** (robust, self-relative) → **duration-weighted mean** across a workout's efforts → **final window-level min–max rescale** so the window's best workout reads exactly 100 and the worst 0. One point per workout, keyed on `Activity.StartDateUtc`.
- **Frontend is greenfield for viz** (no chart code, no chart dependency). The insertion point is the `@case ('completed')` branch of the existing fetch panel in `dashboard.component.html`. A new `providedIn:'root'` analysis service should mirror `WorkoutFetchService` exactly (signals + `withCredentials: true` + typed responses).
- **Charting recommendation: `ng2-charts` + `chart.js`** (Angular-21-verified, standalone-first, best TS typings, canvas handles 1000 pts + tooltips + fixed 0–100 axis with zero custom code; cost = one extra peer dep `@angular/cdk`). **Hand-rolled inline SVG** is the genuine lightweight runner-up if avoiding all dependencies matters more than free tooltips.

## Detailed Findings

### Backend data model — inputs available to scoring

Persisted per **segment effort** ([SegmentEffort.cs](strava-segments-performance-backend/Models/SegmentEffort.cs)):

| Field | Type | Nullable | Scoring role |
|---|---|---|---|
| `StravaSegmentId` | `long` | no (:8) | **Group key** — controls surface/elevation; normalize within this |
| `ElapsedTimeSeconds` | `int` | no (:10) | Performance input + aggregation weight |
| `AverageHeartRate` | `double?` | **YES (:11)** | Performance input — **must handle NULL** |
| `StartDateUtc` | `DateTime` | no (:12) | Places effort in window |
| `ActivityId` | `int` | no (:6) | Link to parent workout (no nav prop / FK) |
| `StravaSegmentEffortId` | `long` | no | Unique index |
| `SegmentName` | `string` | no | Label only |

Persisted per **activity/workout** ([Activity.cs](strava-segments-performance-backend/Models/Activity.cs)): `Id`, `UserId` (:7, FK-by-convention), `StravaActivityId`, `Name`, `SportType` (:9), `StartDateUtc` (:10 — the **chart x-axis**), `DistanceMeters`, `MovingTimeSeconds`, `ElapsedTimeSeconds`, `DetailsFetched`, `FetchedAtUtc`.

Key structural facts:
- **No EF navigation properties and no FK constraints anywhere** ([AppDbContext.cs:15-35](strava-segments-performance-backend/Data/AppDbContext.cs)) — joins `SegmentEfforts.ActivityId → Activities.Id` are manual LINQ. `SegmentEfforts` has **no `UserId` column**; scope-by-user requires the join to `Activities`.
- **No index on `StravaSegmentId` or `ActivityId`** ([migration 20260709231202_AddWorkoutFetching.cs:80-84](strava-segments-performance-backend/Migrations/20260709231202_AddWorkoutFetching.cs)) — the per-segment grouping query will seq-scan. Consider adding indexes if perf matters (dataset is small per PRD, so likely fine for v1).
- Activities are **already filtered to cycling + has-heartrate at fetch time** ([StravaDtos.cs:40-51](strava-segments-performance-backend/Services/StravaDtos.cs), [WorkoutFetchWorker.cs:80-81](strava-segments-performance-backend/Services/WorkoutFetchWorker.cs)) — `CyclingSportTypes` = Ride/MountainBikeRide/GravelRide/VirtualRide/Handcycle/Velomobile, requiring `HasHeartrate`. But **individual efforts can still have NULL avg HR** even under an HR activity.

Fields Strava returns but we **do NOT persist** (would need DTO + entity + migration to use): per-effort `moving_time`, `average_watts`, `average_cadence`, `max_heartrate`, `pr_rank`/`kom_rank`, **effort `distance`/segment length**, and the activity's own `average_heartrate` ([StravaDtos.cs:10-15, 67-77](strava-segments-performance-backend/Services/StravaDtos.cs)). Notably **there is no per-segment length/distance field** — any "weight by segment length" scheme must fall back to `ElapsedTimeSeconds` or add a persisted field.

### Where the analysis endpoint plugs in

No `/api/analysis` endpoint exists. Existing endpoints in [Program.cs](strava-segments-performance-backend/Program.cs): `/health` (:137), `/auth/login` (:140), `/api/auth/me` (:148), `/api/auth/logout` (:160), `/api/workouts/fetch` (:190), `/api/workouts/fetch-status` (:232). Add a new `app.MapGet("/api/analysis/...", async (HttpContext ctx, AppDbContext db) => {...}).RequireAuthorization();` before `app.Run()` (:241).

**Current-user query pattern** (used at Program.cs:192-193, :234-235; WorkoutFetchWorker.cs:57):
```csharp
var stravaId = long.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
var user = await db.Users.FirstAsync(u => u.StravaAthleteId == stravaId);
// scoring query then joins efforts→activities on a.UserId == user.Id, groups by StravaSegmentId
```

### Scoring formula design (core focus)

**Recommended end-to-end algorithm** (all parameter-free — honors PRD "no user-configurable parameters", [prd.md:87](context/foundation/prd.md)):

```
INPUT: all SegmentEfforts for the user with StartDateUtc in the analyzed window.

1. Per-effort measure (HR-aware, no free weight):
   for each effort with non-null AvgHR:
       C = ElapsedTimeSeconds * AverageHeartRate      # "heartbeat cost"; lower = fitter
   # efforts with NULL HR are DROPPED here

1b. Stall hygiene (in v1 — handles mid-segment stops):
   for each segment, w_s = median(ElapsedTimeSeconds) over its window efforts
   DROP any effort with ElapsedTimeSeconds_e > k * w_s   # k ~= 2.0, internal constant (NOT a user knob)
   # refinement to validate: only drop when t high AND avg HR low vs segment norm (the "stop" signature,
   #   distinct from a genuine hard day where both t and HR are high)

2. Per-segment normalization (self-relative, robust):
   group by StravaSegmentId
   for each segment with N >= 2 scored efforts:
       p_e = 100 * (# efforts with C worse than e) / (N - 1)   # best->100, worst->0
   # segments with N < 2 contribute nothing

3. Per-workout aggregation (weighted by segment characteristic duration):
   group scored efforts by ActivityId
   S_w = sum(w_{s(e)} * p_e) / sum(w_{s(e)})   # w_s = segment's median elapsed time (a per-segment constant)
   # weight by the segment's TYPICAL duration, NOT the effort's own ElapsedTime -- so a stalled effort
   #   cannot buy itself outsized weight, and a slow day no longer weighs more than a fast one
   # workout with 0 scored efforts -> no chart point (gap)

4. Final self-relative rescale to exact 0..100 across the window:
   F_w = 100 * (S_w - Smin) / (Smax - Smin)
   # guarantees window's best workout = 100, worst = 0 (prd.md:49,74)
   # single scored workout (Smax==Smin) -> report 50 or suppress

OUTPUT: one (Activity.StartDateUtc, F_w) point per scored workout.
```

**Why each choice:**
- **Per-effort = product `t·hr` (heartbeat cost).** Monotonic in both "better" directions (faster at same HR, or same time at lower HR), parameter-free, and physically meaningful (≈ beats to complete the segment; the parameter-free cousin of cycling's Efficiency Factor, substituting speed for the power we don't store). Rejected: **weighted composite** `w·norm(t)+(1−w)·norm(hr)` needs an arbitrary `w` (PRD forbids knobs); **2-D Pareto** leaves most effort pairs incomparable so can't collapse to a deterministic scalar.
- **Per-segment percentile rank** over min–max/z-score: robust to a single stopped effort (it just becomes rank 0 and can't smear the rest), naturally bounded 0–100, self-relative by construction. Cost: discards magnitude (big PB and small PB both ~100) — acceptable for a *trend* signal; min–max is the named fallback if magnitude sensitivity matters.
- **Duration-weighted mean** aggregation, weighted by each segment's **characteristic (median) duration** — a 20-min climb counts more than a 30-sec sprint. Using the segment's median (a per-segment constant) instead of the effort's own `ElapsedTimeSeconds` is deliberate: it prevents a stalled effort from buying itself outsized weight (see stop-handling below) and removes the perverse coupling where a slow day would weigh more than a fast one. Parameter-free (median is data-derived). **Median-of-scores** is the robustness fallback for the aggregation itself.
- **Final window min–max rescale** resolves a real tension: per-segment normalization only guarantees 0/100 *per segment*, so a naive pipeline compresses toward the middle (top workout might read 85). The final stretch delivers the literal PRD guarantee (100 = peak, 0 = lowest). Because `S_w` is already a multi-segment aggregate, workout-level min–max is far less outlier-fragile than it would be per-effort; swap for workout-level percentile if a single disastrous ride distorts it.

**Complexity:** O(E log E) in-memory batch (E = total efforts) — milliseconds for ~1000 workouts, well inside the 30s budget ([prd.md:66](context/foundation/prd.md)). Pure batch recompute per analysis, consistent with cache-and-reuse.

**Edge-case handling:**
| Case | Handling |
|---|---|
| NULL / missing HR | Drop that effort (formula is HR-aware by design; `C` undefined without HR). Require HR on historical efforts used for normalization too. All-NULL workout → gap, not a time-only fallback. |
| Segment ridden once in window (N=1) | Unscorable (no spread); excluded. Workout survives on its other repeated segments. |
| Workout with no repeated segments | No chart point (leading/interior gap); becomes scorable once repeats accumulate in the batch. |
| Stop mid-segment (inflated elapsed time) | **Handled in v1, three layers.** A stop inflates `ElapsedTime` (dampened slightly because avg HR sags during the stop → `C=t·hr` grows less than `t` alone). (1) **Percentile normalization** floors the stalled effort at rank 0 and — being ordinal — prevents it from distorting peers on that segment. (2) **Weight by segment median duration, not the effort's own elapsed time** (step 3), so the stall can't buy outsized aggregation weight — the leak in a naive duration-weighted mean. (3) **Drop efforts with `t_e > k·median(t_segment)`** (k≈2.0, internal constant, not a user knob) *before* scoring, so egregious stalls don't count at all; a workout left with no comparable effort becomes an honest gap rather than a penalized point. Refinement to validate: gate the drop on the stop *signature* (t high AND HR low vs segment norm) to spare genuine hard days. **Cleaner source-level fix (deferred, needs a call):** persist Strava's `moving_time` (already returned, currently dropped at StravaDtos.cs:10-15) and use it instead of `elapsed_time` — removes stopped time at the source, but requires a DTO+entity+migration change and a re-fetch/backfill of already-cached efforts. |
| Wind / draft noise | Irreducible with time+HR (weather is an explicit non-goal, prd.md:84). Mitigated structurally: segment identity controls elevation, multi-segment aggregation and the trend chart smooth noise. Known validation risk. |

**Biggest validation risk (PRD's "core risk"):** the multiplicative time↔HR tradeoff in `C=t·hr` is an unvalidated modeling assumption, and average HR is a noisy proxy (cardiac drift, heat, fatigue, caffeine, sleep, draft). Single per-workout points will be jumpy even for a genuinely improving athlete; percentile normalization is also population-size sensitive (coarse ranks when a segment has few efforts). **Iteration plan:** validate against known training blocks and eyeballed raw data; treat min–max and z-score variants as an A/B sensitivity suite; consider a smoothed trend line over the raw dots.

### Per-effort measure: `C = HR·t` vs a physically-grounded Efficiency Factor

Every candidate is a cost to cover the segment, `cost = f(HR) · g(t)`, lower = fitter. `C = HR·t` is the degenerate case `f = identity`, `g = identity`. Two physiologically/physically motivated upgrades were evaluated (do **not** assume they're improvements — see verdicts):

**g(t) — time → effort is nonlinear (STRONG, physically correct).** Physiological effort tracks power, and power = force × velocity. Aerodynamic drag *force* ∝ v², so aero *power* ∝ **v³** (cube). The exponent is gradient-dependent: flat/fast segments are aero-dominated (`P ∝ v³`, a 10% faster time ≈ +37% power); steep climbs are gravity-dominated (`P ∝ v¹`, ≈ +10%). So "a 10% time gain on a fast segment is worth far more than on a slow one" is quantitatively true (30→40 km/h ≈ ×2.4 aero power). **To do it right you must persist the segment `distance`** (→ speed `v = d/t`; distance is constant per segment, so one length per segment suffices) **and ideally `average_grade`** (→ pick the exponent). Both are in the Strava payload we already fetch but currently drop ([StravaDtos.cs:10-15](strava-segments-performance-backend/Services/StravaDtos.cs)). Same DTO+entity+migration + backfill tradeoff as `moving_time`.
- **Two catches:** (1) **percentile normalization discards exactly this magnitude** — Point 2 only pays off with a magnitude-preserving normalization (min–max, z-score, or "% of your best EF"); you cannot fully keep both percentile robustness and physics magnitude. (2) The "fast segments count more" effect is most naturally expressed as a **power-weighted aggregation** (hard segments carry more watts), not in the per-effort measure.

**f(HR) — HR reserve / Karvonen (LEGITIMATE but narrow; lower priority).** `%HRR = (HR − HR_rest)/(HR_max − HR_rest)` tracks %VO₂ better than raw HR, so subtracting a baseline is real sports science. But: (a) **at fixed time it changes nothing** — ranking by HR is identical with or without a constant subtraction; it only re-tunes the HR↔time exchange rate, and the core fitness signal (HR drifting down at the *same* segment time) is a fixed-time comparison. (b) **Naive `(HR−k)·t` rewards slowness** (`= C − k·t`; bigger `t` gets a bigger subtraction) — HRR belongs in an efficiency *ratio* (speed per HRR), not a raw product. (c) The "zone 2 ≈ ½ of zone 4" intuition is a training-*stress* notion (exponential, à la Banister TRIMP), not effort *demand*; for a steady segment effort VO₂ is ~linear in HRR (zone2/zone4 ≈ 1.3×, not 2×), so **linear HRR is the physiologically correct choice, not exponential.** TRIMP also has the wrong polarity (measures load, not fitness) and cannot be swapped in as "lower = fitter."

**Field-grade target both converge on:** the cycling **Efficiency Factor**, `EF = Power / HR_reserve` (higher = fitter) — what coaches actually track (TrainingPeaks EF = Normalized Power / avg HR). `C = HR·t` is its data-cheap shadow (raw HR instead of HRR, raw time instead of a power model). Computing EF needs: persisted segment `distance` + `average_grade`, and **measured effort `average_watts` when present** (power-meter riders give real power; estimate is the fallback — do not mix measured and estimated within one segment's history).

**Recommendation / trajectory:**
| Data available | Per-effort measure | Verdict |
|---|---|---|
| v1, no schema change | keep `C = HR·t` (or A/B the `(HR−HR_rest)·tⁿ`, n≈1.5–2 variant) | Downstream percentile normalization mutes both tweaks; ship simple, treat variants as the PRD-mandated sensitivity suite |
| + persist segment `distance` & `average_grade` *(recommended iteration)* | `EF = P(v)/(HR−HR_rest)`, magnitude-preserving normalization, power-weighted aggregation | Delivers **both** critiques with real physics; cross-segment comparable; "fast segments count more" falls out naturally |
| + use measured `average_watts` where present | measured `Power/HRR`, estimate as fallback | Best fidelity; mind mixed-source caveat |

**Net steer:** Point 2 (speed→power) justifies a schema addition; Point 1 (HR baseline) mostly does not. The two are coupled — investing in the physics only pays off if percentile normalization is dropped for something magnitude-aware. So this reduces to one planning decision: **stay with the cheap self-relative proxy, or commit to persisting distance/grade/watts and build a proper Efficiency-Factor metric.**

**DECISION (v1): keep `C = HR · t` unchanged — both critiques declined for v1.** Rationale: leaving HR *and* time un-transformed is self-mitigating. Under the raw product, an equal-percentage HR-up/time-down swap favors the faster effort by exactly `x²` (`(1+x)(1−x)=1−x²`), so e.g. a 5%-faster / 5%-higher-HR effort on a fast segment scores as a slight *win* (`0.9975·C`), not a loss. Subtracting an HR baseline (Point 1) would amplify the HR side (5% raw ≈ +8.3% on reserve) and flip that same effort to a *loss* — the opposite of what a fast segment deserves. So *not* subtracting the base partially compensates for *not* applying the time-power law (Point 2): both push time to matter more relative to HR. **Caveat (on record):** the compensation is directional, not calibrated — raw HR only makes time win by ~`x²`, whereas a truly aero segment's cube law would want the time gain to count several-fold more. So `HR·t` stops scoring the effort as *worse* but still under-credits how good a time gain is on a fast segment; acceptable for a parameter-free self-relative trend. EF (`Power/HRR`) remains the documented iteration if real data shows the proxy is too noisy.

### Frontend post-auth surface

- **Authenticated landing = `/dashboard`** → `DashboardComponent`, guarded by `authGuard` ([app.routes.ts:6](strava-segments-performance/src/app/app.routes.ts)). `''` and `**` redirect to it. OAuth is a full-page redirect to the backend ([auth.service.ts:24-26](strava-segments-performance/src/app/auth/auth.service.ts)).
- **Insertion point:** the S-02 fetch UI is a single `@switch (fetchService.status().status)` block in [dashboard.component.html:8-46](strava-segments-performance/src/app/dashboard/dashboard.component.html). The chart renders in/after the `@case ('completed')` branch.
- **API service pattern to mirror (`WorkoutFetchService`):** `@Injectable({providedIn:'root'})`, constructor-injected `HttpClient`, URLs `${environment.apiBaseUrl}/api/...` ([environment.ts:3](strava-segments-performance/src/environments/environment.ts) = `http://localhost:5000` dev, prod = same-origin), **every call passes `{ withCredentials: true }`** (cookie auth, no interceptor/token), responses generic-typed with an interface declared in the same file, errors routed to a private `setFailed()` ([workout-fetch.service.ts:9-15,33-82](strava-segments-performance/src/app/dashboard/workout-fetch.service.ts)).
- **State/polling idiom:** state in a single Angular `signal<>()`; polling via RxJS `interval(2000)` → `switchMap` GET → `tap` update signal → `takeWhile(..., true)` self-terminate ([workout-fetch.service.ts:27,57-74](strava-segments-performance/src/app/dashboard/workout-fetch.service.ts)). Analysis result flows the same way (POST kick-off, read result or poll).
- **Conventions:** standalone components, separate `.html`/`.scss` files, new control-flow (`@if`/`@for`/`@switch`, no `*ngIf`), signals (no `@Input`/async pipe), default change detection, DI via constructor. Strava-orange palette `#fc4c02` / hover `#d84300`, grays `#555/#333/#777/#ddd`, error `#c0392b` ([dashboard.component.scss](strava-segments-performance/src/app/dashboard/dashboard.component.scss)).
- **No chart code or chart dependency exists.** Frontend deps: only `@angular/*` ^21.2.0, `rxjs ~7.8.0`, `tslib` ([package.json:13-22](strava-segments-performance/package.json)). No `@angular/cdk`, `@angular/animations`, or `platform-browser-dynamic`.

### Charting library survey (Angular 21.2.x, verified against npm registry Aug 2026)

| Option | Ng21? | Bundle | Standalone | TS typings | 1000 pts + tooltips + 0–100 axis | Verdict |
|---|---|---|---|---|---|---|
| **ng2-charts + Chart.js** | ✅ `ng2-charts@10`, `chart.js@4` | ~45–60 KB gz (canvas, tree-shaken) | Excellent (`BaseChartDirective`) | **Best** (`ChartConfiguration<'line'>`) | Trivial, all built-in | **1st pick** — adds one peer dep `@angular/cdk@^21` |
| **Hand-rolled inline SVG** | ✅ n/a | ~1–3 KB own code | Perfect | Perfect | Easy; tooltip hit-test is ~30 LOC you own | **2nd** — genuine lightweight, zero deps |
| ngx-charts (Swimlane) | ✅ `@25` | ~50–70 KB gz + d3 tree | Good | Weak-ish | Works (SVG heavier at 1000) | Over-provisioned — needs 3 extra Angular peers (`animations`, `platform-browser-dynamic`, `cdk`) + d3 |
| ngx-echarts | ⚠️ **must pin `@21`** (`@22`=Ng22 only) | Heaviest (~50–90 KB gz) | Fine | Verbose | Excellent, overkill | Version-pin footgun |

**Recommendation: ng2-charts + Chart.js** — tooltips are a stated need and it delivers them without custom code; canvas makes 1000 points a non-issue; strongest typings for strict TS. Install: `npm install ng2-charts chart.js @angular/cdk`. **Hand-rolled SVG** is a legitimate alternative given the "lightest option is acceptable" steer — pick it only if avoiding every dependency outweighs free tooltip hit-testing.

## Code References

- `strava-segments-performance-backend/Models/SegmentEffort.cs:6,8,10,11,12` — scoring inputs (ActivityId, segment id, elapsed time, nullable HR, date)
- `strava-segments-performance-backend/Models/Activity.cs:7,9,10` — UserId, SportType, workout StartDateUtc (chart x-axis)
- `strava-segments-performance-backend/Data/AppDbContext.cs:15-35` — no FKs/nav props; index config only
- `strava-segments-performance-backend/Migrations/20260709231202_AddWorkoutFetching.cs:37-54,80-84` — SegmentEfforts schema; nullable HR; no index on segment/activity id
- `strava-segments-performance-backend/Services/StravaDtos.cs:10-15,40-51,67-77` — effort DTO/mapping; cycling+HR filter; unpersisted fields
- `strava-segments-performance-backend/Program.cs:190,232,241` — endpoint registration pattern; where analysis endpoint plugs in
- `strava-segments-performance/src/app/app.routes.ts:6` — `/dashboard` authenticated landing
- `strava-segments-performance/src/app/dashboard/workout-fetch.service.ts:9-15,27,33-82` — service/signal/polling/`withCredentials` pattern to mirror
- `strava-segments-performance/src/app/dashboard/dashboard.component.html:8-46` — `@switch` fetch panel; chart insertion point (`completed` case)
- `strava-segments-performance/package.json:13-22` — no chart dep; Angular 21.2.x
- `context/foundation/prd.md:47-51,61-62,66,68-76,84,87` — US-01, FR-004, perf budget, business logic, non-goals

## Architecture Insights

- **Batch-recompute model fits the codebase.** No FKs/nav props, manual LINQ joins, small dataset, cache-and-reuse of raw efforts — a stateless per-request scoring pass (no stored scores) matches both the data layer and the PRD's "reuse cached workouts, recompute analysis" framing. Scores need not be persisted for v1.
- **Cookie-auth everywhere, no interceptor.** Any new frontend call must set `withCredentials: true` explicitly; any new backend endpoint must `.RequireAuthorization()` and resolve the user from the `NameIdentifier` claim → `User.Id`.
- **Parameter-free is a hard design constraint** (PRD non-goal "no user knobs"), which is why the recommended pipeline avoids every tunable weight — a real constraint on formula choice, not just a preference.
- **The formula is deliberately swappable.** Percentile↔min–max at step 2, weighted-mean↔median at step 3, min–max↔percentile at step 4 are all drop-in variants — build the pipeline so these are isolated, testable functions, because the PRD demands formula iteration.

## Historical Context (from prior changes)

- `context/archive/2026-07-10-workout-data-fetch/` — S-02, the direct prerequisite. Established the fetch worker, caching, `WorkoutFetchStatus` progress model, and the effort/activity persistence this slice consumes. Lesson carried forward: **review EF migrations before applying** (`context/foundation/lessons.md`) — relevant only if S-03 adds a field (e.g. segment length or persisted scores), which the recommended v1 formula avoids.
- `context/changes/strava-oauth-login/` — S-01, established the cookie/claim auth the analysis endpoint reuses.

## Related Research

- None prior for this change. Roadmap slice definition: `context/foundation/roadmap.md` (S-03). PRD: `context/foundation/prd.md` (FR-003/FR-004/US-01, Business Logic).

## Open Questions

1. **Persist scores or recompute each request?** Recommended: recompute (small data, matches cache model). Revisit only if the 30s budget is threatened.
2. **Add indexes on `SegmentEfforts.StravaSegmentId` / `ActivityId`?** Not needed for v1's small dataset; a cheap migration if the grouping query is slow.
3. **Smoothed trend line vs raw dots?** PRD says "one data point per workout"; a smoothing overlay may be needed to make the noisy HR signal legible — decide during planning, not required for the acceptance criteria.
4. **Chart library vs hand-rolled SVG** — a genuine either/or for `/10x-plan` to lock. ng2-charts (free tooltips, +1 dep) vs inline SVG (zero deps, own the hover logic).
5. **`timeframe-selection` (S-04) integration** — S-04 runs parallel and the "window" is central to this self-relative formula. Agree the date-range contract (query params on the analysis endpoint) now so the two integrate cleanly.
6. **What defines the default "window"?** All cached workouts (per PRD default). Confirm the analysis endpoint's default = entire history unless a timeframe is passed.
7. **Mid-segment-stop handling depth.** V1 handles it statistically with no schema change (percentile floor + segment-median weighting + `t>k·median` drop, k≈2.0). Decide during planning: (a) tune/validate `k` against real data, (b) whether to also gate the drop on the stop signature (t high + HR low), and (c) whether to additionally persist Strava `moving_time` for a source-level fix (cleaner, but DTO+entity+migration + re-fetch/backfill of cached efforts).
8. **Per-effort measure fidelity — DECIDED for v1: keep `C = HR·t`.** Both the HR-reserve baseline (Point 1) and the speed→power law (Point 2) are declined for v1; leaving HR and time un-transformed is self-mitigating (raw HR down-weights HR sensitivity, offsetting the missing time-power law — see "Per-effort measure" DECISION above). The physically-grounded **Efficiency Factor** (`Power/HR_reserve`, needs persisted segment `distance`+`average_grade`+measured `average_watts`, and a magnitude-preserving normalization) remains the documented first iteration if real data shows the proxy is too noisy — not in v1 scope.
