# Authorization + Endpoint Data-Path Tests Implementation Plan

## Overview

Phase 2 of the test rollout (`context/foundation/test-plan.md` §3) proves **Risk #5**: one user's efforts or fetch status must never leak into another user's response. The two authenticated read endpoints — `/api/analysis/fitness-trend` and `/api/workouts/fetch-status` — resolve the caller from a claim (`stravaId`) to a `user.Id` and then query by that id. Today only the *query helper* (`FitnessTrendQuery`) is tested with a two-user seed; the endpoint's **claim→user resolution seam** (where an IDOR would actually live) and the **entire fetch-status endpoint** are untested.

This change adds an endpoint-level integration test harness (`WebApplicationFactory` + a fake auth handler + EF Core InMemory) and writes cross-user-leak and window-filtering tests that drive the real HTTP routes.

## Current State Analysis

- **Endpoints under test** live in `strava-segments-performance-backend/Program.cs`:
  - `/api/analysis/fitness-trend` (line 256) — resolves `stravaId` → `user`, calls `FitnessTrendQuery.GetForUserAsync(db, user.Id, from, to)`, returns `IReadOnlyList<FitnessTrendPoint>`.
  - `/api/workouts/fetch-status` (line 247) — resolves `stravaId` → `user`, returns the caller's `WorkoutFetchStatus` (or an `Idle` DTO). Logic is inline; **no test exists**.
- **Already covered (query layer):** `strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs` seeds two users and asserts `GetForUserAsync` returns only user 1's workouts, plus `from`/`to` window narrowing. It calls the helper directly with a hard-coded `userId` — it never exercises the endpoint's claim→user resolution.
- **Harness gaps:**
  - The test csproj (`strava-segments-performance-backend-tests.csproj`) references EF Core InMemory and xUnit but **not** `Microsoft.AspNetCore.Mvc.Testing`.
  - `Program.cs` uses top-level statements with **no `public partial class Program`**, so `WebApplicationFactory<Program>` cannot reference the entry point from the test assembly.
  - `Program.cs` (lines 118–126) calls `db.Database.MigrateAsync()` and an `ExecuteUpdateAsync` stale-status reset at startup — both Npgsql/relational-specific and will throw under an EF InMemory test host.
- **Config coupling:** startup reads `Frontend:Origin` (used in `AddCors().WithOrigins(...)` — throws on null), `ConnectionString:DefaultConnection`, and `Strava:ClientId`/`ClientSecret`. The test host must supply dummy values for these.
- **Serialization:** minimal APIs use web JSON defaults (camelCase). `FitnessTrendPoint(DateTime Date, double Score)` → `{ "date", "score" }`. The fetch-status DTO already uses lowercase keys (`status`, `stage`, `activitiesProcessed`, `totalToProcess`, `errorMessage`).

## Desired End State

`dotnet test` runs a new endpoint-level integration test class that boots the app in the `Testing` environment against EF Core InMemory, authenticates each request as a chosen Strava athlete via a fake auth handler, and asserts:

1. `/api/analysis/fitness-trend` returns only the calling user's trend points across a two-user seed.
2. `/api/analysis/fitness-trend` honours `from`/`to` window narrowing through the endpoint.
3. `/api/workouts/fetch-status` returns only the caller's status and never surfaces another user's in-progress status.

Production startup behaviour is unchanged outside the `Testing` environment. The test-plan §6.2 cookbook documents how to add such a test.

### Key Discoveries:

- The IDOR seam is the claim→user resolution in the endpoints (`Program.cs:258-259`, `249-250`), not the query helper — so tests must go through HTTP, not call `FitnessTrendQuery` directly.
- `WorkoutFetchStatus` is keyed by `UserId` (`AppDbContext.cs:29-34`), so a two-user fetch-status seed is a single row per user.
- The existing two-user fitness seed shape in `FitnessTrendQueryTests.cs:18-40` (repeated segments so each activity clears the min-3-scored-efforts bar) is directly reusable for the endpoint test.
- EF InMemory override + `EnsureCreated` matches the provider already in the test csproj — no new DB dependency needed.

## What We're NOT Doing

- **No unauthenticated / 401 assertions** — the route-protection and cookie/env-config behaviour is Phase 4 (OAuth environment-config safety). Phase 2 uses authenticated callers only.
- **No POST `/api/workouts/fetch` coverage** — its channel + hosted-worker write path belongs to Phase 3 (fetch-worker resilience).
- **No SQLite / relational provider** — the risk is app-level scoping, not SQL semantics.
- **No changes to scoring logic or query logic** — this is a test-only change plus a minimal startup guard.
- **No re-testing of the query-helper scoping** already covered by `FitnessTrendQueryTests`.

## Implementation Approach

Two phases. Phase 1 stands up a reusable endpoint-test harness and proves it boots with a single smoke test. Phase 2 writes the actual authorization/data-path assertions on top of that harness and fills in the cookbook. Splitting this way means the harness plumbing (the risky, fiddly part) is verified in isolation before the substantive tests are layered on.

The fake auth handler reads the acting athlete's `stravaId` from a request header (absent header → no authenticated user), builds a `ClaimsPrincipal` with a `NameIdentifier` claim, and is registered as the default authenticate scheme so `RequireAuthorization()` resolves it. Each test seeds `User` rows whose `StravaAthleteId` matches the header value it sends.

## Critical Implementation Details

- **Startup guard placement** — the `MigrateAsync` + stale-status `ExecuteUpdateAsync` block (`Program.cs:118-126`) must be skipped when `app.Environment.IsEnvironment("Testing")`; the factory sets `UseEnvironment("Testing")`. Nothing else in startup is relational-eager, but the CORS `WithOrigins` call requires `Frontend:Origin` to be present, so the factory must inject test configuration before the host builds.
- **Default auth scheme override** — the app registers Cookie as the default scheme; the factory must re-point the default authenticate/challenge scheme at the test scheme (via `AddAuthentication` in `ConfigureTestServices`) so the endpoints' `RequireAuthorization()` authenticates through the fake handler rather than the cookie handler.
- **DbContext override ordering** — remove the existing `DbContextOptions<AppDbContext>` (and `AppDbContext`) service descriptors before adding the InMemory registration; use a fixed in-memory database name per factory instance so seed + request share one store. Seed inside a created scope after the host is built.

## Phase 1: Integration test harness

### Overview

Make the backend entry point testable, guard the Postgres-coupled startup, add the ASP.NET testing package, and build a reusable `CustomWebApplicationFactory` with InMemory override, test config, a fake auth handler, and a seed hook. Prove it with one smoke test.

### Changes Required:

#### 1. Make `Program` referenceable from tests

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Expose the top-level-statement entry point so `WebApplicationFactory<Program>` can boot it.

**Contract**: Append `public partial class Program { }` at the end of the file (after the existing `record FetchWorkoutsRequest(...)`). No behavioural change.

#### 2. Guard the Postgres-coupled startup block

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Skip the relational migration + stale-status reset when running under the test host, so the app boots against EF InMemory.

**Contract**: Wrap the `using (var scope = app.Services.CreateScope()) { ... MigrateAsync ... ExecuteUpdateAsync ... }` block (lines 118–126) so it only runs when `!app.Environment.IsEnvironment("Testing")`. Production and Development behaviour unchanged.

#### 3. Add the ASP.NET integration-testing package

**File**: `strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`

**Intent**: Provide `WebApplicationFactory`.

**Contract**: Add `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.*" />` (pin to the installed 10.x patch matching the other 10.0.x refs). No other csproj changes.

#### 4. Fake authentication handler

**File**: `strava-segments-performance-backend-tests/TestAuthHandler.cs` (new)

**Intent**: Authenticate each test request as a chosen Strava athlete by injecting a `NameIdentifier` claim from a request header, so the endpoints' claim→user resolution runs for real.

**Contract**: An `AuthenticationHandler<AuthenticationSchemeOptions>` (scheme name e.g. `"Test"`). On `HandleAuthenticateAsync`: if the configured header (e.g. `X-Test-Strava-Id`) is present, return `AuthenticateResult.Success` with a `ClaimsPrincipal` carrying `new Claim(ClaimTypes.NameIdentifier, headerValue)`; otherwise `AuthenticateResult.NoResult()`. Expose the header name as a `const`.

#### 5. Custom web application factory

**File**: `strava-segments-performance-backend-tests/CustomWebApplicationFactory.cs` (new)

**Intent**: Boot the real app in `Testing` mode against EF InMemory with test config and the fake auth scheme, and provide a seed helper.

**Contract**: `class CustomWebApplicationFactory : WebApplicationFactory<Program>`.
- `UseEnvironment("Testing")`.
- `ConfigureAppConfiguration`: inject in-memory settings for `ConnectionStrings:DefaultConnection`, `Frontend:Origin` (a valid dummy URL), `Strava:ClientId`, `Strava:ClientSecret` (dummy non-empty).
- `ConfigureTestServices`: remove the `DbContextOptions<AppDbContext>` (+ `AppDbContext`) descriptors; add `AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(<fixed name>))`; register `AddAuthentication` defaulting to the `"Test"` scheme with `TestAuthHandler`.
- A `SeedAsync(Action<AppDbContext>)` (or `Seed(...)`) helper that creates a scope, resolves `AppDbContext`, calls `EnsureCreated`, applies the caller's seed, and saves.
- A helper to build an `HttpClient` that sets the `X-Test-Strava-Id` header for a given athlete id.

#### 6. Harness smoke test

**File**: `strava-segments-performance-backend-tests/EndpointAuthorizationTests.cs` (new — smoke test added here, full suite in Phase 2)

**Intent**: Prove the host boots and an authorized request reaches a read endpoint.

**Contract**: One `[Fact]` that seeds a single user, issues an authenticated GET to `/api/workouts/fetch-status` as that athlete, and asserts `200 OK`. Uses `IClassFixture<CustomWebApplicationFactory>`.

### Success Criteria:

#### Automated Verification:

- Backend builds: `dotnet build strava-segments-performance-backend/strava-segments-performance-backend.csproj`
- Test project builds: `dotnet build strava-segments-performance-backend-tests/strava-segments-performance-backend-tests.csproj`
- The harness smoke test passes: `dotnet test strava-segments-performance-backend-tests --filter FullyQualifiedName~EndpointAuthorizationTests`
- Existing tests still pass: `dotnet test strava-segments-performance-backend-tests`

#### Manual Verification:

- Running the backend normally (`dotnet run`) still applies migrations and resets stale statuses (startup guard only affects the `Testing` environment).

**Implementation Note**: After completing this phase and all automated verification passes, pause for manual confirmation that normal startup is unaffected before proceeding to Phase 2.

---

## Phase 2: Authorization + data-path tests

### Overview

Write the substantive cross-user-leak and window-filtering tests on the harness, then document the pattern in the test-plan cookbook.

### Changes Required:

#### 1. Cross-user leak + window tests

**File**: `strava-segments-performance-backend-tests/EndpointAuthorizationTests.cs`

**Intent**: Assert the two read endpoints scope strictly to the calling user and that fitness-trend's window filtering holds through HTTP.

**Contract**: Add these `[Fact]`s (each seeds via the factory helper, requests as a specific athlete, deserializes the JSON response):
- **fitness-trend no-leak** — seed two users (user 1 athlete id A, user 2 athlete id B) with the repeated-segment shape from `FitnessTrendQueryTests.cs:18-40`. As athlete A, GET `/api/analysis/fitness-trend`; assert every returned point's date belongs to user 1's activities and the count matches user 1 only. Repeat as athlete B to confirm the mirror.
- **fitness-trend window filtering** — single user seeded across three dates; GET with `?from=` narrowing the window; assert reduced point count and that the excluded date is absent (mirrors `FitnessTrendQueryTests.cs:76-87` but through the endpoint).
- **fetch-status caller-only** — seed user 1 with a `Running` status (distinctive counts) and user 2 with a `Completed` status. As athlete A, GET `/api/workouts/fetch-status`; assert the DTO reflects user 1's status/counts and does **not** match user 2's. As athlete B, assert the mirror.
- **fetch-status no-row** — an authenticated athlete with no `WorkoutFetchStatus` row gets the `idle` DTO (confirms the fallback path is still caller-scoped).

Deserialize fitness-trend into a small `record TrendPoint(DateTime Date, double Score)` list and fetch-status into a record/`JsonDocument` matching the lowercase DTO keys.

#### 2. Fill in the cookbook

**File**: `context/foundation/test-plan.md`

**Intent**: Replace the §6.2 "TBD" with the concrete pattern this phase established.

**Contract**: Rewrite §6.2 ("Adding a backend integration test (endpoint + user scoping)") to describe: use `CustomWebApplicationFactory` + `TestAuthHandler` (set `X-Test-Strava-Id`), seed via the factory helper, two-user seed to reveal leaks, assert on deserialized JSON. Keep it to a short paragraph + the file references. Do not alter §3 status rows (the orchestrator owns those).

### Success Criteria:

#### Automated Verification:

- All backend tests pass: `dotnet test strava-segments-performance-backend-tests`
- The new authorization tests are present and green: `dotnet test strava-segments-performance-backend-tests --filter FullyQualifiedName~EndpointAuthorizationTests`

#### Manual Verification:

- Skim the two-user assertions to confirm they would actually fail if the endpoint dropped its `UserId`/`StravaAthleteId` filter (i.e. the seed genuinely contains the other user's data on the same dates/segments).

**Implementation Note**: After completing this phase and all automated verification passes, pause for manual confirmation before considering the rollout phase complete.

---

## Testing Strategy

### Integration Tests:

- Endpoint-level via `WebApplicationFactory<Program>` in the `Testing` environment against EF Core InMemory.
- Fake auth handler injects the acting athlete's `stravaId` claim so the real claim→user resolution executes.
- Two-user seeds for both read endpoints; single-user multi-date seed for window filtering.

### Manual Testing Steps:

1. `dotnet run` the backend and confirm migrations still apply (startup guard is `Testing`-only).
2. Read the two-user assertions and confirm the seed places the *other* user's data where a broken filter would surface it.

## References

- Test plan (Phase 2 row): `context/foundation/test-plan.md` §2 (Risk #5), §3, §6.2
- Endpoints under test: `strava-segments-performance-backend/Program.cs:247-263`
- Existing query-layer coverage / seed shape: `strava-segments-performance-backend-tests/FitnessTrendQueryTests.cs`
- DTO: `strava-segments-performance-backend/Services/FitnessScoring.cs:10`
- Startup block to guard: `strava-segments-performance-backend/Program.cs:118-126`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Integration test harness

#### Automated

- [x] 1.1 Backend builds — 0bc1b1b
- [x] 1.2 Test project builds — 0bc1b1b
- [x] 1.3 Harness smoke test passes — 0bc1b1b
- [x] 1.4 Existing tests still pass — 0bc1b1b

#### Manual

- [ ] 1.5 Normal `dotnet run` startup still migrates + resets stale statuses

### Phase 2: Authorization + data-path tests

#### Automated

- [x] 2.1 All backend tests pass — 1e8ab24
- [x] 2.2 New authorization tests present and green — 1e8ab24

#### Manual

- [ ] 2.3 Two-user assertions confirmed to fail if the endpoint dropped its user filter
