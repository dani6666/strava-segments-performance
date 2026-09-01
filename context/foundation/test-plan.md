# Test Plan

> Phased test rollout for this project. Strategy is frozen at the top
> (§1–§5); cookbook patterns at the bottom (§6) fill in as phases ship.
> Read before writing any new test.
>
> Refresh: re-run `/10x-test-plan --refresh` when stale (see §8).
>
> Last updated: 2026-09-01 (Phase 2 change opened, running in parallel with Phase 1)

## 1. Strategy

Tests follow three non-negotiable principles for this project:

1. **Cost × signal.** The cheapest test that gives a real signal for the
   risk wins. Do not promote to e2e because e2e "feels safer." Do not put a
   vision model on top of a deterministic visual diff that already catches
   the regression.
2. **User concerns are first-class evidence.** Risks anchored in "the team
   is worried about X, and the failure would surface somewhere in <area>"
   carry the same weight as PRD lines or hot-spot data.
3. **Risks are scenarios, not code locations.** This plan documents *what
   could fail* and *why we believe it's likely* — drawn from documents,
   interview, and codebase *signal* (churn, structure, test base). It does
   NOT claim to know which line owns the failure. That knowledge is
   produced by `/10x-research` during each rollout phase. If the plan and
   research disagree about where the failure lives, research is the
   ground truth.

Hot-spot scope used for likelihood weighting: `strava-segments-performance-backend/`, `strava-segments-performance/src/` (excluding `Migrations/`, generated `*.Designer.cs`/snapshot files, and build output).

## 2. Risk Map

The top failure scenarios this project must protect against, ordered by
risk = impact × likelihood. Risks are failure scenarios in user / business
terms, not test names. The Source column cites the *evidence that surfaced
this risk* — never a specific file as "where the failure lives" (that is
research's job, see §1 principle #3).

| # | Risk (failure scenario) | Impact | Likelihood | Source (evidence — not anchor) |
|---|---|---|---|---|
| 1 | A scoring change produces a plausible-looking but **wrong fitness trend on real-world data** — the product silently lies about whether the user is getting fitter | High | High | PRD FR-004 ("formula validation is the core risk"); interview Q1, Q4; hot-spot dir `backend/Services/` (~18 commits/30d) |
| 2 | **OAuth login/callback fails or diverges between dev and prod** (cookie SameSite/Secure + forwarded-header config), leaving the user stuck at a login error — blocks the entire app | High | High | interview Q2 (lived incident), Q3; PRD FR-001; hot-spot `Program.cs` churn (7 commits/30d, holds OAuth wiring) |
| 3 | The **fetch worker mishandles a 429 or a mid-fetch interruption** — job dies, re-fetches cached data (risking Strava suspension), or leaves a stuck `Running` status blocking re-trigger | High | High | interview Q3, Q4; PRD FR-003 guardrail + AGENTS.md rate-limit rule; archive `workout-data-fetch/plan.md`; hot-spot dir `backend/Services/` |
| 4 | **Token refresh fails on the ~6h expiry during a long fetch**, aborting a multi-hour run | High | Medium | interview Q4; archive `workout-data-fetch/plan.md` (refresh first exercised in S-02); hot-spot dir `backend/Services/` |
| 5 | **One user's efforts leak into another's** fitness trend or fetch status — endpoint checks "logged in" but not "this data is yours" (IDOR) | High | Medium | PRD Access Control ("each sees only their own data"); interview Q4; archive `fitness-trend-chart/plan.md` (manual join, no user id on efforts); hot-spot `Program.cs` churn |
| 6 | The **trend chart errors or misrenders** on empty/sparse/large series — a user who fetched data sees a broken dashboard instead of their trend | Medium | Medium | interview Q4; hot-spot dir `frontend src/app/` (19 commits/30d, dashboard); archive `fitness-trend-chart/plan.md` |

Order: protect High × High first (#1, #2, #3), then High × Medium (#4, #5), then Medium × Medium (#6).

**Abuse / security lens.** The product has auth and per-user data, so the map includes abuse scenarios: #5 covers authorization/access (IDOR — endpoints must verify ownership, not just authentication) and #3 covers resource abuse (rate-limit / redundant re-fetch). Secret leakage was reviewed — tokens are stored encrypted — and untrusted-input parity is partly guarded by an existing fetch-window validator; neither warranted an additional row for v1.

### Risk Response Guidance

| Risk | What would prove protection | Must challenge | Context `/10x-research` must ground | Likely cheapest layer | Anti-pattern to avoid |
|------|-----------------------------|----------------|--------------------------------------|-----------------------|-----------------------|
| #1 | Same segment time at lower HR scores higher; best-in-window → ~100, worst → ~0; a realistic multi-workout fixture matches an **independently hand-computed** expectation | "The expected score came from running the scorer" (oracle problem — tautological) | Locked pipeline stages, window semantics, tie/stall rules, min-scored-efforts gate | unit (pure scorer) | expected value lifted from the implementation instead of from requirements |
| #2 | Cookie SameSite/Secure resolves correctly per environment; `/api/*` returns 401 (not a redirect) when unauthenticated | "Auth works in dev implies it works in prod" | env-branching + forwarded-headers behind a proxy; the 401-vs-redirect branch | integration / config | asserting only the dev branch; mocking away the environment difference |
| #3 | A 429 triggers a bounded wait + retry (not a failure); an interrupted run resets `Running` → `Interrupted` and re-trigger resumes without re-fetching cached activities | "Final status 200 means the retry logic worked" | 429/`Retry-After` handling, startup status reset, idempotent re-list/diff | integration w/ injected clock (`FakeTimeProvider`) | real sleeps; happy-path-only; asserting no re-fetch from logs instead of state |
| #4 | An expired access token triggers refresh and the fetch continues without user action | "The token was fine because the call succeeded" | refresh trigger condition, clock injection | integration w/ injected clock | real time waits; testing refresh in isolation from the fetch loop |
| #5 | `fitness-trend` and `fetch-status` return **only** the calling user's data across a two-user seed | "Logged-in implies scoped" | user resolution + the manual effort→activity join | integration (in-memory / SQLite `AppDbContext`) | single-user fixture that cannot reveal a leak |
| #6 | The chart component renders without error on empty, sparse, and normal series; an empty series shows the empty state | "Rendering equals correct"; styling is not the target | series → chart-config mapping, empty-state branch | Vitest component test | snapshot/styling assertions; testing pixel look (explicit negative space) |

## 3. Phased Rollout

Each row is a discrete rollout phase that will open its own change folder
via `/10x-new`. Status moves left-to-right through the values below; the
orchestrator updates Status as artifacts appear on disk.

| # | Phase name | Goal (one line) | Risks covered | Test types | Status | Change folder |
|---|---|---|---|---|---|---|
| 1 | Scoring realistic-data coverage | Prove Risk #1 scores hold on realistic multi-workout data against independent oracles | #1 | unit | change opened | context/changes/testing-scoring-coverage/ |
| 2 | Authorization + endpoint data-path | Prove Risk #5 no cross-user leak and window filtering is correct | #5 | integration | change opened | context/changes/testing-authorization-data-path/ |
| 3 | Fetch worker resilience | Prove Risk #3/#4 429 backoff, token refresh, and interrupt/resume behave | #3, #4 | integration | not started | — |
| 4 | OAuth environment-config safety | Prove Risk #2 cookie/auth config resolves per environment and the 401 branch holds | #2 | integration / config | not started | — |
| 5 | Frontend chart render safety | Prove Risk #6 the chart renders on empty/sparse/normal series without error | #6 | component | not started | — |

**Status vocabulary** (fixed — parser literals): `not started` → `change opened` → `researched` → `planned` → `implementing` → `complete`.

An AI-native / multimodal phase is deliberately omitted: a deterministic component test catches chart render errors more cheaply than a vision model, and chart styling is negative space (§7). Do not add a vision layer where a deterministic assertion already catches the regression.

## 4. Stack

The classic test base for this project. AI-native tools (if any) carry a
`checked:` date so future readers can see which lines need re-verification.

| Layer | Tool | Version | Notes |
|-------|------|---------|-------|
| unit + integration (backend) | xUnit | 2.9.3 | test project references the backend project; scoring already unit-tested |
| in-memory data | EF Core InMemory / Relational | 10.0.9 | already referenced in the test csproj for endpoint/query integration |
| time control | Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`) | 10.8.0 | for 429 backoff / token-refresh waits without real sleeps |
| coverage | coverlet.collector | 6.0.4 | backend coverage collection |
| unit + component (frontend) | Vitest via `@angular/build:unit-test` | 4.1.11 | `npm test` runs single-shot, non-watch; jsdom 30 present |
| e2e | none yet — deferred | — | no e2e runner installed; candidate future phase, not in this rollout |

**Stack grounding tools (current session):**
- Docs: none (no Context7 / framework-docs MCP exposed); `fetch_webpage` available for official docs if needed; checked: 2026-08-31
- Search: none (no Exa.ai / web-search MCP exposed); checked: 2026-08-31
- Runtime/browser: Playwright/browser tooling available as a possible future e2e layer — not used for this plan; checked: 2026-08-31
- Provider/platform: Azure DevOps MCP present but not relevant (CI is GitHub Actions); not used; checked: 2026-08-31

## 5. Quality Gates

The full set of gates that must pass before a change reaches production.
"Required after §3 Phase <N>" means the gate is enforced once that rollout
phase lands; before that, the gate is `planned`.

| Gate | Where | Required? | Catches |
|------|-------|-----------|---------|
| lint + format (`dotnet format`, `prettier`) | local + CI | recommended | syntactic / style drift |
| typecheck / compile (`dotnet build`, `tsc` strict) | local + CI | required | type drift, build breakage |
| unit + integration (`dotnet test`, `npm test`) | local + CI | required after §3 Phase 1 | logic regressions |
| e2e on critical flows | CI on PR | deferred (no runner yet) | broken critical user paths |
| post-edit hook | local (agent loop) | optional | regressions at edit time |
| multimodal visual review | CI on PR | optional (not planned — see §7) | visual issues classic diff misses |

CI already exists as GitHub Actions (`.github/workflows/backend-ci.yml`, `frontend-ci.yml`); gate wiring for the unit+integration gate lands with §3 Phase 1.

## 6. Cookbook Patterns

How to add new tests in this project. Each sub-section is filled in once
the relevant rollout phase ships; before that, the sub-section reads
"TBD — see §3 Phase <N>."

### 6.1 Adding a backend unit test (scoring)

- TBD — see §3 Phase 1 (realistic multi-workout scoring with independent oracles).

### 6.2 Adding a backend integration test (endpoint + user scoping)

Endpoint-level tests use `CustomWebApplicationFactory` (`strava-segments-performance-backend-tests/CustomWebApplicationFactory.cs`), which boots the real app in the `"Testing"` ASP.NET environment against a private EF Core InMemory database (one per factory instance) and a `TestAuthHandler` that authenticates a request as whichever Strava athlete id is sent in the `X-Test-Strava-Id` header — this exercises the endpoint's real claim→user resolution, not just the query/service layer underneath it.

Pattern: construct a fresh `CustomWebApplicationFactory` per test (`IDisposable`, no `IClassFixture`, so seeded data never bleeds across facts), seed via `factory.SeedAsync(db => ...)`, then call `factory.CreateClientAs(stravaAthleteId)` and hit the real route. For a no-leak assertion, seed **two** users with deliberately asymmetric data (different counts/values, not just different ids) so a dropped `UserId`/`StravaAthleteId` filter changes the response instead of coincidentally matching. See `strava-segments-performance-backend-tests/EndpointAuthorizationTests.cs` for the full pattern (fitness-trend no-leak + window filtering, fetch-status caller-only + no-row-falls-back-to-idle).

Note: `Program.cs` reads some configuration (`Frontend:Origin`, `Strava:ClientId/ClientSecret`, the connection string) directly off `builder.Configuration` *before* `builder.Build()` runs — earlier than `WebApplicationFactory`'s `ConfigureWebHost`/`ConfigureAppConfiguration` hooks take effect. `CustomWebApplicationFactory` supplies these via process environment variables (set in a static constructor) instead, since `AddEnvironmentVariables()` is one of `WebApplication.CreateBuilder`'s own default config sources and is visible immediately.

### 6.3 Adding a fetch-worker resilience test

- TBD — see §3 Phase 3 (429 backoff, token refresh, interrupt/resume with `FakeTimeProvider`).

### 6.4 Adding an OAuth / environment-config test

- TBD — see §3 Phase 4 (per-environment cookie policy, 401-vs-redirect branch).

### 6.5 Adding a frontend component test (chart render)

- TBD — see §3 Phase 5 (empty/sparse/normal series render without error; no styling assertions).

### 6.6 Per-rollout-phase notes

(Optional. After each phase lands, `/10x-implement` appends a 2–3 line note
here capturing anything surprising the rollout phase taught.)

## 7. What We Deliberately Don't Test

Exclusions agreed during the rollout (Phase 2 interview, Q5). Future
contributors should respect these unless the underlying assumption changes.

- **`/health` endpoint** — trivial, low signal. Re-evaluate if it grows real logic. (Source: Phase 2 interview Q5.)
- **Chart pixel styling / visual look** — a test may assert the chart renders without errors, but must not assert styling. Re-evaluate if a styling regression ever causes a real incident. (Source: Phase 2 interview Q5; reinforced by §3 Phase 5 scope.)
- **OAuth middleware library internals** — the auth code living inside the OAuth/ASP.NET library is the library's responsibility, not ours. Re-evaluate only if we fork or wrap it. (Source: Phase 2 interview Q5.)
- **Weather/surface conditions and other fitness platforms** — PRD Non-Goals; not in the product. (Source: PRD Non-Goals.)
- **Social features and user-configurable scoring params** — PRD Non-Goals. (Source: PRD Non-Goals.)

## 8. Freshness Ledger

- Strategy (§1–§5) last reviewed: 2026-08-31
- Stack versions last verified: 2026-08-31
- AI-native tool references last verified: 2026-08-31

Refresh (`/10x-test-plan --refresh`) when:

- a new top-3 risk surfaces from the roadmap or archive,
- a recommended tool's `checked:` date is older than three months,
- the project's tech stack changes (new framework, new test runner),
- §7 negative-space no longer matches what the team believes.
