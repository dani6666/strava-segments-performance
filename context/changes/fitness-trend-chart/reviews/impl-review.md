<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Fitness Trend Chart (S-03)

- **Plan**: context/changes/fitness-trend-chart/plan.md
- **Scope**: Phases 1–3 of 3 (all complete) + min-3-efforts addition
- **Date**: 2026-08-31
- **Verdict**: APPROVED
- **Findings**: 0 critical, 1 warning, 2 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | PASS |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | PASS |

## Findings

### F1 — Min-3 guard counts efforts, not distinct segments

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Plan Adherence
- **Location**: strava-segments-performance-backend/Services/FitnessScoring.cs:33
- **Detail**: `MinScoredEffortsPerWorkout` is applied as `.GroupBy(ActivityId).Where(g => g.Count() >= 3)`, where `g` is a group of scored *efforts*. An activity that repeats a single segment 3+ times (a lap/loop ride) passes the guard while representing only ONE distinct segment — arguably still "too thin a sample" per the code's own comment. Your original request said "at least 3 segment efforts" (efforts — matches the code), but your later debugging phrasing said "min 3 scoring segments" (distinct segments). The two interpretations filter differently for loop rides.
- **Fix A ⭐ Recommended**: Keep effort-count semantics as-is (matches the literal original request); no change.
  - Strength: Simpler; the loop-ride case is rare and 3 repeats of one segment still gives 3 real HR/time comparisons against history.
  - Tradeoff: A single-segment loop ride can still score, which may read as "too thin" to a strict reviewer.
  - Confidence: HIGH — behavior is correct and tested; this is a semantics choice, not a defect.
  - Blind spot: How common loop/lap rides are in your actual data.
- **Fix B**: Switch to distinct-segment count — `.Where(g => g.Select(x => x.Effort.StravaSegmentId).Distinct().Count() >= 3)`.
  - Strength: Guarantees ≥3 independent segments behind every score; matches the "3 scoring segments" phrasing.
  - Tradeoff: Filters out otherwise-valid loop rides; needs a new test and a fixture rework.
  - Confidence: MED — straightforward change but shifts what qualifies.
  - Blind spot: Whether any currently-shown workouts would disappear.
- **Decision**: ACCEPTED (Fix A) — keep effort-count semantics, matches the original request; no code change.

### F2 — Aggregation divides by total weight with no zero guard

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend/Services/FitnessScoring.cs:37
- **Detail**: `Score = g.Sum(x => x.Weight * x.Percentile) / g.Sum(x => x.Weight)`. The denominator is a sum of per-segment median elapsed times. It can only be zero if every contributing effort has `ElapsedTimeSeconds == 0`, which cannot happen for real Strava segment efforts (a segment always takes time). Flagged only for completeness — no fix needed unless synthetic/test data with zero elapsed times is ever fed in.
- **Fix**: None required; optionally assert elapsed > 0 at ingestion if synthetic data is a concern.
- **Decision**: SKIPPED — zero case is impossible for real Strava data.

### F3 — AnalysisService.setFailed drops the error detail (minor pattern deviation)

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: strava-segments-performance/src/app/workouts/analysis.service.ts:33
- **Detail**: The mirrored service `WorkoutFetchService.setFailed(err)` captures `err` into an `errorMessage` signal the template shows. `AnalysisService.setFailed()` takes no argument and only flips `loadState` to `'error'`, and the template shows a fixed "Couldn't load fitness trend." message. This is an intentional simplification (the plan said "setFailed()-style handler") and the generic message is fine for v1, but it diverges slightly from the sibling pattern and loses the underlying error for diagnostics.
- **Fix**: Optionally capture the error into an `errorMessage` signal like `WorkoutFetchService` if you later want a specific failure message; not required for this slice.
- **Decision**: SKIPPED — generic message acceptable for v1.
