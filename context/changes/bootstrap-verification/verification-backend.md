---
bootstrapped_at: 2026-06-01T20:00:32Z
starter_id: dotnet
starter_name: ".NET (ASP.NET Core webapi)"
project_name: strava-segments-performance-backend
language_family: dotnet
package_manager: dotnet
cwd_strategy: subdir-then-move
bootstrapper_confidence: verified
phase_3_status: ok
audit_command: "dotnet list package --vulnerable --include-transitive"
---

## Hand-off

```yaml
starter_id: dotnet
package_manager: dotnet
project_name: strava-segments-performance-backend
hints:
  language_family: dotnet
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
  has_background_jobs: true
```

Two-project architecture: .NET (latest, C#) as a backend REST API and Angular + TypeScript as a separate SPA frontend — .NET handles API and business logic only, not rendering. Modern .NET with minimal APIs passes all four agent-friendly quality gates (typed, convention-based, popular in training data, well-documented) and delivers built-in dependency injection, hosted services for background Strava data fetching, and battle-tested OAuth middleware. Angular provides a typed, convention-heavy frontend with CLI-driven scaffolding that mirrors the backend's structure discipline. Together they form a clean API + SPA split independently deployable via Docker Hub.

## Pre-scaffold verification

| Signal      | Value    | Severity | Notes                                                                                   |
| ----------- | -------- | -------- | --------------------------------------------------------------------------------------- |
| npm package | not run  | n/a      | non-JS starter; no npm package derived from cmd_template                                |
| GitHub repo | not run  | n/a      | docs_url (https://learn.microsoft.com/aspnet/core) is not a GitHub URL; no recency signal available |

## Scaffold log

**Resolved invocation**: `dotnet new webapi -n strava-segments-performance-backend -o .bootstrap-scaffold --no-restore`
**Strategy**: subdir-then-move (default; dotnet not listed in bootstrapper-config.yaml)
**Adaptation note**: `-o .bootstrap-scaffold` added to cmd_template substitution — `dotnet new` without `-o` writes directly to cwd, defeating the temp-dir strategy. The project name (`strava-segments-performance-backend`) was used for `-n` (namespace) and `.bootstrap-scaffold` for `-o` (output dir).
**Exit code**: 0
**Files moved**: 6
**Conflicts (.scaffold siblings)**: none
**.gitignore handling**: absent in scaffold
**.bootstrap-scaffold cleanup**: deleted

Files located at `strava-segments-performance-backend/` (manually moved by user after scaffold):
- `strava-segments-performance-backend/appsettings.Development.json`
- `strava-segments-performance-backend/appsettings.json`
- `strava-segments-performance-backend/Program.cs`
- `strava-segments-performance-backend/strava-segments-performance-backend.csproj`
- `strava-segments-performance-backend/strava-segments-performance-backend.http`
- `strava-segments-performance-backend/Properties/launchSettings.json`

## Post-scaffold audit

**Tool**: `dotnet list package --vulnerable --include-transitive`
**Summary**: 0 CRITICAL, 0 HIGH, 0 MODERATE, 0 LOW
**Direct vs transitive**: not distinguished by this tool
**Raw output**:

```
The given project `strava-segments-performance-backend` has no vulnerable packages given the current sources.
```

#### CRITICAL findings

None.

#### HIGH findings

None.

#### MODERATE findings

None.

#### LOW / INFO findings

None.

## Hints recorded but not acted on

| Hint                    | Value                  |
| ----------------------- | ---------------------- |
| bootstrapper_confidence | verified               |
| quality_override        | false                  |
| path_taken              | custom                 |
| self_check_answers      | typed: true, from_official_starter: true, conventions: true, docs_current: true, can_judge_agent: true |
| team_size               | solo                   |
| deployment_target       | dockerhub              |
| ci_provider             | github-actions         |
| ci_default_flow         | auto-deploy-on-merge   |
| has_auth                | true                   |
| has_payments            | false                  |
| has_realtime            | false                  |
| has_ai                  | false                  |
| has_background_jobs     | true                   |

## Next steps

Next: a future skill will set up agent context (CLAUDE.md, AGENTS.md). For now, your project is scaffolded and verified — happy hacking.

Useful manual steps in the meantime:
- `git init` (if you have not already) to start your own repo history.
- Review any `.scaffold` siblings the conflict policy created and decide which version of each file to keep (none created in this run — zero conflicts).
- Address audit findings per your project's risk tolerance — the full breakdown is in this log (0 findings; clean tree).
