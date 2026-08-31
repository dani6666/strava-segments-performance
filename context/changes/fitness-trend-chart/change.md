---
change_id: fitness-trend-chart
title: Fitness trend chart
status: impl_reviewed
created: 2026-08-27
updated: 2026-08-31
archived_at: null
---

## Notes

<!-- Free-form notes for this change: links, ad-hoc context, decisions that don't belong in research/frame/plan. -->

**Phase 3 finding (pre-existing, not introduced by this change):** `strava-segments-performance/angular.json` has no `test` architect target configured, and the project has zero `*.spec.ts` files anywhere. `npm test` (Karma) as an automated success criterion is N/A until test infrastructure is set up as its own separate effort — user confirmed treating it as N/A and proceeding for Phase 3.

**Post-Phase-1 decision (during Phase 3):** user requested a minimum-sample-size guard: a workout must have at least 3 scored segment efforts (post-stall-drop, post-per-segment-N≥2-filter) to receive a fitness score at all; below that it's excluded from the series entirely, same as the existing "no repeated segments" gap. Implemented as `FitnessScoring.MinScoredEffortsPerWorkout = 3` in [FitnessScoring.cs](../../../strava-segments-performance-backend/Services/FitnessScoring.cs), applied as a `.Where(g => g.Count() >= 3)` filter on the per-workout grouping before aggregation. All Phase 1/2 unit test fixtures were reworked to give asserted workouts ≥3 contributing efforts (by repeating the same relative effort pattern across multiple segments, which preserves the original expected scores unchanged); one new test (`Score_WorkoutWithFewerThanThreeScoredEfforts_IsExcludedFromSeries`) locks the new boundary directly.
