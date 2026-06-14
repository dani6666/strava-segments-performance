<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Strava OAuth Login

- **Plan**: context/changes/strava-oauth-login/plan.md
- **Scope**: All Phases (1–4)
- **Date**: 2026-06-14
- **Verdict**: NEEDS ATTENTION
- **Findings**: 1 critical, 4 warnings, 2 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | WARNING |
| Scope Discipline | PASS |
| Safety & Quality | FAIL |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | PASS |

## Findings

### F1 — SameSite=None on auth cookie in production

- **Severity**: ❌ CRITICAL
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: Program.cs:35
- **Detail**: Plan specified SameSite=Lax unconditionally. Implementation sets Lax in dev but None in production. With BFF pattern, None is overly permissive and enables cross-site cookie sending.
- **Fix**: Change production SameSite from None to Lax.
  - Strength: Matches plan, eliminates cross-site attack surface.
  - Tradeoff: None for same-origin deployments.
  - Confidence: HIGH.
  - Blind spot: None significant.
- **Decision**: ACCEPTED — Backend and frontend are on different domains in production, so SameSite=None is correct for cross-origin API calls with withCredentials. The implementation is more correct than the plan.

### F2 — nginx $host vs $http_host inconsistency

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: nginx.conf:23
- **Detail**: /auth/ uses $http_host but /api/ uses $host, violating the recorded lesson requiring $http_host.
- **Fix**: Change $host to $http_host in /api/ block.
- **Decision**: FIXED

### F3 — No error handling in OnCreatingTicket handler

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: Program.cs:62
- **Detail**: long.Parse and SaveChangesAsync can throw, resulting in 500 instead of friendly redirect. However, exceptions from OnCreatingTicket propagate to OnRemoteFailure automatically, so the existing handler covers these failures.
- **Fix A ⭐ Recommended**: Wrap in try-catch with redirect.
- **Fix B**: Use TryParse guard only.
- **Decision**: ACCEPTED — OAuthCreatingTicketContext lacks HandleResponse(); exceptions propagate to OnRemoteFailure handler which already redirects to login?error=auth_failed.

### F4 — Auth guard fires network request on every navigation

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: auth.guard.ts:10
- **Detail**: Every navigation calls checkAuth() → HTTP GET /api/auth/me, even if user signal already holds data.
- **Fix**: Check signal first, API call only if null.
- **Decision**: FIXED

### F5 — Plaintext OAuth tokens in database

- **Severity**: ⚠️ WARNING
- **Impact**: 🔬 HIGH — architectural stakes; think carefully before deciding
- **Dimension**: Safety & Quality
- **Location**: Models/User.cs:8-9
- **Detail**: AccessToken and RefreshToken stored as plaintext. If DB compromised, tokens are immediately usable.
- **Fix**: AES-256-CBC encryption with key from env var (TokenEncryption:Key).
- **Decision**: FIXED — Added TokenEncryptionService with AES encryption, key stored in env var (same pattern as Strava secrets).

### F6 — Frontend logout() has no error handler

- **Severity**: 💡 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: auth.service.ts:29
- **Detail**: If POST /api/auth/logout fails, user stays on dashboard with stale state.
- **Fix**: Add error callback that clears user signal and redirects regardless.
- **Decision**: FIXED

### F7 — EF Core 9.x packages on .NET 10 target

- **Severity**: 💡 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: strava-segments-performance-backend.csproj:13-14
- **Detail**: Npgsql 9.0.4 and EF Design 9.0.4 on net10.0 target.
- **Fix**: Updated to Npgsql 10.0.2 and EF Design 10.0.9.
- **Decision**: FIXED
