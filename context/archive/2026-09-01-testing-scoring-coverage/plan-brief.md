# Scoring Realistic-Data Coverage (Risk #1) — Plan Brief

> Full plan: `context/changes/testing-scoring-coverage/plan.md`
> Research: `context/changes/testing-scoring-coverage/research.md`

## What & Why

Phase 1 of the test-plan rollout targets Risk #1: a scoring change that silently
produces a wrong fitness trend on real-world data. The scorer has ten per-rule
micro-tests but no lifelike multi-week fixture — the exact kind of test most
likely to catch a subtle formula regression that still passes every micro-test.
This change adds that coverage and wires the CI gate that makes the backend
suite enforceable (it currently isn't).

## Starting Point

`FitnessScoring.Score` is a pure, deterministic, dependency-free static function
with ten inline-fixture `[Fact]` tests, each pinning one rule with a hand-computed
oracle (including exact-magnitude pinning at `FitnessScoringTests.cs:191-228`).
Critically, `backend-ci.yml` never runs `dotnet test` — it only builds and
deploys — and its path filter excludes the test project entirely.

## Desired End State

A synthetic multi-week test proves the scorer reproduces a designed
improving-fitness progression; a deferred real-activity test proves the scorer's
ordering matches the user's ground-truth ranking of their own rides; and
`dotnet test` runs and gates on every backend/test change, with the test-plan §5
gate marked required.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Test layer | Unit only | Scorer is pure/deterministic; integration adds cost with no extra formula signal | Research |
| Data provenance | Two layers: synthetic + frozen real | Synthetic pins shape; real 2-5 activities pin reality; neither is tautological | Research |
| Layer B timing | Plan now, split into a deferred sub-phase | Formula coverage + CI land immediately without waiting on manual data fetch | Plan |
| Fixture format | Inline `SegmentEffortRecord[]` with comments | Matches all existing tests; easiest to hand-verify; no new abstraction | Plan |
| Assertion style | Ordinal + coarse magnitude bands | Exact-magnitude pinning already lives in the micro-tests; realistic tests prove shape/ordering | Plan |
| CI gate | Verify then wire (it's missing) | `backend-ci.yml` runs no tests today, so the §5 gate must actually be added | Plan |

## Scope

**In scope:**
- Synthetic realistic multi-week scoring test (improving signal + stall + null-HR effort)
- CI: `dotnet test` step + path-filter fix + §5 gate flip to required
- Deferred real-activity ordinal test (fully specified)
- Cookbook §6.1 and §6.6 updates

**Out of scope:**
- `from`/`to` window filtering and effort→activity→user scoping (test-plan Phase 2)
- Any change to scorer behavior
- Live Strava calls in tests; fixture builder abstraction; exact-rational assertions on the new tests

## Architecture / Approach

Two complementary tests against the pure `Score` function: Layer A (synthetic,
controlled, immediate) designed so the fitness ordering is unambiguous by
construction and asserted ordinally with coarse bands; Layer B (real, frozen,
anonymized, deferred) asserted purely on ordering from the user's ground truth.
CI wiring adds a gating `dotnet test` step and extends the trigger paths.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Synthetic test + §6.1 | Lifelike fixture proving a rising trend | Designing a fixture that clears both gates and has an unambiguous oracle |
| 2. CI gate | `dotnet test` runs and gates; §5 required | .NET 10 setup in the workflow; path-filter correctness |
| 3. Real-activity test (deferred) | Reality-grounded ordinal test | Chosen activities must share enough segments to be scored |

**Prerequisites:** Phase 3 needs the user to fetch and anonymize 2-5 real activities.
**Estimated effort:** ~1-2 sessions for Phases 1-2; Phase 3 when data is ready.

## Open Risks & Assumptions

- The synthetic fixture's oracle must be hand-derived; reading expected values back from the scorer would make the test tautological.
- The `n-1` percentile denominator and global min/max normalization must be accounted for when sizing the fixture, or bands/ordering can shift unexpectedly.
- CI assumes a .NET 10 SDK setup is available/added in the workflow runner.

## Success Criteria (Summary)

- A realistic multi-week dataset yields a strictly rising fitness trend the user would recognize as "getting fitter."
- Real activities the user knows the ranking of come out ordered correctly by the scorer.
- Backend tests run and gate in CI; a red test blocks deploy.
