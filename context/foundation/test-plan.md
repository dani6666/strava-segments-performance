# Test Plan

> Phased test rollout for this project. Strategy is frozen at the top
> (§1–§5); cookbook patterns at the bottom (§6) fill in as phases ship.
> Read before writing any new test.
>
> Refresh: re-run `/10x-test-plan --refresh` when stale (see §8).
>
> Last updated: 2026-09-02 (--refresh: Risk #2 / Phase 4 OAuth scope widened)

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
| 2 | **The Strava OAuth handshake round-trip fails to complete** — the challenge builds the wrong `redirect_uri` (scheme/host behind the proxy), the `/auth/callback` path isn't handled, code exchange or the ticket handler throws, or the post-callback redirect lands on the wrong endpoint — leaving the user stuck at a login error that blocks the entire app. Cookie SameSite/Secure divergence between dev and prod is one instance of this class. | High | High | **refresh 2026-09-02 user report** ("couldn't handle the callback / went to the wrong endpoint / many errors during this phase"); post-OAuth churn evidence — commits `0b9cca7 fixing https forwarding`, `4565b33 debuging key encryptuion`, `aff6089 removing debuging line`; interview Q2 (lived incident), Q3; PRD FR-001 |
| 3 | The **fetch worker mishandles a 429 or a mid-fetch interruption** — job dies, re-fetches cached data (risking Strava suspension), or leaves a stuck `Running` status blocking re-trigger | High | High | interview Q3, Q4; PRD FR-003 guardrail + AGENTS.md rate-limit rule; archive `workout-data-fetch/plan.md`; hot-spot dir `backend/Services/` |
| 4 | **Token refresh fails on the ~6h expiry during a long fetch**, aborting a multi-hour run | High | Medium | interview Q4; archive `workout-data-fetch/plan.md` (refresh first exercised in S-02); hot-spot dir `backend/Services/` |
| 5 | **One user's efforts leak into another's** fitness trend or fetch status — endpoint checks "logged in" but not "this data is yours" (IDOR) | High | Medium | PRD Access Control ("each sees only their own data"); interview Q4; archive `fitness-trend-chart/plan.md` (manual join, no user id on efforts); hot-spot `Program.cs` churn |
| 6 | The **trend chart errors or misrenders** on empty/sparse/large series — a user who fetched data sees a broken dashboard instead of their trend | Medium | Medium | interview Q4; hot-spot dir `frontend src/app/` (19 commits/30d, dashboard); archive `fitness-trend-chart/plan.md` |
| 7 | The **authenticated date-range → analysis → chart vertical slice breaks at a layer seam** — a logged-in user picks a date range and the trend fails to render, because the API response shape, the date-range threading, or the frontend consumption diverges even though each layer passes its own isolated test | High | Medium | **refresh 2026-09-02 user report** ("the amount of data calculated and shown on the chart can lead to many errors… some layer would fail"); the slice spans fetch→score→aggregate→API→render (many seams); hot-spot dir `frontend src/app/` (19 commits/30d); archive `timeframe-selection/plan.md` (date range threaded through the pipeline), `fitness-trend-chart/plan.md` |

Order: protect High × High first (#1, #2, #3), then High × Medium (#4, #5, #7), then Medium × Medium (#6). Risk #7 is a composed-path (seam) risk: the individual layers are covered by Phases 1, 2, and 6, but only an end-to-end happy path (Phase 5) proves they compose.

**Abuse / security lens.** The product has auth and per-user data, so the map includes abuse scenarios: #5 covers authorization/access (IDOR — endpoints must verify ownership, not just authentication) and #3 covers resource abuse (rate-limit / redundant re-fetch). Secret leakage was reviewed — tokens are stored encrypted — and untrusted-input parity is partly guarded by an existing fetch-window validator; neither warranted an additional row for v1.

### Risk Response Guidance

| Risk | What would prove protection | Must challenge | Context `/10x-research` must ground | Likely cheapest layer | Anti-pattern to avoid |
|------|-----------------------------|----------------|--------------------------------------|-----------------------|-----------------------|
| #1 | Same segment time at lower HR scores higher; best-in-window → ~100, worst → ~0; a realistic multi-workout fixture matches an **independently hand-computed** expectation | "The expected score came from running the scorer" (oracle problem — tautological) | Locked pipeline stages, window semantics, tie/stall rules, min-scored-efforts gate | unit (pure scorer) | expected value lifted from the implementation instead of from requirements |
| #2 | The full handshake completes end-to-end against a **stubbed** Strava: `/api/auth/login` challenges with a correct `redirect_uri`+scope+state; a callback to `/auth/callback` is handled, exchanges the code, runs the ticket handler (user created), sets the cookie, and 302s to `{frontendOrigin}/dashboard`; a failed exchange 302s to `/login?error=auth_failed`. Cookie SameSite/Secure resolves per environment and `/api/*` returns 401 (not a redirect) when unauthenticated. Deployed: the real login 302 `Location` is `https://…/auth/callback`, not `http://localhost`. | "Auth works in dev implies it works in prod"; "the callback path is fine because the happy exchange returned 200" — the wiring (path registration, redirect_uri from forwarded headers, redirect targets) is the thing that broke, not the token math | `CallbackPath` route registration; how `redirect_uri` is built from forwarded headers behind the proxy; the `OnCreatingTicket` user-creation and both redirect targets (success `/dashboard`, failure `/login?error=auth_failed`); the 401-vs-redirect branch; how to point the OAuth handler at a stub authorize/token server in tests | integration round-trip (`WebApplicationFactory` + stub authorize/token endpoints) for the wiring; **browser e2e (Playwright, stub provider via route interception — never real Strava)** for the redirect chain through Angular; deploy-time smoke (one curl on `/api/auth/login`) for the real redirect_uri | driving **real Strava** login in the browser (third-party UI, CAPTCHA, burns rate limits — AGENTS.md); asserting only cookie flags while leaving the callback round-trip untested; mocking away the forwarded-header/proxy difference that caused the prod bug |
| #3 | A 429 triggers a bounded wait + retry (not a failure); an interrupted run resets `Running` → `Interrupted` and re-trigger resumes without re-fetching cached activities | "Final status 200 means the retry logic worked" | 429/`Retry-After` handling, startup status reset, idempotent re-list/diff | integration w/ injected clock (`FakeTimeProvider`) | real sleeps; happy-path-only; asserting no re-fetch from logs instead of state |
| #4 | An expired access token triggers refresh and the fetch continues without user action | "The token was fine because the call succeeded" | refresh trigger condition, clock injection | integration w/ injected clock | real time waits; testing refresh in isolation from the fetch loop |
| #5 | `fitness-trend` and `fetch-status` return **only** the calling user's data across a two-user seed | "Logged-in implies scoped" | user resolution + the manual effort→activity join | integration (in-memory / SQLite `AppDbContext`) | single-user fixture that cannot reveal a leak |
| #6 | The chart component renders without error on empty, sparse, and normal series; an empty series shows the empty state | "Rendering equals correct"; styling is not the target | series → chart-config mapping, empty-state branch | Vitest component test | snapshot/styling assertions; testing pixel look (explicit negative space) |
| #7 | An authenticated user picks a date range with seeded data and the trend chart renders a non-empty series end-to-end — the real analysis API response actually drives the real chart over HTTP | "Each layer's own test passing means the composed path works" (the seams are the risk, not the layers); "assert the chart looks right" (only render-succeeded + expected point count, not pixels) | the date-range param threading from picker → API → response shape → chart input; a login path a browser test can complete without real Strava; a seeded fixture that yields a known, non-empty trend | **one** browser e2e (Playwright, stub-provider login, seeded data) — runner already installed by Phase 4 | an e2e matrix of chart states (edge series belong in the #6 component test); styling/pixel assertions; asserting the API and the chart separately instead of the composed round-trip |

## 3. Phased Rollout

Each row is a discrete rollout phase that will open its own change folder
via `/10x-new`. Status moves left-to-right through the values below; the
orchestrator updates Status as artifacts appear on disk.

| # | Phase name | Goal (one line) | Risks covered | Test types | Status | Change folder |
|---|---|---|---|---|---|---|
| 1 | Scoring realistic-data coverage | Prove Risk #1 scores hold on realistic multi-workout data against independent oracles | #1 | unit | complete | context/archive/2026-09-01-testing-scoring-coverage/ |
| 2 | Authorization + endpoint data-path | Prove Risk #5 no cross-user leak and window filtering is correct | #5 | integration | not started | — |
| 3 | Fetch worker resilience | Prove Risk #3/#4 429 backoff, token refresh, and interrupt/resume behave | #3, #4 | integration | not started | — |
| 4 | OAuth handshake round-trip + config safety | Prove Risk #2 the full challenge→callback→cookie→redirect round-trip completes against a stubbed Strava (success + failure branches, redirect_uri from forwarded headers), cookie/auth config resolves per environment, and the 401 branch holds | #2 | integration / config + browser e2e (stub provider) + deploy smoke | planned | context/changes/testing-oauth-roundtrip/ |
| 5 | Vertical-slice happy-path e2e | Prove Risk #7 an authenticated date-range → analysis → chart happy path renders end-to-end (browser e2e, reusing the Phase 4 Playwright runner) | #7 | browser e2e (stub-provider login, seeded data) | not started | — |
| 6 | Chart render safety (component) | Prove Risk #6 the chart renders on empty/sparse/normal series without error | #6 | component | not started | — |

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
| e2e (frontend) | Playwright | latest 1.61.x (candidate — confirm/pin in Phase 4 research) | no runner installed yet; **§3 Phase 4 installs it** for the OAuth redirect-chain smoke and **Phase 5 reuses it** for the date-range→chart happy path. Drives a stubbed provider via `page.route` interception + `storageState` reuse — never real Strava (rate limits, AGENTS.md). Protractor is retired; Playwright is the current Angular e2e choice |

**Stack grounding tools (current session):**
- Docs: Context7 MCP exposed — used to ground the Playwright e2e recommendation (`/websites/playwright_dev`: `page.route` provider stubbing + `storageState` reuse); checked: 2026-09-02. (Prior session had no docs MCP; checked: 2026-08-31)
- Search: none (no Exa.ai / web-search MCP exposed); checked: 2026-08-31
- Runtime/browser: Playwright now in scope as the §3 Phase 4 e2e layer for the OAuth redirect chain (stub provider only); checked: 2026-09-02
- Provider/platform: Azure DevOps MCP present but not relevant (CI is GitHub Actions); not used; checked: 2026-08-31

## 5. Quality Gates

The full set of gates that must pass before a change reaches production.
"Required after §3 Phase <N>" means the gate is enforced once that rollout
phase lands; before that, the gate is `planned`.

| Gate | Where | Required? | Catches |
|------|-------|-----------|---------|
| lint + format (`dotnet format`, `prettier`) | local + CI | recommended | syntactic / style drift |
| typecheck / compile (`dotnet build`, `tsc` strict) | local + CI | required | type drift, build breakage |
| unit + integration (`dotnet test`, `npm test`) | local + CI | required | logic regressions |
| e2e — critical flows (Playwright, stub provider): OAuth redirect chain + date-range→chart happy path | CI on PR (`.github/workflows/e2e-ci.yml`) | OAuth required after §3 Phase 4; chart happy path required after §3 Phase 5 | broken login round-trip and broken composed data→chart slice through the browser |
| deploy-time OAuth redirect smoke (curl `/api/auth/login`, assert `Location` is `https://…/auth/callback`) | post-deploy (CD or manual) | required after §3 Phase 4 | prod-only `redirect_uri`/forwarded-proto misconfig no offline test can catch |
| post-edit hook | local (agent loop) | optional | regressions at edit time |
| multimodal visual review | CI on PR | optional (not planned — see §7) | visual issues classic diff misses |

CI already exists as GitHub Actions (`.github/workflows/backend-ci.yml`, `frontend-ci.yml`); the unit+integration gate is now wired — `backend-ci.yml` runs `dotnet test` in a `test` job that gates `build-and-deploy` via `needs:`.

## 6. Cookbook Patterns

How to add new tests in this project. Each sub-section is filled in once
the relevant rollout phase ships; before that, the sub-section reads
"TBD — see §3 Phase <N>."

### 6.1 Adding a backend unit test (scoring)

- **Location**: `strava-segments-performance-backend-tests/FitnessScoringTests.cs`. `FitnessScoring.Score` is a pure, dependency-free static function — no DbContext, clock, or mocks needed; this is unit-only.
- **Fixture style**: inline positional `SegmentEffortRecord[]` with trailing `// comment` intent, matching the existing tests. No shared builder — keep new fixtures inline unless the file's density genuinely demands one (and if it does, name any helper descriptively per `lessons.md`, never a single letter).
- **Gates every fixture must clear**: a segment needs ≥ 2 survivors after the 2×-median stall drop or it contributes nothing; a workout needs ≥ 3 scored efforts across its segments or it is absent from the trend entirely.
- **Oracle discipline**: expected values (or expected ordering/bands) must be derived by hand from the formula and the requirements — never read back from running the scorer. That tautology is the anti-pattern this test-plan explicitly forbids for Risk #1.
- **Reference tests**: `Score_LongSegmentOutweighsShortSegmentInAggregation` (exact-rational oracle, `10000.0/109.0`) for pinning exact magnitudes; `Score_RealisticMultiWeekImprovingFitness_ProducesRisingTrend` for a lifelike multi-week fixture asserted ordinally + by coarse band (the realistic-shape pattern to follow when an exact oracle would be too laborious to hand-derive).
- **Run command**: `dotnet test strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj --filter "FullyQualifiedName~FitnessScoringTests"` (or drop the filter for the full suite).


### 6.2 Adding a backend integration test (endpoint + user scoping)

- TBD — see §3 Phase 2 (in-memory `AppDbContext`, two-user seed, no-leak assertion).

### 6.3 Adding a fetch-worker resilience test

- TBD — see §3 Phase 3 (429 backoff, token refresh, interrupt/resume with `FakeTimeProvider`).

### 6.4 Adding an OAuth handshake / environment-config test

- TBD — see §3 Phase 4. Three layers to fill in when the phase ships:
  1. **Integration round-trip** (`WebApplicationFactory` + a stub authorize/token server): assert the login challenge's `redirect_uri`/scope/state, that `/auth/callback` is handled, code→ticket→cookie→302 to `/dashboard`, and the failed-exchange 302 to `/login?error=auth_failed`; plus per-environment cookie policy and the 401-vs-redirect branch.
  2. **Browser e2e** (Playwright, stub provider via `page.route` — never real Strava): the redirect chain completes through Angular and lands authenticated on `/dashboard`.
  3. **Deploy smoke** (curl `/api/auth/login`, assert the 302 `Location` scheme+host+path).

- **Auth seam for *other* e2e tests (`/auth/test-login`).** Tests that need an authenticated session but do **not** test login (Phase 5 chart happy path, the seed) must not drive the OAuth handshake — `page.route` cannot stub the server-side code→token exchange (the backend, not the browser, calls Strava's token endpoint). Instead the backend exposes `GET /auth/test-login?athleteId&name`, **gated to `ASPNETCORE_ENVIRONMENT=E2E`** (not mapped in Development/Production), which upserts the user and `SignInAsync`s a real cookie session — no Strava. Playwright's `setup` project calls it once and saves `storageState` (`playwright/.auth/user.json`, gitignored, regenerated every run); the `chromium` project reuses it via `dependencies: ['setup']`. This keeps the never-real-Strava rule while giving authenticated tests a genuine backend session (real `/api/*`, not a stubbed `/api/auth/me`). The **Phase 4 handshake test itself** must NOT use this seam — it starts unauthenticated and exercises the real challenge→callback→cookie→redirect chain.

### 6.5 Adding a frontend chart test (component + vertical-slice e2e)

Two layers, now split across two phases:

- **Happy-path e2e** — TBD, see §3 Phase 5 (Playwright, runner from Phase 4): stub-provider login → pick a seeded date range → assert the trend chart renders a non-empty series end-to-end. One path only — deterministic assertions (render-succeeded + expected point count), not a state matrix and not pixels.
- **Component test** — TBD, see §3 Phase 6 (Vitest): empty/sparse/normal series render without error; empty series shows the empty state; no styling/pixel assertions.

### 6.6 Per-rollout-phase notes

(Optional. After each phase lands, `/10x-implement` appends a 2–3 line note
here capturing anything surprising the rollout phase taught.)

- **Testing scoring coverage (§3 Phase 1/3, Risk #1).** Two complementary fixtures, not one: a synthetic exact-shape fixture pins the formula, and a frozen (never live-fetched) real-activity fixture pins reality via ordinal assertions from the user's own ground-truth ranking. Freeze real data into the inline fixture at authoring time — anonymize raw Strava ids, no network calls in the test — the same offline/deterministic discipline as every other unit test in `FitnessScoringTests.cs`.

## 7. What We Deliberately Don't Test

Exclusions agreed during the rollout (Phase 2 interview, Q5). Future
contributors should respect these unless the underlying assumption changes.

- **`/health` endpoint** — trivial, low signal. Re-evaluate if it grows real logic. (Source: Phase 2 interview Q5.)
- **Chart pixel styling / visual look** — a test may assert the chart renders without errors, but must not assert styling. Re-evaluate if a styling regression ever causes a real incident. (Source: Phase 2 interview Q5; reinforced by §3 Phase 5 e2e and Phase 6 component scope — both assert render-succeeded, neither asserts pixels.)
- **OAuth middleware library internals** — the auth code living inside the OAuth/ASP.NET library is the library's responsibility, not ours. Re-evaluate only if we fork or wrap it. (Source: Phase 2 interview Q5.)
- **Weather/surface conditions and other fitness platforms** — PRD Non-Goals; not in the product. (Source: PRD Non-Goals.)
- **Social features and user-configurable scoring params** — PRD Non-Goals. (Source: PRD Non-Goals.)

## 8. Freshness Ledger

- Strategy (§1–§5) last reviewed: 2026-08-31
- Stack versions last verified: 2026-08-31 (Playwright e2e added 2026-09-02, grounded via Context7)
- AI-native tool references last verified: 2026-08-31
- 2026-09-02 `--refresh`: Risk #2 widened from "cookie config" to the full OAuth handshake round-trip after a user report of callback/endpoint failures (corroborated by `0b9cca7 fixing https forwarding` + debugging commits). Phase 4 now spans integration round-trip + browser e2e (stub provider) + deploy smoke; §5 promotes the e2e gate and adds the deploy-smoke gate.
- 2026-09-02 `--refresh` (2nd finding): added Risk #7 (authenticated date-range→analysis→chart vertical slice fails at a layer seam) from a user report that the composed data/chart path is high-risk, covered by one happy-path browser e2e reusing the Phase 4 Playwright runner. At user request the old combined chart phase was split into two single-risk phases: Phase 5 (Risk #7, e2e) ordered ahead of Phase 6 (Risk #6, component) by priority (H×M before M×M). Edge-series matrix stays in the Phase 6 component test; styling stays negative space (§7).

Refresh (`/10x-test-plan --refresh`) when:

- a new top-3 risk surfaces from the roadmap or archive,
- a recommended tool's `checked:` date is older than three months,
- the project's tech stack changes (new framework, new test runner),
- §7 negative-space no longer matches what the team believes.
