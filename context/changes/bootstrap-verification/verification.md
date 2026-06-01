---
bootstrapped_at: 2026-06-01T20:15:00Z
starter_id: angular
starter_name: Angular
project_name: strava-segments-performance
language_family: js
package_manager: npm
cwd_strategy: subdir-then-move
bootstrapper_confidence: verified
phase_3_status: ok
audit_command: "npm audit --json"
---

## Hand-off

```yaml
starter_id: angular
package_manager: npm
project_name: strava-segments-performance
hints:
  language_family: js
  team_size: solo
  deployment_target: dockerhub
  ci_provider: github-actions
  ci_default_flow: auto-deploy-on-merge
  bootstrapper_confidence: verified
  path_taken: custom
  quality_override: false
  self_check_answers:
    typed: true
    from_official_starter: true
    conventions: true
    docs_current: true
    can_judge_agent: true
  has_auth: true
  has_payments: false
  has_realtime: false
  has_ai: false
  has_background_jobs: false
```

### Why this stack

Angular + TypeScript as a standalone SPA frontend for the Strava Segments Performance project. Angular's CLI-driven scaffold provides a typed, convention-heavy structure (components, services, modules) that passes all four agent-friendly quality gates. The strict TypeScript typing and opinionated project layout give AI agents reliable patterns to follow. Deployed independently via Docker Hub as a containerized static app served by nginx, communicating with the .NET backend API over HTTP.

## Pre-scaffold verification

| Signal      | Value                                          | Severity | Notes                                |
| ----------- | ---------------------------------------------- | -------- | ------------------------------------ |
| npm package | @angular/cli v21.2.13 published 2026-06-01     | fresh    | resolved from cmd_template           |
| GitHub repo | not run                                        | n/a      | docs_url is angular.dev, not a GitHub URL |

## Scaffold log

**Resolved invocation**: `npx @angular/cli new bootstrap-scaffold --defaults --routing --style scss --skip-tests --ssr false`

**Note**: Angular CLI rejects project names starting with `.`; `bootstrap-scaffold` was used as the temp directory name instead of `.bootstrap-scaffold`. Files were subsequently moved by user into `strava-segments-performance/` (the intended project folder). Project name references inside `angular.json`, `package.json`, `package-lock.json`, and `src/app/app.ts` were updated from `bootstrap-scaffold` to `strava-segments-performance` post-move.

**Strategy**: scaffold into a temp directory then move files up

**Exit code**: 0

**Files moved**: 15 root items (.editorconfig, .git, .gitignore, .prettierrc, .vscode/, angular.json, node_modules/, package-lock.json, package.json, public/, README.md, src/, tsconfig.app.json, tsconfig.json, tsconfig.spec.json)

**Final location**: `strava-segments-performance/`

**Conflicts (.scaffold siblings)**: none

**.gitignore handling**: moved silently (no pre-existing .gitignore in destination)

**bootstrap-scaffold cleanup**: deleted

## Post-scaffold audit

**Tool**: `npm audit --json`

**Summary**: 0 CRITICAL, 0 HIGH, 0 MODERATE, 0 LOW

**Direct vs transitive**: 0/0/0/0 direct of total 0/0/0/0

Clean tree — 513 total dependencies (10 prod, 504 dev, 127 optional). No advisories.

## Hints recorded but not acted on

| Hint                    | Value                |
| ----------------------- | -------------------- |
| bootstrapper_confidence | verified             |
| quality_override        | false                |
| path_taken              | custom               |
| self_check_answers      | all true             |
| team_size               | solo                 |
| deployment_target       | dockerhub            |
| ci_provider             | github-actions       |
| ci_default_flow         | auto-deploy-on-merge |
| has_auth                | true                 |
| has_payments            | false                |
| has_realtime            | false                |
| has_ai                  | false                |
| has_background_jobs     | false                |

## Next steps

Next: a future skill will set up agent context (CLAUDE.md, AGENTS.md). For now, your project is scaffolded and verified — happy hacking.

Useful manual steps in the meantime:
- `cd strava-segments-performance && npm start` to verify the dev server runs.
- `git init` inside `strava-segments-performance/` if you want your own clean git history (the folder already has one from Angular CLI's auto-init — reset it if you prefer a fresh start).
- Address audit findings per your project's risk tolerance — clean tree, nothing to address.
