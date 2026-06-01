---
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
---

## Why this stack

Angular + TypeScript as a standalone SPA frontend for the Strava Segments Performance project. Angular's CLI-driven scaffold provides a typed, convention-heavy structure (components, services, modules) that passes all four agent-friendly quality gates. The strict TypeScript typing and opinionated project layout give AI agents reliable patterns to follow. Deployed independently via Docker Hub as a containerized static app served by nginx, communicating with the .NET backend API over HTTP.
