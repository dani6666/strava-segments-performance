# Scoring Realistic-Data Coverage (Risk #1) Implementation Plan

## Overview

Close the Phase 1 gap identified in `context/foundation/test-plan.md` §3: the
scorer has ten per-rule micro-tests but **no lifelike, multi-week fixture** that
would catch a subtle formula regression which still passes every micro-test.
This plan adds two realistic-data tests (a synthetic exact-shape test and a
deferred real-activity ordinal test) and wires the CI gate that makes the
backend test suite enforceable — which is currently absent.

## Current State Analysis

- The scorer, [FitnessScoring.Score](strava-segments-performance-backend/Services/FitnessScoring.cs#L23), is a **pure, deterministic, dependency-free** static function. Risk #1 is fully coverable at the unit layer — no DbContext, clock, or mocks.
- [FitnessScoringTests.cs](strava-segments-performance-backend-tests/FitnessScoringTests.cs) has 10 `[Fact]` tests, each pinning one rule with a hand-computed oracle (including exact-magnitude pinning at [FitnessScoringTests.cs:191-228](strava-segments-performance-backend-tests/FitnessScoringTests.cs#L191-L228), the `10000.0/109.0` weighted-aggregation test). Fixtures are inline positional `SegmentEffortRecord[]` with trailing intent comments; no shared builder exists.
- **The CI gate is not wired.** [backend-ci.yml](.github/workflows/backend-ci.yml) only builds and pushes a Docker image and redeploys — it never runs `dotnet test`. Its `paths:` trigger (`strava-segments-performance-backend/**`) also excludes the test project, so test-only changes wouldn't trigger CI even if a test step existed.
- The test project references xUnit 2.9.3, EF Core InMemory 10.0.9, `FakeTimeProvider` 10.8.0, coverlet 6.0.4, `net10.0` ([strava-segments-performance-backend-tests.csproj](strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj)).

## Desired End State

- A new synthetic realistic-data test exercises a ~4-6 segment × ~6-8 workout multi-week dataset with a designed improving-fitness signal, an embedded stall, and a null-HR effort; it asserts the workout scores are strictly ordered by the designed fitness progression, with the best workout in a high band and the worst in a low band.
- A new (deferred) real-activity test asserts the scorer's output ordering matches the user's ground-truth ranking across 2-5 frozen, anonymized real activities.
- `dotnet test` runs in CI on every backend or test-project change, and the test-plan §5 unit+integration gate is marked required.
- Cookbook §6.1 (and §6.6 note) in `test-plan.md` document how to add a scoring test.

### Key Discoveries:

- Pure scorer → unit-only is the cheapest correct layer ([FitnessScoring.cs:23](strava-segments-performance-backend/Services/FitnessScoring.cs#L23)).
- Two oracle traps to design the fixture around: the `n-1` percentile denominator makes 2-survivor segments emit only {0,100} ([FitnessScoring.cs:82-105](strava-segments-performance-backend/Services/FitnessScoring.cs#L82-L105)); global min/max normalization rescales all workouts when one is added/removed ([FitnessScoring.cs:45-53](strava-segments-performance-backend/Services/FitnessScoring.cs#L45-L53)).
- Exact-magnitude formula pinning already exists in the micro-tests, so the new realistic tests can use ordinal + coarse-band assertions without leaving magnitude regressions uncovered.
- CI gate genuinely missing — Phase 2 is real wiring work, not a confirmation ([backend-ci.yml](.github/workflows/backend-ci.yml)).
- Lessons rule: object-creation helpers must have descriptive names, never single-letter ([context/foundation/lessons.md](context/foundation/lessons.md)).

## What We're NOT Doing

- Not testing `from`/`to` window filtering or the effort→activity→user join — that is Phase 2 (`Authorization + endpoint data-path`). The scoring oracle stays decoupled from windowing by calling `Score` directly with a fixed record list.
- Not changing the scorer's behavior — this is test + CI work only.
- Not fetching real Strava data inside any test at run time (offline/deterministic only; respects the AGENTS.md rate-limit rule).
- Not adding a fixture builder abstraction — staying with inline arrays to match the existing file.
- Not adding exact-rational magnitude assertions to the new realistic tests (that pinning stays in the existing micro-tests).
- Not restructuring or re-deriving the existing 10 micro-tests.

## Implementation Approach

Two complementary realistic-data tests, split so formula coverage and the CI
gate land immediately while the real-data test waits on manually fetched data:

- **Layer A (Phase 1)** — synthetic, controlled, deterministic. A single fixture designed so the intended fitness ordering is unambiguous by construction; assertions are ordinal (strictly increasing across the designed progression) plus coarse magnitude bands. Catches gross formula regressions and shape/ordering breaks against a lifelike dataset.
- **Layer B (Phase 3, deferred)** — real, reality-grounding. 2-5 of the user's own activities frozen into an anonymized inline fixture; ordinal assertions from the user's ground-truth knowledge of their own rides.
- **CI (Phase 2)** — wire `dotnet test` and fix the path filter so the suite is actually enforced, then mark the §5 gate required.

## Phase 1: Synthetic realistic-data scoring test (Layer A)

### Overview

Add one lifelike multi-week fixture and assert the scorer reproduces the
designed fitness progression, then document the pattern in the cookbook.

### Changes Required:

#### 1. Realistic-data scoring test

**File**: `strava-segments-performance-backend-tests/FitnessScoringTests.cs`

**Intent**: Add a new `[Fact]` exercising a realistic multi-week dataset that no existing micro-test covers: a rider repeating 4-6 segments across 6-8 workouts, holding segment times roughly steady while average HR trends down over time (the "getting fitter" signal), with one mid-segment stall effort (elapsed > 2× that segment's median) and one effort with null HR mixed in as real-world noise. Assert the resulting workout scores are strictly increasing in the designed fitness order, with the earliest (least fit) workout in a low band and the latest (most fit) in a high band.

**Contract**: New method `Score_RealisticMultiWeekImprovingFitness_ProducesRisingTrend` (or similar `Score_<Condition>_<Expected>` name). Input: inline positional `SegmentEffortRecord[]` with trailing intent comments, matching the existing file style. Every workout must clear both gates by construction — ≥ 2 survivors on each shared segment and ≥ 3 scored efforts per workout ([FitnessScoring.cs:66-69](strava-segments-performance-backend/Services/FitnessScoring.cs#L66-L69), [FitnessScoring.cs:20](strava-segments-performance-backend/Services/FitnessScoring.cs#L20)). The stall effort and null-HR effort must be placed so they do not accidentally drop a workout below the 3-effort gate. Assertions: order workout scores by date and assert strict monotonic increase across the designed progression; assert the latest workout > an upper band and the earliest < a lower band. No exact-rational equality (that pinning lives in the existing micro-tests).

#### 2. Cookbook §6.1

**File**: `context/foundation/test-plan.md`

**Intent**: Replace the §6.1 `TBD` placeholder now that a reference test exists, so future contributors know how to add a scoring test.

**Contract**: §6.1 "Adding a backend unit test (scoring)" documents: location (`FitnessScoringTests.cs`), fixture style (inline `SegmentEffortRecord[]` with intent comments), the two gates a fixture must clear, the oracle discipline (expected values derived from requirements, never from running the scorer), the new test as the reference for realistic-shape coverage, and the run command (`dotnet test`).

### Success Criteria:

#### Automated Verification:

- Backend builds: `dotnet build strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`
- New test passes: `dotnet test --filter "FullyQualifiedName~FitnessScoringTests"`
- Full backend test suite passes: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`

#### Manual Verification:

- The fixture's expected ordering and bands were derived by hand from the ten scoring anchors, not read back from a scorer run.
- The improving-fitness signal, stall effort, and null-HR effort are all present and clearly commented in the fixture.
- Cookbook §6.1 reads as actionable guidance for a new contributor.

**Implementation Note**: After completing this phase and all automated verification passes, pause for manual confirmation before proceeding.

---

## Phase 2: Wire the CI gate

### Overview

Make the backend test suite run in CI and mark the test-plan gate required.

### Changes Required:

#### 1. Backend CI test step + path trigger

**File**: `.github/workflows/backend-ci.yml`

**Intent**: The workflow currently never runs the tests. Add a step (or job) that runs `dotnet test` on the backend test project before the Docker build/deploy, and extend the `paths:` triggers so changes to the test project also trigger CI. The test run must gate the pipeline — a failing test fails the workflow — while deploy steps remain guarded by `if: github.ref == 'refs/heads/master'`.

**Contract**: Add `strava-segments-performance-backend-tests/**` to both the `push` and `pull_request` `paths:` lists. Add a `dotnet test` step using an appropriate .NET setup for `net10.0` (mirror the SDK approach used elsewhere in the repo's workflows). The test step runs on both PR and push; only build/push/deploy stay master-gated. Ensure the test step runs regardless of branch so PRs are gated.

#### 2. Test-plan §5 gate status

**File**: `context/foundation/test-plan.md`

**Intent**: Flip the unit+integration gate from conditional to enforced now that CI runs it.

**Contract**: In §5, change the "unit + integration (`dotnet test`, `npm test`)" row's Required? cell from "required after §3 Phase 1" to "required", and update the closing note that says gate wiring lands with Phase 1 to reflect that it is now wired.

### Success Criteria:

#### Automated Verification:

- Workflow YAML is valid (parses; no syntax errors).
- `dotnet test` invoked in the workflow runs the backend test project green locally: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`

#### Manual Verification:

- A CI run (on a PR or push touching backend/test paths) shows the `dotnet test` step executing and gating the pipeline.
- A deliberately failing test causes the workflow to fail (spot-check reasoning or a throwaway local run) — deploy does not proceed on red.
- Test-plan §5 reflects the gate as required.

**Implementation Note**: After completing this phase and all automated verification passes, pause for manual confirmation before proceeding.

---

## Phase 3: Real-activity ordinal test (Layer B) — deferred

### Overview

Add a reality-grounding test from the user's own activities. Fully specified
now; completed once the user has fetched and transcribed the data.

### Changes Required:

#### 1. Fetch and freeze real activity data (manual, user)

**File**: n/a (data-gathering step)

**Intent**: The user fetches 2-5 of their own real Strava activities where they know the ground-truth fitness ranking (e.g. "on segment X I held the same time at clearly lower HR in ride B"), choosing activities that share several common segments so each workout clears the ≥2-survivor and ≥3-effort gates.

**Contract**: For each activity, capture per segment effort: segment id, elapsed seconds, average HR, activity id, and activity start date. Anonymize raw Strava segment/activity ids (remap to small integers). Record the user's expected relative ordering of the workouts.

#### 2. Real-activity ordinal test

**File**: `strava-segments-performance-backend-tests/FitnessScoringTests.cs`

**Intent**: Add a `[Fact]` that feeds the frozen, anonymized real-activity records into `Score` and asserts the output workout scores are ordered consistently with the user's ground-truth ranking — never fetching from Strava at run time.

**Contract**: New method `Score_RealActivities_MatchKnownFitnessOrdering` (or similar). Input: inline anonymized `SegmentEffortRecord[]` transcribed from the fetched data, with comments citing the ground-truth reasoning. Assertions: relative ordering only (`score(fitter) > score(lessFit)`); no exact magnitudes. Test is fully offline and deterministic.

#### 3. Cookbook §6.6 note

**File**: `context/foundation/test-plan.md`

**Intent**: Append a short §6.6 per-phase note capturing that a frozen real-activity ordinal test complements the synthetic one, and the freeze-not-fetch rule.

**Contract**: 2-3 line note under §6.6 referencing the real-data ordinal pattern and the offline/anonymize constraints.

### Success Criteria:

#### Automated Verification:

- New test passes: `dotnet test --filter "FullyQualifiedName~FitnessScoringTests"`
- Full backend test suite passes: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`
- No network calls in the test (test data is inline/frozen).

#### Manual Verification:

- The expected ordering encodes the user's real-world knowledge, not scorer output.
- Raw Strava ids are anonymized; no PII committed.
- Selected activities share enough common segments that every workout is scored (none silently dropped).

**Implementation Note**: This phase is deferred until the data is fetched. After completion and automated verification, pause for manual confirmation.

---

## Testing Strategy

### Unit Tests:

- Synthetic realistic multi-week fixture: strict monotonic score increase across a designed improving-fitness progression; best/worst workouts in expected bands; stall and null-HR efforts present without dropping workouts below the 3-effort gate.
- Real-activity fixture (deferred): output ordering matches the user's ground-truth ranking.

### Integration Tests:

- None. The scorer is pure; integration adds cost with no extra formula coverage. Endpoint/window/user-scoping integration is Phase 2 of the test-plan rollout (a separate change).

### Manual Testing Steps:

1. Derive the synthetic fixture's expected ordering and bands by hand from the ten anchors; confirm no expected value was read back from a scorer run.
2. Run `dotnet test` locally and confirm green.
3. After CI wiring, open a PR touching a backend/test path and confirm the `dotnet test` step runs and gates.

## Performance Considerations

None. Unit tests over a small in-memory fixture; no performance budget.

## Migration Notes

None.

## References

- Related research: `context/changes/testing-scoring-coverage/research.md`
- Test plan: `context/foundation/test-plan.md` (§2 Risk #1, §3 Phase 1, §5 gate, §6.1 cookbook)
- Reference test (exact-magnitude oracle pattern): `strava-segments-performance-backend-tests/FitnessScoringTests.cs:191-228`
- Scorer: `strava-segments-performance-backend/Services/FitnessScoring.cs:23-105`
- CI to wire: `.github/workflows/backend-ci.yml`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Synthetic realistic-data scoring test (Layer A)

#### Automated

- [x] 1.1 Backend test project builds — d8d3e4b
- [x] 1.2 New test passes (`--filter FitnessScoringTests`) — d8d3e4b
- [x] 1.3 Full backend test suite passes — d8d3e4b

#### Manual

- [x] 1.4 Expected ordering/bands derived by hand, not from scorer output — d8d3e4b
- [x] 1.5 Improving-fitness signal, stall, and null-HR effort all present and commented — d8d3e4b
- [x] 1.6 Cookbook §6.1 is actionable — d8d3e4b

### Phase 2: Wire the CI gate

#### Automated

- [x] 2.1 Workflow YAML valid
- [x] 2.2 `dotnet test` runs the backend test project green locally

#### Manual

- [ ] 2.3 CI run shows the `dotnet test` step executing and gating
- [ ] 2.4 A failing test fails the workflow; deploy does not proceed on red
- [ ] 2.5 Test-plan §5 gate marked required

### Phase 3: Real-activity ordinal test (Layer B) — deferred

#### Automated

- [x] 3.1 New test passes (`--filter FitnessScoringTests`)
- [x] 3.2 Full backend test suite passes
- [x] 3.3 No network calls in the test

#### Manual

- [ ] 3.4 Expected ordering encodes real-world knowledge, not scorer output
- [ ] 3.5 Raw Strava ids anonymized; no PII committed
- [ ] 3.6 Selected activities share enough segments that no workout is silently dropped
