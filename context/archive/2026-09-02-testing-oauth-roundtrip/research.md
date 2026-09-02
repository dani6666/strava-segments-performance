---
date: 2026-09-02T17:00:24+0200
researcher: Daniel Włudarczyk
git_commit: 754a3da583b8ec06d23d5e7363c852fe5a4cd578
branch: feature/e2e-tests
repository: strava-segments-performance (github.com/dani6666/strava-segments-performance)
topic: "OAuth handshake round-trip — grounding Phase 4 tests (Risk #2)"
tags: [research, codebase, oauth, strava, auth, forwarded-headers, playwright, e2e, integration-test]
status: complete
last_updated: 2026-09-02
last_updated_by: Daniel Włudarczyk
last_updated_note: "Resolved the six open questions with grounded answers (WebApplicationFactory seam, relational-DB requirement, stub-server mechanics, test key, Playwright pin, deploy-smoke home)"
---

# Research: OAuth handshake round-trip — grounding Phase 4 tests (Risk #2)

**Date**: 2026-09-02T17:00:24+0200
**Researcher**: Daniel Włudarczyk
**Git Commit**: 754a3da583b8ec06d23d5e7363c852fe5a4cd578
**Branch**: feature/e2e-tests
**Repository**: strava-segments-performance

## Research Question

Ground the implementation of the Strava OAuth handshake round-trip so that
Phase 4 of the frozen test-plan (`context/foundation/test-plan.md`, Risk #2)
can be planned. Specifically, locate and document the exact code for: the
`CallbackPath` registration, how `redirect_uri` is built from forwarded headers
behind the proxy, the `OnCreatingTicket` user-creation and both redirect targets
(success `/dashboard`, failure `/login?error=auth_failed`), the 401-vs-redirect
branch, cookie policy per environment, and how the OAuth handler could be
pointed at a stub authorize/token server in tests — plus what e2e scaffolding
already exists on this branch and what actually broke in prod.

## Summary

**All backend OAuth wiring lives in one file — `strava-segments-performance-backend/Program.cs`** — using the `AspNet.Security.OAuth.Strava` v10.0.0 provider over ASP.NET Core cookie auth (BFF pattern: the backend owns tokens; Angular never sees them). Every grounding point the test-plan named is present and precisely located (see Code References).

Three findings decisively shape the plan:

1. **The Strava authorize/token/userinfo endpoints are HARDCODED package defaults — not bound to configuration.** A config/appsettings override cannot repoint them at a stub server. A *real* challenge→callback→token-exchange integration test (test-plan §6.4 layer 1) therefore requires a **new code seam** — a `PostConfigure<StravaAuthenticationOptions>` (or a `WebApplicationFactory` that overrides `AuthorizationEndpoint`/`TokenEndpoint`/`UserInformationEndpoint`) — which does **not** exist today. This is the single biggest planning implication.

2. **The e2e scaffolding is half-built and must not be duplicated.** The Playwright *source* (`playwright.config.ts`, `e2e/auth.setup.ts`, `e2e/fixtures.ts`, `e2e/seed.spec.ts`) and the backend `/auth/test-login` E2E-seam (`Program.cs:166-194`) already exist on this branch. What is missing: the `@playwright/test` dependency + version pin, an e2e npm script, the backend `webServer`/E2E-DB entry in the Playwright config, a backend integration-test harness (`WebApplicationFactory` / `Microsoft.AspNetCore.Mvc.Testing` — **not** referenced), and an e2e CI job.

3. **The prod failure class is proxy scheme/host construction, not token math.** The historical churn (`0b9cca7`, `5f99ff5`, `fdec002`) was all about making `redirect_uri` come out as `https://<host>/auth/callback` behind the Railway proxy. `FORWARDED_PROTO` is a *static build-time constant* in nginx, and `ForwardedHeaders` trusts any upstream — so any test that mocks away forwarded headers hides exactly the bug that occurred. The deploy-smoke gate exists for this. **Correction to the test-plan's smoke command:** the login path is `/auth/login`, **not** `/api/auth/login` (the latter 404s).

## Detailed Findings

### A. Backend OAuth wiring (`Program.cs`)

Provider: `AspNet.Security.OAuth.Strava` v10.0.0 (`strava-segments-performance-backend/strava-segments-performance-backend.csproj:11`).

- **Handler registration** — cookie default scheme + Strava OAuth chained: `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(...)` (`Program.cs:31-33`), `.AddStrava(options => {...})` (`Program.cs:54-95`).
- **CallbackPath** = `/auth/callback` (`Program.cs:60`). **Scope** = single `activity:read_all` (`Program.cs:58`). `SaveTokens = true` (`Program.cs:59`).
- **Endpoints (hardcoded package defaults, NOT set in code, NOT config-bound)** — from the DLL: AuthorizationEndpoint `https://www.strava.com/oauth/authorize`, TokenEndpoint `https://www.strava.com/oauth/token`, UserInformationEndpoint `https://www.strava.com/api/v3/athlete`.
- **`state`** — not handled manually; managed by the framework `OAuthHandler` (correlation cookie + state param).

### B. Login challenge endpoint

- **`GET /auth/login`** (`Program.cs:140-146`) — issues `Results.Challenge` against the Strava scheme with `AuthenticationProperties.RedirectUri = {frontendOrigin}/dashboard`. That RedirectUri is the **post-auth app landing**, *not* the OAuth `redirect_uri`. `returnUrl` query param is accepted but unused.
- Note the path is `/auth/login`, not under `/api`. The `/api/auth/*` group only holds `me` / `logout`.

### C. redirect_uri construction behind the proxy (the prod-critical piece)

- `ForwardedHeadersOptions` configured with `XForwardedFor | XForwardedProto | XForwardedHost` and `KnownNetworks.Clear()` + `KnownProxies.Clear()` (`Program.cs:107-112`) — forwarded headers trusted from **any** upstream (no proxy-IP allowlist).
- `app.UseForwardedHeaders()` runs **first**, before CORS/auth (`Program.cs:116`) — so `Request.Scheme`/`Request.Host` are rewritten before the OAuth middleware composes `redirect_uri = {forwarded-scheme}://{forwarded-host}/auth/callback`.
- No manual scheme/host override in C#; it relies entirely on `UseForwardedHeaders` reading nginx's `X-Forwarded-Proto` / `X-Forwarded-Host`.

### D. OnCreatingTicket — user upsert + token storage

- `options.Events.OnCreatingTicket` (`Program.cs:62-87`): reads `stravaId` from `ClaimTypes.NameIdentifier` and `displayName` from `ClaimTypes.Name` (`:66-67`); **encrypts** access + refresh tokens via `TokenEncryptionService` before storage (`:68-69`); computes `TokenExpiresAtUtc` from `ExpiresIn` (fallback +6h, `:70-72`); upserts `User` by `StravaAthleteId` and `SaveChangesAsync` (`:74-86`).
- Token encryption: AES-CBC, random IV prepended to ciphertext, base64; key from config `TokenEncryption:Key` (`Services/TokenEncryptionService.cs:11-48`). **The key is absent from both appsettings files** — must be supplied via env/user-secrets or the service throws at first use. Registered singleton at `Program.cs:97`. `User` fields at `Models/User.cs:8-10`.
- No try/catch in `OnCreatingTicket` — a missing claim (`long.Parse`) or `SaveChangesAsync` failure propagates to `OnRemoteFailure` (accepted in the original impl-review as F3).

### E. Redirect targets

- **Success → `{frontendOrigin}/dashboard`** (`Program.cs:142-144`).
- **Failure → `{frontendOrigin}/login?error=auth_failed`** via `OnRemoteFailure` with `context.HandleResponse()` (`Program.cs:89-94`).

### F. 401-vs-redirect branch

- Cookie `Events.OnRedirectToLogin` (`Program.cs:43-52`): if `Request.Path.StartsWithSegments("/api")` → set `401` and return (no redirect); otherwise redirect normally. All `/api/*` endpoints carry `.RequireAuthorization()` (`Program.cs:158, 275, 284, 293`), so unauthenticated `/api/*` yields a clean 401 (JSON), not a 302 to Strava.

### G. Cookie policy per environment

- `HttpOnly = true` always (`Program.cs:35`).
- **SameSite**: `Lax` in Development, **`None`** in prod (`Program.cs:36-38`).
- **SecurePolicy**: `SameAsRequest` in Development, **`Always`** in prod (`Program.cs:39-41`).
- `None` + `Always` (cross-site credentialed cookie) **depends on `UseForwardedHeaders` reporting https** — ties directly to finding C. CORS is credential-aware: policy `"Frontend"` `WithOrigins(frontendOrigin).AllowCredentials()` (`Program.cs:22-29`, applied `:133`).

### H. Pointing the OAuth handler at a stub server (test seam — DOES NOT EXIST)

- Authorize/token/userinfo endpoints are never assigned in `Program.cs`, so they stay at the `www.strava.com` defaults baked into the provider. **A plain appsettings/config override will not repoint them** — no config keys are bound to them.
- To exercise a genuine authorize→callback→token round-trip against a stub, the plan must add a seam: a `PostConfigure<StravaAuthenticationOptions>` in a test host (or a `WebApplicationFactory`) that sets `AuthorizationEndpoint` / `TokenEndpoint` / `UserInformationEndpoint` (and likely a `BackChannelHttpHandler` to the stub). None exists today.
- The team's current approach **sidesteps OAuth entirely** for authenticated tests via `/auth/test-login` (finding I) — which is correct for Phase 5/seed but is explicitly **forbidden for the Phase 4 handshake test itself** (test-plan §6.4: Phase 4 must start unauthenticated and drive the real chain).

### I. Existing e2e / test scaffolding (branch `feature/e2e-tests`)

| Component | Exists? | Location | Notes |
|---|---|---|---|
| `playwright.config.ts` | Yes | `strava-segments-performance/playwright.config.ts` | `testDir ./e2e`; `baseURL http://localhost:4200`; projects **setup** (`/auth\.setup\.ts/`) + **chromium** (`storageState playwright/.auth/user.json`, `dependencies:['setup']`); `webServer: npm start` (frontend only; backend block commented out, sketches `ASPNETCORE_ENVIRONMENT: E2E`) |
| `@playwright/test` dependency | **No** | — | absent from `package.json` devDeps + `node_modules`; **no version pinned**; no `test:e2e` script |
| `e2e/auth.setup.ts` | Yes | `strava-segments-performance/e2e/auth.setup.ts` | GETs `${E2E_API_BASE_URL ?? http://localhost:5000}/auth/test-login?athleteId&name`, saves `storageState` to `playwright/.auth/user.json` |
| `e2e/fixtures.ts` | Yes | `strava-segments-performance/e2e/fixtures.ts` | `SEED_USER = { stravaAthleteId: 12345, displayName: 'Test Rider' }`; `test` fixture `goto('/dashboard')`; no `page.route` mocks (hits real backend authenticated) |
| `e2e/seed.spec.ts` | Yes | `strava-segments-performance/e2e/seed.spec.ts` | single test: `getByRole('heading', { name: /welcome/i })` visible |
| `/auth/test-login` backend seam | **Yes (built)** | `Program.cs:166-194` | gated `app.Environment.IsEnvironment("E2E")`; upserts user + `SignInAsync` real cookie; `?athleteId&name` |
| Backend integration harness (`WebApplicationFactory` / `Mvc.Testing`) | **No** | — | not referenced anywhere; must be added for the integration round-trip |
| Backend test project | Yes (unit only) | `strava-segments-performance-backend-tests/` | xUnit 2.9.3, EFCore.InMemory 10.0.9, TimeProvider.Testing 10.8.0, coverlet 6.0.4; tests: `FitnessScoringTests`, `FitnessTrendQueryTests`, `StravaApiClientTests`; **no auth/OAuth tests** |
| e2e CI job | **No** | `.github/workflows/` | only `backend-ci.yml`, `db-ci.yml`, `frontend-ci.yml` (unit) |
| `.gitignore` (root) e2e ignores | Yes (uncommitted) | `.gitignore` | adds `playwright` + `.playwright-cli` (the modified file in git status) |
| `.gitignore` (frontend) e2e ignores | Yes | `strava-segments-performance/.gitignore:42-47` | `/playwright/.auth/`, `/test-results/`, `/playwright-report/`, `/blob-report/`, `/playwright/.cache/` |
| `playwright/.auth/user.json` | Yes (stale) | repo root | ephemeral/gitignored; currently holds stale `www.strava.com` CLI cookies — regenerated per run |

### J. Config surface & environment wiring

- **nginx** (`strava-segments-performance/nginx.conf`): proxies `/auth/` (`:12-19`) and `/api/` (`:21-28`); SPA fallback at `/` (`:8`). Does **not** set `proxy_set_header Host`; instead forwards `X-Forwarded-Host $http_host` and `X-Forwarded-Proto ${FORWARDED_PROTO}` (`:14-15`, `:23-24`). `$http_host` preserves the original host+port. **`FORWARDED_PROTO` is a build-time env constant, not the real edge scheme** — the top prod risk.
- **Frontend env**: `environment.ts:3` `apiBaseUrl: 'http://localhost:5000'` (dev); `environment.prod.ts:3` `apiBaseUrl: ''` (same-origin via nginx). Login trigger: `auth.service.ts:24-26` `window.location.href = ${apiBaseUrl}/auth/login` (full navigation, required for the 302 chain), from the "Connect with Strava" button (`login.component.html:9`). Routes (`app.routes.ts`): `login` (no guard), `dashboard` (`canActivate:[authGuard]`), `''`/`**` → `/dashboard`; `authGuard` (`auth/auth.guard.ts:16`) → `/login` on `checkAuth()` failure. Authenticated calls use `withCredentials: true`.
- **Backend appsettings**: `appsettings.json` = Logging + `AllowedHosts` only (no OAuth keys). `appsettings.Development.json`: `Frontend:Origin = http://localhost:4200`, `Strava:ClientId`/`ClientSecret` = placeholders, real local Postgres conn string. **No `appsettings.Production.json` or `appsettings.E2E.json`** — non-dev config comes entirely from env vars. `TokenEncryption:Key` absent from both (env-only).
- **Docker/compose**: compose backend `ASPNETCORE_ENVIRONMENT: Development`, `:5000`; frontend `BACKEND_ORIGIN: http://backend:5000`, `FORWARDED_PROTO: http`. `Frontend:Origin` **not** overridden in compose → falls back to appsettings dev value. Frontend `Dockerfile` prod defaults `BACKEND_ORIGIN=http://backend.railway.internal:8080`, `FORWARDED_PROTO=https`, `:8080`. Backend `Dockerfile` sets `ASPNETCORE_URLS=http://+:8080`, `EXPOSE 8080`, and **no `ASPNETCORE_ENVIRONMENT`** → defaults to **Production** in prod.
- **Deploy-smoke target**: login is `GET /auth/login` (302 whose `Location` is the Strava authorize URL, with `redirect_uri=https://<host>/auth/callback` embedded). Assert the scheme+host+path `https://…/auth/callback`. **The test-plan's `curl /api/auth/login` is wrong — use `/auth/login`.** Dev/compose port `:5000`, prod `:8080`.

## Code References

- `strava-segments-performance-backend/Program.cs:31-95` — auth registration (cookie + Strava options block)
- `Program.cs:60` — `CallbackPath = "/auth/callback"`; `:58` — scope `activity:read_all`
- `Program.cs:140-146` — `GET /auth/login` challenge; `RedirectUri = {frontendOrigin}/dashboard`
- `Program.cs:107-112` — `ForwardedHeadersOptions` (XFF/Proto/Host, cleared KnownNetworks/Proxies); `:116` — `UseForwardedHeaders()`
- `Program.cs:62-87` — `OnCreatingTicket` upsert + token encryption
- `Program.cs:89-94` — `OnRemoteFailure` → `/login?error=auth_failed`
- `Program.cs:43-52` — `OnRedirectToLogin` → 401 for `/api/*`
- `Program.cs:35-41` — cookie SameSite/SecurePolicy per environment
- `Program.cs:22-29`, `:133` — credential-aware CORS `Frontend` policy
- `Program.cs:118-126` — startup `MigrateAsync()` + `ExecuteUpdateAsync` (relational-only; blocks InMemory for the full-host test)
- `Program.cs:166-194` — E2E-only `GET /auth/test-login` seam
- `Program.cs:295` — `app.Run()` (end of top-level statements; no `public partial class Program`)
- `.github/workflows/backend-ci.yml` — `dotnet test` gate + Railway deploy; `Frontend__Origin=https://frontend-production-2e86.up.railway.app`, `ASPNETCORE_ENVIRONMENT=Production`, `:8080`
- `.github/workflows/frontend-ci.yml` — frontend Railway deploy; `BACKEND_ORIGIN=http://backend.railway.internal:8080` (backend not public)
- `Services/TokenEncryptionService.cs:11-48` — AES token encrypt/decrypt; key from `TokenEncryption:Key`
- `Models/User.cs:8-10` — `AccessToken`, `RefreshToken`, `TokenExpiresAtUtc`
- `strava-segments-performance/playwright.config.ts` — Playwright config (setup+chromium, storageState, deps)
- `strava-segments-performance/e2e/auth.setup.ts` — calls `/auth/test-login`, saves storageState
- `strava-segments-performance/e2e/fixtures.ts` — `SEED_USER`, `test` fixture → `/dashboard`
- `strava-segments-performance/nginx.conf:12-28` — `/auth/` + `/api/` proxy, X-Forwarded-Host/Proto
- `strava-segments-performance/src/app/auth/auth.service.ts:24-26` — login navigation
- `strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj:11-20` — test deps (no `Mvc.Testing`)

## Architecture Insights

- **BFF, single-file wiring.** All OAuth lives in `Program.cs`; there is no separate auth module. Tests target this one composition root — a `WebApplicationFactory<Program>` is the natural integration entry point (the `Program` partial-class visibility should be confirmed in planning).
- **The wiring is the risk, not the crypto.** Path registration, `redirect_uri` scheme/host from forwarded headers, the two redirect targets, and the 401 branch are the fragile seams. The token math is provider-owned (explicitly out of scope per test-plan §7 "OAuth middleware library internals").
- **Two distinct auth seams for tests, do not conflate them.** (1) `/auth/test-login` mints a real cookie without Strava — for Phase 5 / seed / any test that needs a session but does *not* test login. (2) The Phase 4 handshake test must start **unauthenticated** and drive the real challenge→callback→cookie→redirect against a **stub authorize/token server** — which needs the not-yet-existing endpoint-override seam (finding H). `page.route` cannot stub the server-side code→token exchange (the backend, not the browser, calls Strava's token endpoint), so the browser-e2e layer of Phase 4 asserts the redirect *chain* while the integration layer asserts the *exchange*.
- **Three test layers for Risk #2** (test-plan §6.4): integration round-trip (`WebApplicationFactory` + stub authorize/token) for the wiring; browser e2e (Playwright, stub provider) for the redirect chain through Angular; deploy-smoke (`curl /auth/login`) for the real prod `redirect_uri`.

## Historical Context (from prior changes)

- `context/archive/2026-06-13-strava-oauth-login/` — the feature's design record. `plan.md:190-196` explicitly flagged that the callback URL is built **from the request host**, not from a `Strava__CallbackUrl` env var — "this is the exact behavior that later broke behind the proxy." `impl-review.md` accepted the prod cookie divergence (`SameSite=None`/`Secure=Always`, F1) and added `TokenEncryptionService` (F5) after finding plaintext tokens in the DB.
- **Prod bug saga (the failure class Phase 4 must lock down):**
  - `0b9cca7` "fixing https forwarding" — added `ForwardedHeaders` + `UseForwardedHeaders` (`Program.cs:107-116`); without it the backend saw `http` and built an `http://localhost` `redirect_uri` Strava rejects.
  - `5f99ff5` "fixing login redirects" — tried a bare `return 302 ${BACKEND_ORIGIN}` for nginx `/auth/`; changed CI URLs `:5000`→`:8080`.
  - `fdec002` "fixing auth/me redirecting" — reverted `/auth/` to `proxy_pass`, moved `BACKEND_ORIGIN` to `backend.railway.internal:8080`, forced `X-Forwarded-Host $host` + `X-Forwarded-Proto https` (the `return 302` had broken `/api/auth/me` cross-origin).
  - `4565b33` "debuging key encryptuion" — logged the AES `TokenEncryption:Key` to stdout while diagnosing a bad/missing key → **leaked the secret to logs**; `aff6089` removed it 4 min later. Lesson for tests: cover encrypt/decrypt round-trip + a missing/invalid-key failure mode, and never print the key.
- `context/archive/2026-07-10-workout-data-fetch/` — added `IStravaTokenService`/`StravaTokenService` refresh on ~6h expiry (Risk #4, adjacent phase); decrypts `user.AccessToken` to attach the bearer.
- **E2E groundwork on this branch:** `567fbd5` first Playwright scaffolding (originally `page.route`-stubbed `/api/auth/me`); `7bedd8d` replaced the stub approach with the real `/auth/test-login` session seam + `auth.setup.ts` storageState; `bc41f69` widened Risk #2 in the test-plan and added the e2e phases.

## Related Research

- `context/archive/2026-06-13-strava-oauth-login/research.md` — original OAuth/BFF exploration.
- `context/archive/2026-07-10-workout-data-fetch/plan.md` — token refresh / encryption usage.
- `context/foundation/test-plan.md` §2 (Risk #2 Response Guidance), §6.4 (OAuth test cookbook) — the frozen spec this research grounds.

## Resolved Questions (Phase 4 planning decisions)

Each of the six is now grounded in code; a plan can proceed on these.

### R1 — `Program` accessibility for `WebApplicationFactory` → **needs two small changes**

- `Program.cs` uses **top-level statements** (`WebApplication.CreateBuilder` at `Program.cs:12`, `app.Run()` at `:295`). There is **no `public partial class Program`**, so the compiler-generated `Program` is `internal` and `WebApplicationFactory<Program>` will not compile from the test project. **Fix:** append `public partial class Program { }` to `Program.cs` (one line) *or* add `[assembly: InternalsVisibleTo("strava-segments-performance-backend-tests")]`.
- The test project is a plain `Microsoft.NET.Sdk` with **no `Microsoft.AspNetCore.Mvc.Testing` reference** (`strava-segments-performance-backend-tests.csproj:11-20`). **Fix:** add `Microsoft.AspNetCore.Mvc.Testing` (net10.0) — it brings the `Microsoft.AspNetCore.App` framework reference the host needs.

### R2 — Startup migrate forces a **relational** test DB (InMemory won't work) → biggest constraint

- On startup the host runs `await db.Database.MigrateAsync()` **and** `ExecuteUpdateAsync(...)` (`Program.cs:118-126`) — both are **relational-only** EF Core APIs.
- Therefore `Microsoft.EntityFrameworkCore.InMemory` (already referenced in the test csproj, `…csproj:14`) **cannot** back a `WebApplicationFactory<Program>` round-trip: `MigrateAsync()` throws *"Relational-specific methods can only be used with a relational store"* on the InMemory provider, and startup fails before any test runs. InMemory stays valid for **Phase 2/5** tests that construct `AppDbContext` directly, but **not** for the full-host OAuth test.
- **Decision for the plan:** the Phase 4 integration round-trip runs against a **real throwaway Postgres** — `Testcontainers.PostgreSql` (Docker is available in GitHub Actions) or a CI `services: postgres` container — with `ConnectionStrings:DefaultConnection` overridden to it via `WebApplicationFactory`. This also exercises the *real* relational upsert the callback performs (`OnCreatingTicket`, `Program.cs:74-86`), which is part of the assertion. (Alternative — refactor `Program.cs` to gate the startup migrate behind a test flag + swap to InMemory — is more invasive and wouldn't test the real relational path; not recommended.)

### R3 — Stub authorize/token server → `PostConfigure` + `BackchannelHttpHandler`, drive callback directly

- `StravaAuthenticationOptions : OAuthOptions` exposes settable `AuthorizationEndpoint`, `TokenEndpoint`, `UserInformationEndpoint`, and `BackchannelHttpHandler`. Override them in the factory: `builder.ConfigureTestServices(s => s.PostConfigure<StravaAuthenticationOptions>(StravaAuthenticationDefaults.AuthenticationScheme, o => { o.BackchannelHttpHandler = stub; o.ClientId = o.ClientSecret = "test"; }))`.
- **Round-trip mechanics** — use a non-redirect-following, cookie-preserving client (`factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true })`):
  1. `GET /auth/login` → 302. Assert `Location` host = AuthorizationEndpoint and its query `redirect_uri = {scheme}://{host}/auth/callback`, `scope=activity:read_all`, `response_type=code`, `client_id`, and `state` present. Capture `state` + the `.AspNetCore.Correlation.Strava.*` Set-Cookie.
  2. `GET /auth/callback?code=fake&state=<captured>` **carrying the correlation cookie** → handler POSTs the (stubbed) TokenEndpoint (returns access/refresh/expires_in) then GETs the (stubbed) UserInformationEndpoint (returns athlete id + name) → `OnCreatingTicket` upserts the `User` → auth cookie Set-Cookie → 302 to `{frontendOrigin}/dashboard`. Assert all of these.
  3. **Failure branch:** stub the TokenEndpoint to return 400 → `OnRemoteFailure` → 302 `{frontendOrigin}/login?error=auth_failed`.
- **To exercise the prod bug (scheme/host):** send `X-Forwarded-Proto: https` + `X-Forwarded-Host: example.test` on the `/auth/login` request and assert the returned `redirect_uri` is `https://example.test/auth/callback` — this is the `UseForwardedHeaders` path (`Program.cs:107-116`) the offline test must NOT mock away. The correlation-cookie/state coupling is the only fiddly part; the cookie-preserving client handles it.

### R4 — Test `TokenEncryption:Key` → supply a 32-byte base64 key via in-memory config

- `TokenEncryptionService` throws `InvalidOperationException` if `TokenEncryption:Key` is missing (`Services/TokenEncryptionService.cs:11-12`), then `Convert.FromBase64String` requires valid base64 and AES requires a 16/24/32-byte key. **Fix:** the test host supplies e.g. `builder.UseSetting("TokenEncryption:Key", Convert.ToBase64String(new byte[32]))`.
- `Encrypt` prepends a fresh 16-byte IV then base64-encodes (`…Service.cs:16-31`); `Decrypt` slices `[..16]`/`[16..]` (`:33-48`). Worth a small **unit** test (Encrypt→Decrypt round-trip + missing-key throw), and — per the `4565b33` incident — an assertion/guard that the key is never logged.

### R5 — Playwright pin + backend webServer → `@playwright/test@^1.61.0`, wire the commented block

- **Version:** Context7 (`/microsoft/playwright`) lists **v1.61.0** as current → pin `@playwright/test@^1.61.0` (matches the test-plan's 1.61.x candidate). *Checked: 2026-09-02.*
- **Missing wiring** (all still to-do): add `@playwright/test` to `strava-segments-performance/package.json` devDeps, add a `test:e2e` script, and add `npx playwright install --with-deps chromium`.
- **Backend webServer:** the commented block at `playwright.config.ts:9-14` is the placeholder — add a second `webServer` running `dotnet run` (cwd `../strava-segments-performance-backend`) with `env: { ASPNETCORE_ENVIRONMENT: 'E2E' }`, `url: 'http://localhost:5000/health'`, `reuseExistingServer: !process.env.CI`. `auth.setup.ts` reads `E2E_API_BASE_URL` (defaults `http://localhost:5000`).

### R6 — E2E database → same relational Postgres decision as R2

- `/auth/test-login` (`Program.cs:173-193`) and the startup migrate both need a real Postgres. **Decision:** the E2E backend points at a **throwaway Postgres** (Docker service in CI; a local dev DB or `docker compose` Postgres locally). This is the same call as R2 — make it once. `Frontend:Origin` must be set to the E2E frontend origin (`http://localhost:4200`) for CORS + redirect; no `appsettings.E2E.json` exists yet, so supply via env.

### R7 — Deploy-smoke home → post-deploy step against the **frontend public origin**

- The backend is **internal-only** on Railway (`backend.railway.internal:8080`, `frontend-ci.yml` sets `BACKEND_ORIGIN`); the **only public OAuth entry is through the frontend origin** `https://frontend-production-2e86.up.railway.app` (from `backend-ci.yml`'s `Frontend__Origin`). So the smoke must curl `https://frontend-production-2e86.up.railway.app/auth/login` and assert the 302 `Location`'s decoded `redirect_uri` is `https://frontend-production-2e86.up.railway.app/auth/callback` (scheme **https**, correct host).
- **Home:** a post-deploy step after the frontend **Redeploy** in `frontend-ci.yml` (or a small separate master-gated job that runs after *both* services redeploy) — with a `/health` poll first, since `railway redeploy` returns before the new revision is live.
- **Test-plan correction:** the path is `/auth/login`, **not** `/api/auth/login` (`§5` / `§6.4`) — the latter 404s.

## Remaining genuinely-open (decide in `/10x-plan`)

- **Testcontainers vs. CI `services: postgres`** for R2/R6 — Testcontainers gives one mechanism that works identically local + CI (needs Docker on the dev machine); a CI Postgres service is lighter in CI but needs a separate local story. Lean Testcontainers for parity.
- **Which environment the integration test runs under** — cookie policy branches on `IsDevelopment()` (`Program.cs:36-41`); to assert the prod `SameSite=None`/`Secure=Always` branch and the Dev `Lax`/`SameAsRequest` branch, the test likely parametrizes `UseEnvironment(...)` across two hosts. Confirm the matrix in planning.
- **Whether to add the `public partial class Program`** in this change or as a tiny separate prep commit — trivial, but it touches production `Program.cs`.
