---
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
---

## Why this stack

Two-project architecture: .NET (latest, C#) as a backend REST API and Angular + TypeScript as a separate SPA frontend — .NET handles API and business logic only, not rendering. Modern .NET with minimal APIs passes all four agent-friendly quality gates (typed, convention-based, popular in training data, well-documented) and delivers built-in dependency injection, hosted services for background Strava data fetching, and battle-tested OAuth middleware. Angular provides a typed, convention-heavy frontend with CLI-driven scaffolding that mirrors the backend's structure discipline. Together they form a clean API + SPA split independently deployable via Docker Hub.
