# OAuth Handshake Round-Trip Tests (Risk #2) Implementation Plan

## Overview

Prove the Strava OAuth handshake round-trip completes end-to-end — the failure class behind the real prod incident (Risk #2 in `context/foundation/test-plan.md`). The centerpiece is a **browser e2e that completes a full login** (click → challenge → callback → cookie → `/dashboard`) against an **in-process, `E2E`-gated stub Strava** — never the real Strava. A **full e2e CI job** runs it on every PR. A **scoped backend integration round-trip** covers the prod-only cases the browser test structurally cannot reach on localhost-http: the `redirect_uri` built from forwarded headers (the actual incident), the production cookie branch, the failure redirect, and the `/api/*`→401 branch.

## Current State Analysis

All OAuth wiring lives in one file — `strava-segments-performance-backend/Program.cs` (`AspNet.Security.OAuth.Strava` v10 over cookie auth, BFF pattern). Every seam Risk #2 names is present and located (full detail in `context/changes/testing-oauth-roundtrip/research.md`):

- Challenge `GET /auth/login` (`Program.cs:140-146`); `CallbackPath = "/auth/callback"` (`:60`); scope `activity:read_all` (`:58`).
- `redirect_uri` composed from request scheme/host, rewritten by `UseForwardedHeaders()` (`:107-116`) — the prod-critical path.
- `OnCreatingTicket` upsert + AES token encryption (`:62-87`); success → `{frontendOrigin}/dashboard` (`:142`), failure → `{frontendOrigin}/login?error=auth_failed` (`:89-94`).
- `/api/*` → 401 (not 302) via `OnRedirectToLogin` (`:43-52`); cookie SameSite/Secure branch on `IsDevelopment()` (`:35-41`).
- E2E-only session seam `GET /auth/test-login` already exists (`:166-194`) — for tests that need a session but do *not* test login; **the handshake test must not use it**.

E2E scaffolding is **half-built** on this branch: `playwright.config.ts`, `e2e/auth.setup.ts`, `e2e/fixtures.ts`, `e2e/seed.spec.ts` exist, but `@playwright/test` is **not installed/pinned**, there is no `test:e2e` script, the backend `webServer` block is commented out, and there is **no e2e CI job**. The backend test project (`strava-segments-performance-backend-tests/`) is xUnit unit-only — **no `Microsoft.AspNetCore.Mvc.Testing`**, no `WebApplicationFactory`.

Key constraints discovered:
- Startup runs `db.Database.MigrateAsync()` + `ExecuteUpdateAsync` (`Program.cs:118-126`) — **relational-only**. `EntityFrameworkCore.InMemory` cannot back a `WebApplicationFactory<Program>`; the integration layer needs a real Postgres.
- The Strava authorize/token/userinfo endpoints are **hardcoded package defaults**, not config-bound — repointing them at a stub requires code (`PostConfigure<StravaAuthenticationOptions>`), which does not exist yet.
- `Program` uses top-level statements with **no `public partial class Program`** → not reachable by `WebApplicationFactory<Program>` as-is.

## Desired End State

- `npm run test:e2e` (frontend) passes locally and in CI: an **unauthenticated** browser clicks "Connect with Strava" and lands **authenticated** on `/dashboard`, with `/api/auth/me` returning the stub athlete — the whole chain, no real Strava.
- A PR-gated **e2e CI job** stands up Postgres + backend (`E2E`) + frontend + Playwright and runs the browser suite green, uploading the Playwright report on failure.
- `dotnet test` (backend, in the existing CI gate) passes an integration round-trip asserting: `redirect_uri` = `https://<forwarded-host>/auth/callback` from injected forwarded headers; the **Production** cookie branch (`SameSite=None`/`Secure=Always`) and the **Development** branch (`Lax`/`SameAsRequest`); a forced token-exchange failure → 302 `…/login?error=auth_failed`; unauthenticated `/api/auth/me` → 401; and `TokenEncryptionService` encrypt/decrypt round-trip + missing-key throw.
- After a production deploy, a **fail-loud smoke** confirms the live public `GET /auth/login` 302's with an `https://<prod-host>/auth/callback` `redirect_uri` — the prod-only forwarded-proto/host wiring no offline test can see — gated on the just-deployed build SHA so it can't pass against a stale revision.
- Verify: run `npm run test:e2e` and `dotnet test` locally; open a PR and see both the existing gates and the new e2e job pass; on a master deploy, see the smoke step poll for the new SHA and assert the redirect_uri.

### Key Discoveries:

- Cookie policy branches on `IsDevelopment()` (`Program.cs:35-41`); under `E2E` it resolves to `Secure=Always`, which drops the cookie over plain-http localhost → the browser would land on `/dashboard` then bounce to `/login`. **Phase 1 must treat `E2E` as dev-like for the cookie policy.** (`localhost:4200` and `:5000` are same-*site*, so `Lax` is fine for the XHR to `/api/auth/me`.)
- The handshake browser spec must run **unauthenticated** — it cannot reuse the `setup` project's `storageState` (`playwright.config.ts:26-27`). It needs its own no-`storageState` project.
- The in-process stub authorize endpoint is same-origin with `/auth/callback` (both on the backend origin), so the OAuth **correlation cookie survives** the round-trip without special handling.
- Research `research.md` §R1–R7 already resolved the harness seam, DB choice, stub mechanics, test key, Playwright pin, and deploy topology — this plan executes those decisions.

## What We're NOT Doing

- **No real Strava**, ever (rate limits, CAPTCHA, third-party UI — AGENTS.md). All provider interaction is stubbed.
- **No OAuth/ASP.NET middleware internals** (`test-plan.md` §7 — the library's responsibility).
- **No happy-path re-assertion in the integration layer** — the browser e2e owns the success path; Phase 4 asserts only the delta the browser can't reach.
- **No auto-rollback on smoke failure** — Phase 5's deploy-smoke is a fail-loud *detector*, not a promotion gate (Railway has already deployed by the time it runs); rollback stays manual. See Open Risks.
- **Not** the test-plan §3 **Phase 5** chart vertical-slice e2e (a separate change; it reuses this Playwright runner). Note: this plan's own "Phase 5" is the deploy-smoke, not that chart slice.

## Implementation Approach

Two complementary layers, browser-first (the browser chain is the must-pass floor):

1. **Browser layer** (Phases 1–3): make a genuine full login possible without Strava by hosting a stub authorize/token/athlete inside the backend under the `E2E` environment and repointing the Strava handler at it; drive it with Playwright; gate it in CI. This proves the composed chain through Angular, real Kestrel, and real Postgres.
2. **Integration layer** (Phase 4): a `WebApplicationFactory<Program>` over a Testcontainers Postgres with a backchannel-stubbed token/athlete, asserting only the prod-only / negative cases the browser layer cannot reach on localhost-http.

## Critical Implementation Details

- **Cookie policy under `E2E`.** The `IsDevelopment()` cookie branch (`Program.cs:35-41`) must be widened so `E2E` gets the dev-like settings (`SameSite=Lax`, `SecurePolicy=SameAsRequest`). Without this the auth cookie is `Secure` and never set over http-localhost, silently breaking the whole browser chain. The Phase 4 cookie-matrix test asserts the **Development** and **Production** branches (not `E2E`), so this widening does not weaken that assertion.
- **Stub reachability / ordering.** The `PostConfigure` that repoints `AuthorizationEndpoint`/`TokenEndpoint`/`UserInformationEndpoint` and the stub endpoint mappings must both be gated to `E2E` and must not touch Development/Production wiring. The token + athlete endpoints are called server-side (backchannel) by Kestrel to itself — that is fine; the authorize endpoint is the only one the browser hits directly.
- **Unauthenticated project isolation.** Adding a no-`storageState` Playwright project means the handshake spec starts logged out while the existing `chromium` project stays authenticated via `setup`. Keep the two projects' `testMatch` disjoint so the handshake spec never inherits the injected session.

---

## Phase 1: Backend E2E stub OAuth server

### Overview

Make a real full login possible in the `E2E` environment without contacting Strava: host stub authorize/token/athlete endpoints in-process, repoint the Strava handler at them, fix the cookie policy for `E2E`, and add committed test-only `E2E` config. After this phase a human can click through a full login in a browser against a locally-run `E2E` backend.

### Changes Required:

#### 1. E2E-gated stub OAuth endpoints

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Under `app.Environment.IsEnvironment("E2E")` (alongside the existing `/auth/test-login` block at `:166-194`), map three stub endpoints that impersonate Strava: an **authorize** endpoint that 302s the browser straight back to the OAuth `redirect_uri` with a canned `code` and the passed-through `state`; a **token** endpoint returning a canned `access_token`/`refresh_token`/`expires_in`; and an **athlete** endpoint returning a canned athlete (stable id + first/last name) shaped like Strava's `/api/v3/athlete`.

**Contract**: Routes e.g. `GET /e2e-stub/oauth/authorize` (reads `redirect_uri`, `state`; 302 → `{redirect_uri}?code=e2e-code&state={state}`), `POST /e2e-stub/oauth/token` (JSON token response with the field names `AspNet.Security.OAuth.Strava` expects), `GET /e2e-stub/api/athlete` (athlete JSON incl. the id claim source). Canned athlete id/name are constants the browser spec will assert against. Happy-path only (failure is covered in Phase 4).

#### 2. Repoint the Strava handler at the stub in E2E

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Only in `E2E`, override the three Strava OAuth endpoints to the in-process stub URLs so the framework's own handler drives the real challenge/callback/backchannel logic against the stub. Everything else about the handler (CallbackPath, scope, `OnCreatingTicket`, `OnRemoteFailure`) stays identical to production.

**Contract**: `services.PostConfigure<StravaAuthenticationOptions>(StravaAuthenticationDefaults.AuthenticationScheme, o => { o.AuthorizationEndpoint = …/e2e-stub/oauth/authorize; o.TokenEndpoint = …/e2e-stub/oauth/token; o.UserInformationEndpoint = …/e2e-stub/api/athlete; })`, registered only when the environment is `E2E`. The base URL is the backend's own origin (from config/`ASPNETCORE_URLS`, default `http://localhost:5000`).

#### 3. Widen the cookie policy to treat E2E as dev-like

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Change the two cookie branches (`Program.cs:36-41`) so the dev-like values (`SameSite=Lax`, `SecurePolicy=SameAsRequest`) apply when the environment is Development **or** `E2E`; production behavior is unchanged.

**Contract**: Replace `builder.Environment.IsDevelopment()` in both cookie predicates with `builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("E2E")`.

#### 4. Committed E2E configuration

**File**: `strava-segments-performance-backend/appsettings.E2E.json` (new)

**Intent**: Provide test-only, non-secret config so `dotnet run` / CI can launch the backend in `E2E` deterministically: a fixed base64 `TokenEncryption:Key` (32 bytes), placeholder `Strava:ClientId`/`ClientSecret` (unused against the stub but required so options bind), and `Frontend:Origin = http://localhost:4200`. The Postgres connection string is supplied via environment (`ConnectionStrings__DefaultConnection`) so local and CI can differ.

**Contract**: New JSON file committed to the repo (values are throwaway/test-only, safe to commit). Confirm `appsettings.E2E.json` is copied to output / included in the build so `dotnet run --environment E2E` and the published image both pick it up.

### Success Criteria:

#### Automated Verification:

- Backend builds: `dotnet build strava-segments-performance-backend/strava-segments-performance-backend.csproj`
- Backend unit tests still pass: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`
- The stub endpoints and `PostConfigure` are absent outside `E2E` (grep confirms they are inside the `IsEnvironment("E2E")` guard)

#### Manual Verification:

- Run a local Postgres for E2E, then `ASPNETCORE_ENVIRONMENT=E2E dotnet run` (with `ConnectionStrings__DefaultConnection` set) and `npm start` for the frontend; clicking "Connect with Strava" completes the full chain and lands authenticated on `/dashboard`, and `/api/auth/me` returns the canned athlete
- Reloading `/dashboard` stays authenticated (the auth cookie was actually set — proves the E2E cookie-policy fix)
- Running the backend in Development/Production does **not** expose any `/e2e-stub/*` route

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual login-through-the-browser test was successful before proceeding to the next phase.

---

## Phase 2: Playwright runner + browser OAuth handshake spec (the floor)

### Overview

Install and pin the Playwright runner, wire the backend `E2E` `webServer` and an unauthenticated project into the config, and write the handshake spec that drives the full login from a logged-out browser to `/dashboard`. This is the must-pass floor for Risk #2.

### Changes Required:

#### 1. Install and script the runner

**File**: `strava-segments-performance/package.json`

**Intent**: Add `@playwright/test` (pinned) as a devDependency and a `test:e2e` script; document the one-time `npx playwright install` browser step.

**Contract**: `devDependencies["@playwright/test"] = "^1.61.0"` (current release, Context7-confirmed 2026-09-02); `scripts.test:e2e = "playwright test"`. No change to the existing `test` (Vitest) script.

#### 2. Wire the backend webServer + unauthenticated project

**File**: `strava-segments-performance/playwright.config.ts`

**Intent**: Turn the commented backend `webServer` sketch (`:9-14`) into a real second `webServer` that runs the backend under `ASPNETCORE_ENVIRONMENT=E2E` and waits on `/health`; add a third project for the handshake spec that uses **no** `storageState` and has **no** `setup` dependency, so it starts logged out. Keep the existing `setup` + `chromium` (authenticated) projects intact for the seed/Phase-5 tests.

**Contract**: `webServer` becomes an array: the existing frontend entry plus `{ command: 'dotnet run', cwd: '../strava-segments-performance-backend', url: 'http://localhost:5000/health', env: { ASPNETCORE_ENVIRONMENT: 'E2E', ConnectionStrings__DefaultConnection: <from env> }, reuseExistingServer: !process.env.CI, timeout: 120_000 }`. New project e.g. `{ name: 'chromium-noauth', testMatch: /oauth-handshake\.spec\.ts/, use: { ...devices['Desktop Chrome'] } }`; ensure the `chromium` project's `testMatch`/`testIgnore` excludes the handshake spec so it never runs authenticated. `E2E_API_BASE_URL` continues to default to `http://localhost:5000` for `auth.setup.ts`.

#### 3. The handshake spec

**File**: `strava-segments-performance/e2e/oauth-handshake.spec.ts` (new)

**Intent**: From a fresh, unauthenticated context, visit `/login`, click "Connect with Strava", and follow the full redirect chain; assert the browser ends on `/dashboard` and is genuinely authenticated (dashboard content renders and/or `/api/auth/me` returns the canned athlete). Use role/text locators and state-based waits (`waitForURL('**/dashboard')`, `toBeVisible()`), never `waitForTimeout`.

**Contract**: A single `test('completes the OAuth handshake and lands authenticated', …)` in the `chromium-noauth` project. Assertion anchors: final URL matches `/dashboard`; an authenticated-only element is visible OR a `page.request.get('/api/auth/me')` returns the stub athlete id/name from Phase 1. Test is independent and needs no cleanup beyond its own fresh context (the stub upserts by a fixed athlete id — idempotent across re-runs).

### Success Criteria:

#### Automated Verification:

- Playwright browsers install: `npx playwright install --with-deps chromium` (run in `strava-segments-performance/`)
- Full e2e suite passes locally: `npm run test:e2e` (the handshake spec + the existing `seed` spec)
- The handshake spec runs in a project with no `storageState` (config inspection / `--project=chromium-noauth` runs it in isolation)

#### Manual Verification:

- Watch `npm run test:e2e` (headed, `--project=chromium-noauth --headed`) complete the visible login chain and land on `/dashboard`
- Re-running the suite twice in a row passes both times (idempotent, no cross-run collision)

**Implementation Note**: After automated verification passes, pause for human confirmation that the browser chain runs reliably locally before wiring CI.

---

## Phase 3: e2e CI/CD job

### Overview

Run the browser suite on every PR: a workflow that stands up Postgres, the backend in `E2E`, the frontend, and Playwright, and fails the PR if the handshake breaks.

### Changes Required:

#### 1. New e2e workflow

**File**: `.github/workflows/e2e-ci.yml` (new)

**Intent**: On PRs touching the frontend, backend, or the workflow, run a job that: provisions a Postgres **service**; sets up .NET 10 and Node; installs frontend deps and Playwright browsers; sets `ConnectionStrings__DefaultConnection` to the service; and runs `npm run test:e2e` (Playwright's `webServer` boots both servers, backend under `E2E`). Upload the Playwright HTML report as an artifact on failure.

**Contract**: New workflow keyed on `pull_request` with `paths:` covering `strava-segments-performance/**`, `strava-segments-performance-backend/**`, and the workflow file. A `postgres` service container with health checks; `E2E_API_BASE_URL`/`ConnectionStrings__DefaultConnection` env wired to it; `CI=true` (so Playwright does not reuse a server); `actions/upload-artifact` for `playwright-report/` with `if: failure()`. Mirror the existing workflows' setup-dotnet/setup-node/versions.

#### 2. Register the gate in the test-plan quality gates

**File**: `context/foundation/test-plan.md`

**Intent**: The §5 e2e gate row already exists ("OAuth required after §3 Phase 4"); confirm it now maps to this workflow. No behavioral change beyond documentation accuracy.

**Contract**: Prose/table touch-up in §5 only (name the workflow); no strategy change.

### Success Criteria:

#### Automated Verification:

- The workflow is valid and runs on a PR (GitHub Actions parses and starts it)
- The e2e job completes **green** on a PR branch (backend `E2E` + frontend + Playwright all boot; handshake passes)
- On an intentionally-broken chain, the job **fails** and the Playwright report artifact is uploaded (proves the gate bites)

#### Manual Verification:

- Open a draft PR and confirm the e2e job appears as a check and passes
- Inspect one run's logs: Postgres came up, backend logged `E2E` startup + migration, Playwright ran the `chromium-noauth` project

**Implementation Note**: After the job passes on a PR, pause for human confirmation before proceeding.

---

## Phase 4: Backend integration round-trip (scoped to the delta)

### Overview

A fast, deterministic `WebApplicationFactory<Program>` suite over a Testcontainers Postgres that asserts only the prod-only / negative cases the browser layer cannot reach on localhost-http. Runs inside the existing backend `dotnet test` CI gate — no new workflow.

### Changes Required:

#### 1. Make `Program` reachable + add test harness packages

**Files**: `strava-segments-performance-backend/Program.cs`, `strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`

**Intent**: Expose the top-level `Program` to the test project and add the integration harness dependencies.

**Contract**: Append `public partial class Program { }` to the end of `Program.cs` (after `app.Run()` and the trailing `record`). Add `Microsoft.AspNetCore.Mvc.Testing` (net10.0) and `Testcontainers.PostgreSql` PackageReferences to the test csproj. (`Microsoft.EntityFrameworkCore.InMemory` stays for the existing Phase-2/5 unit tests; it is not used by this harness.)

#### 2. Integration fixture: factory + Testcontainers Postgres + backchannel stub

**File**: `strava-segments-performance-backend-tests/OAuth/OAuthRoundTripFixture.cs` (new; name/namespace to match project convention)

**Intent**: A reusable fixture that starts a throwaway Postgres container, builds a `WebApplicationFactory<Program>` whose config points `ConnectionStrings:DefaultConnection` at the container and supplies a valid base64 `TokenEncryption:Key`, and — via `ConfigureTestServices` — installs a `BackchannelHttpHandler` on the Strava options that returns canned token + athlete responses (and, on demand, a failing token response). Parameterizable by hosting environment so tests can boot `Development` and `Production` variants.

**Contract**: `IAsyncLifetime` fixture exposing a cookie-preserving, non-redirect-following client factory: `CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true })`. `PostConfigure<StravaAuthenticationOptions>` sets `BackchannelHttpHandler` (stub) and dummy `ClientId`/`ClientSecret`. A knob selects success vs. 400-token responses. `MigrateAsync` runs against the container at host startup (relational — the reason InMemory is unusable here).

#### 3. The delta assertions

**File**: `strava-segments-performance-backend-tests/OAuth/OAuthRoundTripTests.cs` (new)

**Intent**: Assert exactly the cases the browser e2e cannot reach:
- **Forwarded-header `redirect_uri`** — `GET /auth/login` with `X-Forwarded-Proto: https` + `X-Forwarded-Host: example.test` → 302 whose authorize `Location` carries `redirect_uri=https://example.test/auth/callback`, plus `scope=activity:read_all`, `response_type=code`, and a `state`. This is the actual prod incident.
- **Cookie matrix** — boot a **Production** host: after a stubbed successful callback the auth cookie is `SameSite=None; Secure`; boot a **Development** host: `SameSite=Lax` and not forced-Secure.
- **Failure branch** — with the token stub returning 400, the callback 302s to `{frontendOrigin}/login?error=auth_failed`.
- **401 branch** — unauthenticated `GET /api/auth/me` returns 401, not a 302 to the login.

**Contract**: xUnit tests using the fixture. The success-callback legs capture `state` + the `.AspNetCore.Correlation.Strava.*` cookie from the challenge and replay them to `/auth/callback?code=…&state=…` (cookie-preserving client). These tests intentionally do **not** re-assert the happy-path landing already proven by the browser e2e — they exist for the redirect_uri/cookie/failure/401 deltas.

#### 4. TokenEncryptionService unit test

**File**: `strava-segments-performance-backend-tests/TokenEncryptionServiceTests.cs` (new)

**Intent**: Lock the encrypt→decrypt round-trip and the missing-key failure mode (the `4565b33` incident context), and guard that the key is never surfaced in output.

**Contract**: `Encrypt` then `Decrypt` returns the original plaintext (fresh IV each call → differing ciphertexts); constructing the service with no `TokenEncryption:Key` throws `InvalidOperationException`. Pure unit test (in-memory `IConfiguration`), no DB.

### Success Criteria:

#### Automated Verification:

- Backend test suite passes, including the new integration + unit tests: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`
- The forwarded-header test fails if `UseForwardedHeaders()` is removed (spot-check by temporary local edit — proves the assertion bites)
- The suite runs in the existing `backend-ci.yml` `test` job with no workflow change (Docker is available on the runner for Testcontainers)

#### Manual Verification:

- `dotnet test` passes on a machine with Docker running (Testcontainers spins Postgres); the run is deterministic across repeats
- Review confirms the integration tests assert only the delta (no happy-path landing re-tested)

**Implementation Note**: After automated verification passes, pause for human confirmation before closing the change.

---

## Phase 5: Deploy-time OAuth redirect smoke

### Overview

After a production deploy, assert the one thing no offline test can see: the live public `GET /auth/login` builds an `https://<prod-host>/auth/callback` `redirect_uri` — i.e. nginx's forwarded proto/host are actually flowing and TLS terminates as expected (the `0b9cca7 fixing https forwarding` failure class). The check is a single unauthenticated request; the engineering is (a) making sure we probe the **new** revision, not a stale pod, and (b) failing loudly when it regresses. Reintroduces the `test-plan.md` §5 deploy-smoke gate that was earlier deferred.

### Changes Required:

#### 1. Build-SHA marker on a publicly-reachable health endpoint

**Files**: `strava-segments-performance-backend/Program.cs`, `strava-segments-performance/nginx.conf`

**Intent**: Let the smoke distinguish the new deploy from the old. Extend `/health` (`Program.cs:137-138`) to also return the deployed commit SHA read from configuration, and make that endpoint reachable through the public frontend origin (nginx currently proxies only `/auth/` and `/api/`, so `/health` falls through to the SPA today).

**Contract**: `/health` returns `{ status, sha }` where `sha = builder.Configuration["BUILD_SHA"]` (empty/`"unknown"` when unset — never throws). Add an nginx `location = /health { proxy_pass … }` (mirroring the existing `/auth/` block) so `https://<frontend-origin>/health` hits the backend. No auth on `/health`. (This SHA marker is deploy tooling, not a behavioral test — `test-plan.md` §7's "don't test `/health`" still holds.)

#### 2. Stamp the SHA at deploy time

**File**: `.github/workflows/backend-ci.yml`

**Intent**: Feed the just-deployed commit into the running backend so `/health` can echo it. Add `BUILD_SHA` to the existing Railway variables step.

**Contract**: In the `Set Railway variables` step, add `BUILD_SHA="${{ github.sha }}"` alongside the other `railway variables set` assignments. No image/Dockerfile change (the value arrives as a runtime env var, mapped to config key `BUILD_SHA`).

#### 3. The smoke script

**File**: `scripts/oauth-redirect-smoke.sh` (new)

**Intent**: A checked-in, reusable probe: given a base URL and an expected SHA, poll `{base}/health` until `sha` matches (bounded timeout, clear failure on timeout), then `curl` `{base}/auth/login`, require a `302`, extract and URL-decode the `redirect_uri` query param from the `Location` header, and assert it starts with `{base}/auth/callback` with scheme `https` and the expected host (not `http`, not `localhost`, not `*.railway.internal`). Non-zero exit on any failure, with the offending value printed.

**Contract**: `oauth-redirect-smoke.sh <base-url> <expected-sha>`; POSIX `sh` + `curl`; no secrets; side-effect-free (the 302 is generated before Strava is contacted; the throwaway correlation cookie is discarded). Parameterized so it runs against any environment (prod, a preview, or locally against a crafted response).

#### 4. Post-deploy smoke step

**File**: `.github/workflows/backend-ci.yml`

**Intent**: Run the script after the backend redeploy on master, against the public frontend origin, gated on the deployed SHA; fail the workflow (loud, visible, notifying) if the redirect_uri regressed. Manual rollback on failure — no auto-revert.

**Contract**: A step (or small dependent job) after `Redeploy backend`, `if: github.ref == 'refs/heads/master'`, invoking `scripts/oauth-redirect-smoke.sh https://frontend-production-2e86.up.railway.app "${{ github.sha }}"`. Runs after the backend deploy because that side owns the forwarded-headers/redirect_uri logic; the frontend nginx image is already live. (Optional, not required by this phase's criteria: `frontend-ci.yml` may invoke the same script after its own redeploy as a lighter retry-based guard without SHA gating, since nginx has no SHA marker.)

### Success Criteria:

#### Automated Verification:

- `/health` returns the SHA locally: run the backend with `BUILD_SHA` set and confirm `curl localhost:5000/health` includes it; unset → `sha` is `"unknown"`, no throw
- The script asserts correctly on crafted inputs: it exits 0 on a `Location` whose `redirect_uri` is `https://<host>/auth/callback`, and non-zero when the scheme is `http` or the host is wrong (drive it against a local fixture/`Location` string)
- `backend-ci.yml` parses and the smoke step is gated to `master`

#### Manual Verification:

- On a real master deploy, the smoke step polls until `/health` reports the new SHA, then prints and asserts the live `redirect_uri` = `https://frontend-production-2e86.up.railway.app/auth/callback`
- Confirm `https://<frontend-origin>/health` is reachable (nginx proxying works) and returns the expected SHA after deploy
- Confirm a forced misconfig (e.g. `FORWARDED_PROTO=http` in a throwaway/preview environment, never prod) makes the script fail — proving the gate bites

**Implementation Note**: After automated verification passes and one real deploy has exercised the smoke, pause for human confirmation before closing the change.

---

## Testing Strategy

### Unit Tests:

- `TokenEncryptionService` encrypt/decrypt round-trip; missing-key throw (Phase 4).

### Integration Tests:

- OAuth round-trip deltas over `WebApplicationFactory` + Testcontainers Postgres + backchannel stub: forwarded-header `redirect_uri`, cookie matrix (Dev + Prod), failure redirect, 401 branch (Phase 4).

### Manual Testing Steps:

1. Run the backend in `E2E` + frontend; click "Connect with Strava"; confirm landing authenticated on `/dashboard` and a stable session on reload (Phase 1).
2. `npm run test:e2e --project=chromium-noauth --headed`; watch the full login chain (Phase 2).
3. Open a PR; confirm the e2e job runs and passes, and that a deliberately broken chain fails with an uploaded report (Phase 3).
4. Confirm no `/e2e-stub/*` route exists when the backend runs in Development or Production (Phase 1).
5. On a master deploy, watch the smoke step poll for the new SHA and assert the live `redirect_uri`; confirm `https://<frontend-origin>/health` returns the deployed SHA (Phase 5).

## Performance Considerations

The e2e job adds real boot time (Postgres + backend migrate + ng serve + Playwright). Keep it to the single handshake spec plus the existing seed; scope its `paths:` trigger so unrelated PRs don't pay the cost. Testcontainers adds container spin-up to `dotnet test` — acceptable for the integration deltas; unit tests remain container-free.

## Migration Notes

No data migrations. `appsettings.E2E.json` and the `E2E`-gated endpoints are additive and inert outside the `E2E` environment. The `public partial class Program { }` line is a compile-time no-op for the running app.

## References

- Related research: `context/changes/testing-oauth-roundtrip/research.md` (R1–R7 resolve the harness, DB, stub, key, pin, and deploy decisions)
- Test strategy: `context/foundation/test-plan.md` §2 (Risk #2), §6.4 (OAuth cookbook), §5 (gates)
- OAuth wiring: `strava-segments-performance-backend/Program.cs:31-194`
- Existing E2E seam & scaffolding: `Program.cs:166-194`, `strava-segments-performance/playwright.config.ts`, `strava-segments-performance/e2e/`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend E2E stub OAuth server

#### Automated

- [x] 1.1 Backend builds (`dotnet build`) — 5aa8007
- [x] 1.2 Backend unit tests still pass (`dotnet test`) — 5aa8007
- [x] 1.3 Stub endpoints + PostConfigure confined to the `IsEnvironment("E2E")` guard (grep) — 5aa8007

#### Manual

- [x] 1.4 Full login through a browser against a local `E2E` backend lands authenticated on `/dashboard` — 5aa8007
- [x] 1.5 `/dashboard` stays authenticated on reload (E2E cookie-policy fix works) — 5aa8007
- [x] 1.6 No `/e2e-stub/*` route exposed in Development/Production — 5aa8007

### Phase 2: Playwright runner + browser OAuth handshake spec

#### Automated

- [x] 2.1 Playwright browsers install (`npx playwright install --with-deps chromium`) — 28cd2e5
- [x] 2.2 Full e2e suite passes locally (`npm run test:e2e`) — 28cd2e5
- [x] 2.3 Handshake spec runs in a no-`storageState` project (`--project=chromium-noauth`) — 28cd2e5

#### Manual

- [x] 2.4 Headed run completes the visible login chain to `/dashboard` — 28cd2e5
- [x] 2.5 Suite passes on two consecutive runs (idempotent) — 28cd2e5

### Phase 3: e2e CI/CD job

#### Automated

- [x] 3.1 Workflow parses and starts on a PR — 7c1a519
- [x] 3.2 e2e job completes green on a PR branch — 7c1a519
- [x] 3.3 Intentionally-broken chain fails the job and uploads the Playwright report — 7c1a519

#### Manual

- [x] 3.4 e2e job appears as a PR check and passes — 7c1a519
- [x] 3.5 Run logs show Postgres up, backend `E2E` startup + migration, `chromium-noauth` executed — 7c1a519

### Phase 4: Backend integration round-trip (scoped to the delta)

#### Automated

- [x] 4.1 Backend test suite passes incl. new integration + unit tests (`dotnet test`) — bcacd6b
- [x] 4.2 Forwarded-header test bites (fails if `UseForwardedHeaders()` removed) — bcacd6b
- [x] 4.3 Suite runs in the existing `backend-ci.yml` `test` job with no workflow change — bcacd6b

#### Manual

- [x] 4.4 `dotnet test` deterministic across repeats with Docker running (Testcontainers) — bcacd6b
- [x] 4.5 Review confirms integration tests assert only the delta (no happy-path re-test) — bcacd6b

### Phase 5: Deploy-time OAuth redirect smoke

#### Automated

- [x] 5.1 `/health` returns `BUILD_SHA` locally (and `"unknown"` when unset, no throw) — acdd169
- [x] 5.2 Smoke script exits 0 on a valid `https://<host>/auth/callback` redirect_uri and non-zero on http/wrong-host — acdd169
- [x] 5.3 `backend-ci.yml` parses and the smoke step is gated to `master` — acdd169

#### Manual

- [ ] 5.4 Real master deploy: smoke polls for the new SHA, then asserts the live redirect_uri is `https://frontend-production-2e86.up.railway.app/auth/callback`
- [ ] 5.5 `https://<frontend-origin>/health` is reachable through nginx and returns the deployed SHA
- [ ] 5.6 A forced misconfig in a throwaway/preview env makes the smoke fail (gate bites)
