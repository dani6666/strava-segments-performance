# Lessons Learned

> Append-only register of recurring rules and patterns. Re-read at start by /10x-frame, /10x-research, /10x-plan, /10x-plan-review, /10x-implement, /10x-impl-review.

## Always assume master is the main branch

- **Context**: All phases — any skill that interacts with the git repository (branching, PRs, diffs, commits, CI)
- **Problem**: Skills guess the wrong base branch; PRs target the wrong ref, diffs are computed against non-existent branches, or CI runs against an unexpected base
- **Rule**: Always assume `master` is the main branch; never assume `main`, `develop`, or `trunk` unless the repo's HEAD or git config explicitly says otherwise.
- **Applies to**: all
