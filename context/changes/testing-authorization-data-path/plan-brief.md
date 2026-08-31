# Authorization + Endpoint Data-Path Tests — Plan Brief

> Full plan: `context/changes/testing-authorization-data-path/plan.md`

## What & Why

Phase 2 of the test rollout proves **Risk #5**: one user's efforts or fetch status must never leak into another user's response. The two authenticated read endpoints (`/api/analysis/fitness-trend`, `/api/workouts/fetch-status`) resolve the caller from a `stravaId` claim to a `user.Id` and query by it — but that claim→user resolution seam (where an IDOR would live) is untested, and the fetch-status endpoint has no test at all. This change adds endpoint-level integration tests that drive the real HTTP routes as different authenticated users.

## Starting Point

- Query-layer scoping/window filtering for fitness-trend is already covered by `FitnessTrendQueryTests` (calls the helper with a hard-coded `userId` — never the endpoint).
- The test csproj lacks `Microsoft.AspNetCore.Mvc.Testing`; `Program.cs` has no `public partial class Program`; startup runs `MigrateAsync` + a stale-status reset that are Npgsql-specific and throw under an in-memory test host.

## Key Decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Test layer | HTTP endpoint via `WebApplicationFactory` + fake auth handler | Covers the real claim→user resolution seam, not just the query helper |
| DB provider | EF Core InMemory (override registration, `EnsureCreated`) | Matches the provider already in the test csproj; no new dependency |
| Startup coupling | Env-guard `MigrateAsync` + stale reset behind `"Testing"` | Tiny production edit; keeps prod startup identical |
| Scope | Both read endpoints (fitness-trend no-leak + window; fetch-status caller-only) | Exactly Risk #5's two named endpoints, incl. the untested fetch-status |
| Unauthenticated 401 | Deferred to Phase 4 | Keeps Phase 2 strictly about cross-user leak; 401/cookie config is the OAuth phase |

## Phases

| # | Phase | Deliverable | Primary risk |
|---|-------|-------------|--------------|
| 1 | Integration test harness | `public partial class Program`, env-guarded startup, Mvc.Testing package, `CustomWebApplicationFactory` (InMemory + test config + fake auth + seed helper), one smoke test | Harness plumbing (auth scheme override, InMemory swap, config injection) |
| 2 | Authorization + data-path tests | Two-user leak tests for both endpoints + endpoint window-filtering test; fill test-plan §6.2 cookbook | Seeds must genuinely place the other user's data where a broken filter would surface it |

**Prerequisites:** none beyond the existing backend + test project.
**Estimated effort:** ~1 session across 2 phases.

## Open Risks & Assumptions

- Overriding the default auth scheme to the test handler is assumed to make `RequireAuthorization()` authenticate through it — verify in the Phase 1 smoke test.
- `Microsoft.AspNetCore.Mvc.Testing` 10.0.x is assumed available on the feed matching the other 10.0.x refs.
- Booting the full app starts the `WorkoutFetchWorker` hosted service; it idles on an empty channel and is not expected to interfere.

## Success Criteria (Summary)

- `dotnet test` runs endpoint tests proving each read endpoint returns only the calling user's data across a two-user seed.
- fitness-trend `from`/`to` window filtering holds when driven through the endpoint.
- Production startup behaviour is unchanged outside the `Testing` environment.
