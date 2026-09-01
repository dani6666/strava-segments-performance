<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Scoring Realistic-Data Coverage (Risk #1)

- **Plan**: context/changes/testing-scoring-coverage/plan.md
- **Scope**: Phases 1–3 of 3 (full plan)
- **Date**: 2026-09-01
- **Verdict**: APPROVED
- **Findings**: 0 critical, 0 warnings, 1 observation

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | PASS |
| Architecture | PASS |
| Pattern Consistency | PASS |
| Success Criteria | PASS |

## Evidence

- Build + full suite green: `dotnet test` → Passed! Failed: 0, Passed: 32, Skipped: 0 (net10.0).
- Phase 1 oracle re-derived by hand: constant elapsed per segment + identical 5 bpm/week HR drop makes every segment rank the weeks identically, so each workout's weighted-average percentile equals that week's rank independent of segment weights → raw scores 0/20/40/60/80/100; global normalization is identity here. Strictly rising; week1 = 0 (< 20), week6 = 100 (> 80). Not read back from the scorer.
- Stall row (201 @ 900s, week3): segment 201 median = 300, 2× = 600, 900 dropped; week3 keeps its 4 core efforts (≥ 3 gate held).
- Null-HR row (202 @ 175, null, week4): filtered before grouping; week4 keeps its 4 core efforts.
- Phase 3 real-activity test is fully offline/inline; ids anonymized to small integers (201–306 activities/segments, 401–403 unrelated); unrelated ride asserted absent (segments 401–403 each have < 2 survivors).
- Scope: `FitnessScoring.cs` unchanged in this change's commits (`git diff d8d3e4b^..HEAD`), confirming test/CI/docs-only.
- CI: `build-and-deploy` gated by `needs: test`; deploy steps stay `if: github.ref == 'refs/heads/master'`; test job runs on PR and push. Test-plan §5 gate row now reads `required`; §6.1 cookbook and §6.6 note present.

## Findings

### F1 — CI pull_request trigger omits the workflow file itself

- **Severity**: 🔵 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: .github/workflows/backend-ci.yml:10-14
- **Detail**: The `push` `paths:` list includes `.github/workflows/backend-ci.yml`, but the `pull_request` `paths:` list does not. A PR that edits only the workflow file (without touching backend or test paths) will not trigger this CI, so a change to the gate itself can't be validated by the gate on its PR. This asymmetry pre-existed the change; the plan's contract only required adding the tests path to both lists (done correctly). Benign for the delivered scope.
- **Fix**: Add `- ".github/workflows/backend-ci.yml"` to the `pull_request` `paths:` list to mirror `push`.
- **Decision**: FIXED
