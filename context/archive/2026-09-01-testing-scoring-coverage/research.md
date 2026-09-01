---
date: 2026-09-01T00:00:00Z
researcher: daniel.wludarczyk
git_commit: 1882480e69cdaa316fe60b26b38e970de3005792
branch: master
repository: strava-segments-performance
topic: "Scoring pipeline grounding for Phase 1 realistic-data coverage (Risk #1)"
tags: [research, codebase, fitness-scoring, testing, risk-1]
status: complete
last_updated: 2026-09-01
last_updated_by: daniel.wludarczyk
---

# Research: Scoring pipeline grounding for Phase 1 realistic-data coverage (Risk #1)

**Date**: 2026-09-01T00:00:00Z
**Researcher**: daniel.wludarczyk
**Git Commit**: 1882480e69cdaa316fe60b26b38e970de3005792
**Branch**: master
**Repository**: strava-segments-performance

## Research Question

Ground the test plan's §2 Risk #1 ("a scoring change silently produces a
wrong fitness trend on real-world data") for rollout Phase 1. Establish the
exact, locked behavior a realistic multi-workout test must assert against an
**independently hand-computed** oracle: pipeline stages, per-effort scoring
formula, window/normalization semantics, tie/stall rules, and the
minimum-scored-efforts gate. Identify what existing tests already cover and
where the realistic-data gap is.

## Summary

The scorer is a small, pure, deterministic static function —
`FitnessScoring.Score(IEnumerable<SegmentEffortRecord>)` in
[FitnessScoring.cs](strava-segments-performance-backend/Services/FitnessScoring.cs) —
with **no external dependencies, no time, no I/O**. This makes it an ideal
unit-test target and means the cheapest layer (a pure unit test with a
hand-computed oracle) fully covers Risk #1. No integration or e2e is
warranted for the scoring formula itself.

The pipeline has seven observable stages, three locked constants, and a
handful of deliberate drop rules. Every number in the output is derivable by
hand from the inputs, so an independent oracle is feasible. The existing
[FitnessScoringTests.cs](strava-segments-performance-backend-tests/FitnessScoringTests.cs)
already covers each *individual* rule (null HR, stall drop, ties, single-effort
segment, min-3 gate, weighted aggregation, degenerate window) with
hand-computed expectations — its strengths are real. **The gap is a single
realistic, multi-week, multi-segment fixture** where a genuine fitness trend
(same segment times at falling HR → rising score) is asserted end-to-end
against a from-requirements oracle. Today's tests are per-rule micro-scenarios;
none exercises a lifelike dataset that would catch a subtle formula regression
that still passes every micro-test.

**Key correctness anchors the oracle must encode (all locked in source):**

1. Cost = `AverageHeartRate × ElapsedTimeSeconds` (multiplicative, not sum/ratio) — [FitnessScoring.cs:72](strava-segments-performance-backend/Services/FitnessScoring.cs#L72)
2. Percentile: lowest cost → **100**, highest → **0**, ties → average rank — [FitnessScoring.cs:82-105](strava-segments-performance-backend/Services/FitnessScoring.cs#L82-L105)
3. Per-segment weight = **median elapsed time of survivors** — [FitnessScoring.cs:71](strava-segments-performance-backend/Services/FitnessScoring.cs#L71)
4. Stall drop: effort with elapsed > **2×** segment raw median is removed before scoring — [FitnessScoring.cs:15](strava-segments-performance-backend/Services/FitnessScoring.cs#L15), [FitnessScoring.cs:64-65](strava-segments-performance-backend/Services/FitnessScoring.cs#L64-L65)
5. Segment needs **≥ 2 survivors** or it is skipped entirely — [FitnessScoring.cs:66-69](strava-segments-performance-backend/Services/FitnessScoring.cs#L66-L69)
6. Workout needs **≥ 3 scored efforts** or it is absent from the trend — [FitnessScoring.cs:20](strava-segments-performance-backend/Services/FitnessScoring.cs#L20), [FitnessScoring.cs:32](strava-segments-performance-backend/Services/FitnessScoring.cs#L32)
7. Workout aggregate = weighted mean `Σ(weight×percentile) / Σ(weight)` — [FitnessScoring.cs:35-37](strava-segments-performance-backend/Services/FitnessScoring.cs#L35-L37)
8. Global normalization across all surviving workouts: min→0, max→100; if `min == max` → **50** — [FitnessScoring.cs:45-53](strava-segments-performance-backend/Services/FitnessScoring.cs#L45-L53)
9. Only efforts **with** average HR are scored — [FitnessScoring.cs:26](strava-segments-performance-backend/Services/FitnessScoring.cs#L26)
10. Output sorted by workout date ascending — [FitnessScoring.cs:49](strava-segments-performance-backend/Services/FitnessScoring.cs#L49)

## Detailed Findings

### Pipeline stages (input → 0–100 trend)

`Score` in [FitnessScoring.cs:23-55](strava-segments-performance-backend/Services/FitnessScoring.cs#L23-L55):

1. **Filter to efforts with HR** — `.Where(e => e.AverageHeartRate.HasValue)` ([:26](strava-segments-performance-backend/Services/FitnessScoring.cs#L26)). Missing-HR efforts vanish before any grouping.
2. **Group by `StravaSegmentId`** ([:27](strava-segments-performance-backend/Services/FitnessScoring.cs#L27)) — scoring is per-segment (surface/elevation controlled by comparing like-for-like).
3. **Score each segment** via `ScoreSegment` ([:28](strava-segments-performance-backend/Services/FitnessScoring.cs#L28), body [:60-80](strava-segments-performance-backend/Services/FitnessScoring.cs#L60-L80)) → yields `(Effort, Weight, Percentile)` tuples for survivors.
4. **Group survivors by `ActivityId`** (the workout) ([:31](strava-segments-performance-backend/Services/FitnessScoring.cs#L31)).
5. **Drop thin workouts** — `.Where(g => g.Count() >= MinScoredEffortsPerWorkout)` ([:32](strava-segments-performance-backend/Services/FitnessScoring.cs#L32)).
6. **Aggregate** each surviving workout to one raw score via weighted mean ([:35-37](strava-segments-performance-backend/Services/FitnessScoring.cs#L35-L37)); workout date = `WorkoutStartUtc` of first effort ([:35](strava-segments-performance-backend/Services/FitnessScoring.cs#L35)).
7. **Normalize globally** across surviving workouts to 0–100 and sort by date ([:45-53](strava-segments-performance-backend/Services/FitnessScoring.cs#L45-L53)).

### Per-effort scoring (`ScoreSegment`)

[FitnessScoring.cs:60-80](strava-segments-performance-backend/Services/FitnessScoring.cs#L60-L80):

- `rawMedian` = median elapsed over **all** efforts in the segment group ([:63](strava-segments-performance-backend/Services/FitnessScoring.cs#L63)).
- `survivors` = efforts with `ElapsedTimeSeconds <= 2.0 * rawMedian` ([:64-65](strava-segments-performance-backend/Services/FitnessScoring.cs#L64-L65)).
- If `survivors.Count < 2` → `yield break` (segment contributes nothing) ([:66-69](strava-segments-performance-backend/Services/FitnessScoring.cs#L66-L69)).
- `weight` = median elapsed of **survivors** (recomputed post-drop, so a stall never inflates its own weight) ([:71](strava-segments-performance-backend/Services/FitnessScoring.cs#L71)).
- `costs[i]` = `AverageHeartRate!.Value * ElapsedTimeSeconds` ([:72](strava-segments-performance-backend/Services/FitnessScoring.cs#L72)).
- Percentiles via `ComputePercentiles(costs)` ([:73](strava-segments-performance-backend/Services/FitnessScoring.cs#L73)); each survivor yields `(effort, weight, percentile)` ([:75-78](strava-segments-performance-backend/Services/FitnessScoring.cs#L75-L78)).

### Percentile with average rank (`ComputePercentiles`)

[FitnessScoring.cs:82-105](strava-segments-performance-backend/Services/FitnessScoring.cs#L82-L105):

`percentile[i] = 100 * (worseCount + tiedCount/2) / (n - 1)`
where `worseCount` = efforts with **higher** cost, `tiedCount` = efforts with **equal** cost, `n` = survivor count ([:104](strava-segments-performance-backend/Services/FitnessScoring.cs#L104)).

- Lowest cost (best = fast + low HR) → highest `worseCount` → **100**.
- Highest cost → **0**.
- All tied → every effort gets `100 * ((n-1)/2)/(n-1)` = **50**.
- Note `n - 1` denominator: a segment with exactly 2 survivors yields percentiles **0 and 100** (never intermediate). The oracle must account for this quantization on small survivor sets.

### Aggregation and global normalization

- Workout raw score = `Σ(weight_i × percentile_i) / Σ(weight_i)` ([:36](strava-segments-performance-backend/Services/FitnessScoring.cs#L36)) — longer segments (higher median elapsed) dominate.
- `min`/`max` computed across **all surviving workout raw scores** ([:45-46](strava-segments-performance-backend/Services/FitnessScoring.cs#L45-L46)).
- Final = `max > min ? 100 * (raw - min)/(max - min) : 50` ([:52](strava-segments-performance-backend/Services/FitnessScoring.cs#L52)) — a single surviving workout, or all-equal workouts, all resolve to **50**.
- Empty result short-circuits to `[]` ([:40-43](strava-segments-performance-backend/Services/FitnessScoring.cs#L40-L43)).

**Window semantics.** The scorer itself has no notion of a time window — it
normalizes over exactly the set of records it is handed. The "analyzed window"
is applied upstream in
[FitnessTrendQuery.GetForUserAsync](strava-segments-performance-backend/Services/FitnessTrendQuery.cs#L7-L9)
via optional `from`/`to` filters on `Activity.StartDateUtc`
([FitnessTrendQuery.cs:14-22](strava-segments-performance-backend/Services/FitnessTrendQuery.cs#L14-L22)),
inclusive bounds. This means **the same workout gets a different 0–100 score
depending on the window**, because min/max shift. A realistic-data oracle must
fix the window (call `Score` directly with a fixed record set) to remain
deterministic — do not couple the Phase-1 scoring oracle to `from`/`to`
behavior (that is Phase 2's territory, see Historical/Related).

### Data contract into the scorer

`SegmentEffortRecord(long StravaSegmentId, int ElapsedTimeSeconds, double? AverageHeartRate, int ActivityId, DateTime WorkoutStartUtc)` — [FitnessScoring.cs:3-8](strava-segments-performance-backend/Services/FitnessScoring.cs#L3-L8).
`FitnessTrendPoint(DateTime Date, double Score)` — [FitnessScoring.cs:10](strava-segments-performance-backend/Services/FitnessScoring.cs#L10).
`FitnessTrendQuery` builds records straight from the joined query and calls `FitnessScoring.Score` ([FitnessTrendQuery.cs:24-32](strava-segments-performance-backend/Services/FitnessTrendQuery.cs#L24-L32)); `WorkoutStartUtc` is mapped from `Activity.StartDateUtc`, **not** `SegmentEffort.StartDateUtc`.

### Existing test coverage and the gap

[FitnessScoringTests.cs](strava-segments-performance-backend-tests/FitnessScoringTests.cs) (xUnit `[Fact]`, naming `Score_<Condition>_<Expected>`):

| Test | Lines | Rule covered |
|------|-------|--------------|
| `Score_FasterLowerHeartRateEffort_ScoresHigherOnSharedSegment` | [8-38](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L8-L38) | monotonic 0/50/100 across 3 workouts |
| `Score_EffortWithNullHeartRate_IsExcludedFromScoring` | [40-60](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L40-L60) | null-HR drop |
| `Score_SingleEffortSegment_ContributesNothing...` | [62-85](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L62-L85) | <2-survivor segment skip |
| `Score_WorkoutWithNoRepeatedSegments_IsAbsentFromSeries` | [87-107](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L87-L107) | workout absent |
| `Score_EffortFarSlowerThanSegmentMedian_IsDroppedAsStall...` | [109-149](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L109-L149) | 2× stall drop |
| `Score_TiedCosts_ProduceEqualPercentiles` | [151-172](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L151-L172) | tie → equal percentile |
| `Score_SingleScoredWorkout_ScoresFifty` | [174-189](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L174-L189) | degenerate min==max → 50 |
| `Score_LongSegmentOutweighsShortSegmentInAggregation` | [191-228](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L191-L228) | weighted mean (oracle `10000/109`) |
| `Score_OutputIsSortedByWorkoutDateAscending` | [230-252](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L230-L252) | date-ascending order |
| `Score_WorkoutWithFewerThanThreeScoredEfforts_IsExcludedFromSeries` | [254-278](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L254-L278) | min-3 gate |

**Strengths (keep):** each expectation is hand-computed from requirements, not lifted from the scorer; naming is disciplined; edge cases are individually pinned.

**Gap (Phase 1 target):** there is **no single realistic fixture** — e.g. a rider repeating ~4–6 segments across ~6–8 workouts over several weeks, with a genuine improving-fitness signal (holding segment times while HR trends down, plus a stall and a missing-HR effort mixed in) — asserted against a fully hand-derived expected trend. Such a test is the one most likely to catch a subtle formula regression (e.g. swapping weight source, or a percentile off-by-one) that still passes all ten micro-tests. This is exactly the "realistic multi-workout fixture matches an independently hand-computed expectation" bar from the test plan §2 Risk #1 guidance.

### Test conventions to follow (from lessons + existing tests)

- Fixtures are **inline positional `SegmentEffortRecord[]`** with trailing `// comment` intent; no shared builder exists today ([FitnessScoringTests.cs:16-24](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L16-L24)).
- **Lessons rule:** object-creation helpers must have descriptive names — never single-letter (`D()`/`E()` were explicitly rejected). See [lessons.md](context/foundation/lessons.md) "Never use single-letter or cryptic abbreviations for object-creation helpers." If Phase 1 introduces a fixture builder, name it `CreateEffort` / `BuildWorkout`, etc.
- Helper/fake naming precedent: verbose sealed inner classes `StubHandler`, `FakeTokenService` ([StravaApiClientTests.cs:15-46](strava-segments-performance-backend-tests/StravaApiClientTests.cs#L15-L46)).

## Code References

- `strava-segments-performance-backend/Services/FitnessScoring.cs:23-55` — `Score` pipeline (entry point)
- `strava-segments-performance-backend/Services/FitnessScoring.cs:60-80` — `ScoreSegment` (stall drop, cost, weight)
- `strava-segments-performance-backend/Services/FitnessScoring.cs:82-105` — `ComputePercentiles` (average-rank percentile)
- `strava-segments-performance-backend/Services/FitnessScoring.cs:14-20` — locked constants `KStall = 2.0`, `MinScoredEffortsPerWorkout = 3`
- `strava-segments-performance-backend/Services/FitnessTrendQuery.cs:7-33` — window filter + record projection + `Score` call
- `strava-segments-performance-backend/Models/SegmentEffort.cs` — effort entity (no `UserId`; reaches user via `ActivityId`)
- `strava-segments-performance-backend/Models/Activity.cs` — `UserId`, `StartDateUtc`
- `strava-segments-performance-backend-tests/FitnessScoringTests.cs:8-278` — existing scoring tests
- `strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj:1-28` — xUnit 2.9.3, EF Core InMemory 10.0.9, FakeTimeProvider 10.8.0, coverlet 6.0.4, `net10.0`

## Architecture Insights

- **Pure function, no seams needed.** `Score` is static, deterministic, side-effect-free. Risk #1 needs no DbContext, no clock, no mocks — the cheapest possible layer (plain xUnit `[Fact]`) gives full signal. Promoting to integration would add cost with zero extra coverage of the formula.
- **The oracle problem is the real hazard.** Because the scorer is self-relative, it is tempting to "compute expected by running it." The existing weighted-aggregation test resists this by writing `10000.0/109.0` from first principles ([FitnessScoringTests.cs:191-228](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L191-L228)). Phase 1's realistic fixture must do the same: every expected number derived by hand from the ten anchors above, never observed from output.
- **Two quantization traps for the oracle:** (a) the `n-1` percentile denominator makes 2-survivor segments emit only {0,100}; (b) global min/max normalization means adding/removing one workout rescales all others. A realistic fixture must be sized so these are intentional, not accidental.
- **Window coupling is upstream.** Keep the scoring oracle decoupled from `from`/`to`; call `Score` directly with a fixed record list.

## Historical Context (from prior changes)

- `context/archive/2026-08-27-fitness-trend-chart/plan.md` — the fitness-trend feature that introduced the scorer and its first tests; source of the "descriptive helper names" lesson (helpers originally `D()`/`E()` in `FitnessScoringTests.cs`).
- `context/archive/2026-07-10-workout-data-fetch/plan.md` — established the `Activity`/`SegmentEffort` shape and the effort→activity→user chain the scorer consumes.
- `context/foundation/lessons.md` — "Never use single-letter or cryptic abbreviations for object-creation helpers" applies directly to any Phase-1 fixture builder.
- `context/foundation/prd.md` — FR-004 ("formula validation is the core risk"); Business Logic section defines self-relative 0–100 semantics (100 = personal best in window, 0 = worst) that the oracle encodes.

## Related Research

- `context/foundation/test-plan.md` §2 Risk #1 + Risk Response Guidance row #1 (this research grounds that row for Phase 1).
- Phase 2 (`Authorization + endpoint data-path`) will own the effort→activity→user IDOR surface and `from`/`to` window behavior surfaced in [FitnessTrendQuery.cs:10-11](strava-segments-performance-backend/Services/FitnessTrendQuery.cs#L10-L11) — out of scope for Phase 1, noted so the scoring oracle stays decoupled from windowing.

## Open Questions

- **Data provenance (resolved — two-layer):** there is **no real Strava data in the repo** — no captured API dumps, no JSON/CSV fixtures, no seed data; every existing scoring test uses hand-authored inline `SegmentEffortRecord[]` (verified: only `.json` files are build/config artifacts). Phase 1 uses **two complementary fixtures**, because Risk #1 has two distinct oracles:
  - **Layer A — synthetic, exact numeric oracle.** Hand-authored data shaped to look realistic (plausible times/HR/dates, several weeks, an improving-fitness signal + a stall + a missing-HR effort), sized small (~4-6 segments × ~6-8 workouts) so **every expected number is hand-computable** from the ten anchors. Pins the *formula* (magnitude correctness — catches weight-source swaps, percentile off-by-one). This layer is mandatory; a real Strava dump is too large to hand-oracle, so its expected values could only come from running the scorer (the anti-pattern's tautology).
  - **Layer B — real activities, frozen, ordinal oracle.** Fetch **2-5 of the user's own real activities** where the user knows the ground-truth ranking ("on segment X, same time at clearly lower HR in ride B"), then **freeze the values into an inline fixture** (transcribe segment id / elapsed seconds / avg HR / activity id / date; anonymize raw Strava IDs). Assert **relative ordering** (`score(A) > score(B)`), never live-fetched. This is *not* tautological: the expected ranking comes from the requirement's definition of fitness applied to reality, not from the scorer. It validates the actual product-level claim in Risk #1 ("silently lies about whether the user is getting fitter"). Constraints: (1) test stays deterministic/offline — no Strava call at test time (network flakiness, rate limits per AGENTS.md, PII); (2) selected activities must **share several common segments** so scored workouts clear the ≥2-survivor and ≥3-effort gates ([FitnessScoring.cs:20](strava-segments-performance-backend/Services/FitnessScoring.cs#L20), [:66-69](strava-segments-performance-backend/Services/FitnessScoring.cs#L66-L69)) — otherwise the workouts drop out and there is nothing to compare.
  - **Why both:** ordinal alone is too weak (a regression can preserve ordering while corrupting magnitudes: `0/50/100` vs `40/45/50`); exact-synthetic alone doesn't prove the scorer matches lived reality. Layer A pins the formula, Layer B pins reality.
- **Fixture format:** inline `SegmentEffortRecord[]` (matches existing style) vs. a small named builder. Existing tests are inline; a realistic multi-week fixture may be more readable with a descriptively named builder — decision deferred to `/10x-plan` under the lessons.md naming rule.
- **Assertion tolerance:** existing precise test uses `precision: 6` on a rational expectation. The realistic oracle will produce rationals; confirm the same `Assert.Equal(expected, actual, precision)` convention and pick a precision that is tight but robust to double rounding.
- **Cookbook §6.1:** this phase must fill `test-plan.md` §6.1 (location, naming, reference test, run command) — captured for `/10x-plan`.
