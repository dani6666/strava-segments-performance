# Strava OAuth Login — Plan Brief

> Full plan: `context/changes/strava-oauth-login/plan.md`
> Research: `context/changes/strava-oauth-login/research.md`

## What & Why

Implement Strava OAuth login using the BFF pattern so cyclists can authenticate and access the app. This is S-01 on the roadmap — the gate for all downstream slices (workout fetching, fitness scoring). The backend owns all tokens; the frontend never touches OAuth.

## Starting Point

Both projects are greenfield scaffolds: the backend has a `/health` endpoint and OpenAPI, the frontend has empty routes and no components. Docker Compose already defines PostgreSQL, the connection string, and Strava env var placeholders — no infrastructure changes needed for the database.

## Desired End State

An unauthenticated user sees a login page with "Connect with Strava." After authorizing on Strava, they land on `/dashboard` showing their Strava name and an empty state. Sessions persist across page refreshes and server restarts (tokens stored in PostgreSQL). OAuth failures redirect to the login page with an error message.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Token persistence | PostgreSQL (Docker container) | Survives server restarts; reuses the DB container docker-compose already defines. | Plan |
| Database engine | PostgreSQL 16 | Already in docker-compose; excellent .NET/EF Core support via Npgsql. | Plan |
| OAuth library | `AspNet.Security.OAuth.Strava` v10.0 | aspnet-contrib provider that plugs into standard ASP.NET Core auth pipeline. | Research |
| Auth architecture | BFF — backend owns all tokens | Keeps tokens server-side only; Angular never touches OAuth. | Research |
| Login UX | Dedicated `/login` route with Strava button | Clean, conventional OAuth UX. | Plan |
| Post-login destination | `/dashboard` with empty state | Sets up the page S-02 will populate with workout data. | Plan |
| Error handling | Redirect to `/login?error=...` | Simple, single error surface the user can retry from. | Plan |
| Dev setup | Separate ports + CORS | Mirrors production topology (separate containers). | Plan |
| Token refresh | Deferred | 6-hour token lifetime is sufficient for MVP. | Plan |

## Scope

**In scope:**
- PostgreSQL + EF Core setup with User entity and migrations
- Strava OAuth via `AspNet.Security.OAuth.Strava` with cookie auth
- Token persistence in DB via `OnCreatingTicket` event
- Backend endpoints: `/auth/login`, callback, `/api/auth/me`, `/api/auth/logout`
- CORS for Angular dev server
- Angular auth service, route guard, login page, dashboard (empty state)
- OAuth error handling (deny, failure → error message)
- Auto-migration on startup for Docker

**Out of scope:**
- Token auto-refresh (background job)
- Strava webhooks
- Production deployment config
- User profile editing
- Frontend SSR

## Architecture / Approach

BFF pattern: Angular → .NET backend → Strava API. The backend handles the full OAuth flow and sets an HTTP-only cookie. Angular checks auth state via `GET /api/auth/me` (returns user or 401). A functional `canActivate` guard protects `/dashboard`. Tokens are persisted to PostgreSQL during the OAuth callback via EF Core.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Database Foundation | EF Core + PostgreSQL with User entity & migration | Minimal — docker-compose already has Postgres |
| 2. Backend OAuth Pipeline | Full Strava OAuth flow with token persistence | `OnCreatingTicket` handler must correctly upsert user and tokens |
| 3. Frontend Auth Flow | Login page, dashboard, auth guard, routing | Cross-origin cookie handling between ports 4200 and 5000 |
| 4. Integration & Verification | End-to-end Docker flow, error handling | Strava app callback domain must be configured correctly |

**Prerequisites:** Strava API application registered at strava.com/settings/api with client ID and secret available.
**Estimated effort:** ~2 sessions across 4 phases.

## Open Risks & Assumptions

- Strava API application must be registered and callback domain set to `localhost` for local dev
- Token auto-refresh is deferred — if a user's session spans >6 hours without re-auth, API calls will fail (acceptable for MVP)
- Auto-migration on startup is a dev convenience; production should use explicit migration steps

## Success Criteria (Summary)

- User can authenticate via Strava and land on an authenticated dashboard showing their name
- Session persists across page refreshes and server restarts
- OAuth denial/failure shows a user-friendly error on the login page
