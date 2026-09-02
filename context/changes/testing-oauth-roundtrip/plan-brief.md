# OAuth Handshake Round-Trip Tests (Risk #2) — Plan Brief

> Full plan: `context/changes/testing-oauth-roundtrip/plan.md`
> Research: `context/changes/testing-oauth-roundtrip/research.md`

## What & Why

Prove the Strava OAuth handshake round-trip completes end-to-end — the failure class behind the real prod incident (Risk #2 in `test-plan.md`: wrong `redirect_uri` behind the proxy, unhandled callback, wrong post-callback endpoint, cookie divergence). We build a browser e2e that completes a **full login** against an in-process **stub Strava** (never the real one), gate it in CI, and back it with a scoped backend integration test for the prod-only cases the browser can't reach.

## Starting Point

All OAuth wiring is in one file (`Program.cs`): challenge `/auth/login`, `CallbackPath /auth/callback`, `OnCreatingTicket` upsert, success→`/dashboard`, failure→`/login?error=auth_failed`, `/api/*`→401, per-env cookies, `UseForwardedHeaders`. Playwright *source* scaffolding and an `E2E`-gated `/auth/test-login` session seam already exist on this branch, but `@playwright/test` isn't installed, there's no e2e CI job, and the backend test project is unit-only (no `WebApplicationFactory`).

## Desired End State

An unauthenticated browser clicks "Connect with Strava" and lands authenticated on `/dashboard` — the whole chain, no real Strava — and this runs green on every PR via a new e2e CI job. A fast backend `dotnet test` additionally proves the `redirect_uri` is built as `https://…/auth/callback` from forwarded headers (the actual incident), the production cookie branch, the failure redirect, and the 401 branch.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Full-login mechanism | In-process `E2E`-gated stub authorize/token/athlete + repoint handler | Lets a real browser complete the whole chain without Strava | Plan |
| Browser vs integration split | Browser owns happy path; integration owns the delta | Avoids redundancy; each layer tests what only it can reach | Plan |
| Integration test DB | Testcontainers Postgres | Startup `MigrateAsync` is relational-only, so InMemory can't back the host | Research (R2) |
| Cookie assertions | Both Dev and Production branches | The dev/prod cookie divergence is a named instance of Risk #2 | Plan |
| Cookie policy under `E2E` | Treat `E2E` as dev-like (`Lax`/`SameAsRequest`) | Otherwise `Secure=Always` drops the cookie over http-localhost and breaks the chain | Plan |
| e2e CI | Full PR-gated job now (Postgres+backend+frontend+Playwright) | Fulfills the test-plan §5 e2e gate | Plan |
| Deploy-smoke | SHA-gated post-deploy probe, fail-loud (Phase 5) | Catches the prod-only forwarded-proto/redirect_uri regression no offline test can see | Plan |
| Must-pass floor | The browser chain | User decision — the composed login is the priority proof | Plan |

## Scope

**In scope:** in-process E2E stub Strava; `E2E` cookie-policy fix; committed `appsettings.E2E.json`; Playwright install/pin + backend `webServer` + unauthenticated project + handshake spec; e2e CI job; scoped `WebApplicationFactory` integration deltas + `TokenEncryptionService` unit test; SHA-gated post-deploy redirect smoke.

**Out of scope:** real Strava; OAuth/ASP.NET library internals; happy-path re-assertion in the integration layer; auto-rollback on smoke failure; the test-plan §3 Phase 5 chart e2e.

## Architecture / Approach

Two complementary layers, browser-first. **Browser layer** (Phases 1–3): the backend hosts stub authorize/token/athlete endpoints under `ASPNETCORE_ENVIRONMENT=E2E` and repoints the Strava handler at them via `PostConfigure`; Playwright drives a logged-out browser through the real framework challenge/callback logic against the stub, landing on `/dashboard`; a CI job runs it against a Postgres service. **Integration layer** (Phase 4): `WebApplicationFactory<Program>` over Testcontainers Postgres with a backchannel-stubbed token/athlete asserts only the forwarded-header `redirect_uri`, the cookie matrix, the failure redirect, and the 401 branch — running in the existing backend `dotnet test` gate.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Backend E2E stub OAuth server | Full login possible without Strava; `E2E` cookie fix; `appsettings.E2E.json` | Cookie/`Secure` over http-localhost silently breaks the session |
| 2. Playwright + handshake spec (floor) | Logged-out browser → full chain → `/dashboard`, runnable locally | Unauthenticated project must not inherit the `setup` session |
| 3. e2e CI/CD job | PR-gated Postgres+backend(`E2E`)+frontend+Playwright, report on failure | Boot/timing flake of the full stack in CI |
| 4. Integration round-trip (delta) | Forwarded-header `redirect_uri`, cookie matrix, failure, 401, token-encryption unit | Testcontainers needs Docker on the runner |
| 5. Deploy-time redirect smoke | SHA-gated post-deploy probe asserting the live `https://…/auth/callback` redirect_uri | Detector not gate — deploy already happened; must probe the new revision, not a stale pod |

**Prerequisites:** local Postgres (or Docker) for the `E2E`/integration DB; Docker available on the CI runner (it is); a reachable public frontend origin for the smoke.
**Estimated effort:** ~3–4 sessions across the five phases.

## Open Risks & Assumptions

- **The deploy-smoke is a fail-loud *detector*, not a promotion gate** — Railway has already deployed by the time it runs, so a regression is caught and alerted but not auto-reverted (rollback is manual). Making it a true gate would require probing a preview/staging environment before promotion (noted as the better-if-available variant).
- **The smoke must probe the *new* revision** — hence the `BUILD_SHA` marker on `/health` and the poll-until-SHA-matches step; without it a green smoke could be a stale pod and mask the very regression it guards.
- The in-process stub assumes the backend can call its own `E2E` token/athlete endpoints (Kestrel self-call) — standard, but confirm in Phase 1.
- E2E cookie same-site reasoning relies on `localhost:4200` and `:5000` being same-*site* (true — port is not part of "site").

## Success Criteria (Summary)

- A logged-out browser completes the full Strava-less login and lands authenticated on `/dashboard`, locally and in CI.
- `dotnet test` proves the forwarded-header `redirect_uri`, both cookie branches, the failure redirect, and the 401 branch.
- A post-deploy smoke asserts the live prod `GET /auth/login` returns an `https://<prod-host>/auth/callback` redirect_uri, gated on the deployed SHA.
- No `/e2e-stub/*` route or `/auth/test-login` seam is reachable outside the `E2E` environment.
