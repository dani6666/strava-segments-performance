# Strava OAuth Login Implementation Plan

## Overview

Implement end-to-end Strava OAuth authentication using the BFF (Backend-For-Frontend) pattern. The .NET backend owns all tokens and stores them in PostgreSQL. The Angular frontend provides a login page with a "Connect with Strava" button and a post-login dashboard with an empty state. No Strava tokens or OAuth logic ever reach the browser.

## Current State Analysis

- **Backend**: Bare .NET 10 minimal API with only `/health` endpoint and OpenAPI (`Program.cs:1-15`). No auth, no database packages.
- **Frontend**: Scaffolded Angular 21 with empty routes, no services/guards/components. `apiBaseUrl` set to `http://localhost:5000` in `environment.ts`.
- **Docker Compose**: Already configured with PostgreSQL 16, connection string, and Strava env var placeholders (`docker-compose.yml:1-51`).
- **No auth infrastructure exists** in either project — this is greenfield.

### Key Discoveries:

- `docker-compose.yml` already defines `postgres` service with `strava_segments` database, `ConnectionStrings__DefaultConnection`, and `Strava__ClientId`/`Strava__ClientSecret` env vars — Phase 1 only needs EF Core wiring, not Docker changes.
- `Strava__CallbackUrl` in docker-compose is set to `http://localhost:4200/auth/callback` — but with BFF pattern the callback goes to the backend, not frontend. This needs correction.
- Angular environment already has `apiBaseUrl: 'http://localhost:5000'` — frontend HTTP calls are pre-configured for the right port.
- The `.csproj` targets `net10.0` with nullable and implicit usings enabled.

## Desired End State

A user visiting the app unauthenticated sees a login page with a "Connect with Strava" button. Clicking it redirects through Strava OAuth. On approval, the user lands on `/dashboard` showing their Strava display name and an empty state message. Refreshing the page preserves the session (cookie-based). Tokens persist in PostgreSQL across server restarts. A logout button clears the session. OAuth errors redirect to the login page with a user-friendly message.

**Verification**: Run `docker compose up`, visit `http://localhost:4200`, click "Connect with Strava", authorize, and confirm landing on the dashboard with user name displayed.

## What We're NOT Doing

- Token auto-refresh (background job) — deferred; 6-hour token lifetime is sufficient for MVP
- Strava webhook subscriptions
- User profile editing or settings
- Database seeding or admin tooling
- Production deployment configuration
- Multiple user session management
- Frontend SSR

## Implementation Approach

Four phases in dependency order: database foundation → backend OAuth pipeline → frontend auth flow → integration verification. Each phase is independently testable. The backend uses `AspNet.Security.OAuth.Strava` (aspnet-contrib) with ASP.NET Core cookie authentication. Tokens are persisted via EF Core event handlers during the OAuth callback. The frontend uses a functional `canActivate` guard backed by a simple auth service that calls `GET /api/auth/me`.

## Critical Implementation Details

### Timing & lifecycle

The `AspNet.Security.OAuth.Strava` package handles the OAuth code-for-token exchange automatically but does NOT persist tokens to a database. The `OnCreatingTicket` event fires after token exchange and before the auth cookie is set — this is the only correct place to persist the user and tokens to PostgreSQL. If this event handler fails, the user gets a cookie with no persisted data.

---

## Phase 1: Database Foundation

### Overview

Add PostgreSQL support via EF Core with Npgsql. Create a `User` entity to store Strava athlete ID, display name, and OAuth tokens. Generate and apply the initial migration.

### Changes Required:

#### 1. NuGet packages

**File**: `strava-segments-performance-backend/strava-segments-performance-backend.csproj`

**Intent**: Add EF Core with Npgsql provider and the design-time tools package needed for migrations.

**Contract**: Add package references for `Npgsql.EntityFrameworkCore.PostgreSQL` and `Microsoft.EntityFrameworkCore.Design`.

#### 2. User entity

**File**: `strava-segments-performance-backend/Models/User.cs`

**Intent**: Define the domain entity representing a Strava-authenticated user. Stores the minimum needed for auth: Strava athlete ID (unique external key), display name, and OAuth tokens with expiry.

**Contract**: Entity with properties: `Id` (int, PK), `StravaAthleteId` (long, unique index), `DisplayName` (string), `AccessToken` (string), `RefreshToken` (string), `TokenExpiresAtUtc` (DateTime). Namespace: `strava_segments_performance_backend.Models`.

#### 3. DbContext

**File**: `strava-segments-performance-backend/Data/AppDbContext.cs`

**Intent**: Define the EF Core DbContext with a `Users` DbSet and configure the unique index on `StravaAthleteId`.

**Contract**: Class `AppDbContext : DbContext` with `DbSet<User> Users`. Override `OnModelCreating` to add unique index on `StravaAthleteId`.

#### 4. Register DbContext in Program.cs

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Wire up EF Core with the PostgreSQL connection string from configuration.

**Contract**: Call `builder.Services.AddNpgsql<AppDbContext>(connectionString)` using `ConnectionStrings:DefaultConnection` from config. Place before `builder.Build()`.

#### 5. Connection string in appsettings.Development.json

**File**: `strava-segments-performance-backend/appsettings.Development.json`

**Intent**: Add the local development connection string matching the docker-compose PostgreSQL service.

**Contract**: Add `ConnectionStrings.DefaultConnection` with value `Host=localhost;Port=5432;Database=strava_segments;Username=strava_user;Password=strava_local_password`.

#### 6. Initial migration

**Intent**: Generate the EF Core migration for the User table using `dotnet ef migrations add InitialCreate`.

**Contract**: Run `dotnet ef migrations add InitialCreate` from the backend directory. Verify the generated migration creates the `Users` table with the unique index on `StravaAthleteId`.

### Success Criteria:

#### Automated Verification:

- Project builds cleanly: `dotnet build` in backend directory
- Migration files are generated in `Migrations/` directory
- Database can be created: `dotnet ef database update` succeeds against running PostgreSQL container

#### Manual Verification:

- Connect to PostgreSQL (`docker compose up postgres`) and verify `Users` table exists with correct columns and index

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 2: Backend OAuth Pipeline

### Overview

Wire up Strava OAuth using `AspNet.Security.OAuth.Strava` with cookie authentication. Persist user and tokens to PostgreSQL on successful auth. Expose auth endpoints: login initiation, callback (handled by middleware), user profile, and logout. Configure CORS for the Angular dev server.

### Changes Required:

#### 1. NuGet package

**File**: `strava-segments-performance-backend/strava-segments-performance-backend.csproj`

**Intent**: Add the aspnet-contrib Strava OAuth provider.

**Contract**: Add package reference for `AspNet.Security.OAuth.Strava`.

#### 2. Authentication configuration

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Register cookie authentication as the default scheme and add the Strava OAuth provider. Configure the `OnCreatingTicket` event to upsert the user and tokens into PostgreSQL. Configure the cookie to redirect to the frontend login page on 401 instead of the default redirect.

**Contract**:
- Default scheme: `CookieAuthenticationDefaults.AuthenticationScheme`
- Cookie: `HttpOnly=true`, `SecurePolicy=Always` (relaxed to `SameAsRequest` in dev), `SameSite=Lax`
- Cookie events: override `OnRedirectToLogin` to return 401 JSON for `/api/*` requests (so the Angular guard gets a clean 401, not a 302 to a Strava URL)
- Strava options: `ClientId` and `ClientSecret` from config (`Strava:ClientId`, `Strava:ClientSecret`), scope `activity:read_all`, `SaveTokens=true`, `CallbackPath=/auth/callback`
- `OnCreatingTicket`: extract `StravaAthleteId` from claims, upsert `User` entity in `AppDbContext`, save access/refresh tokens and expiry

The `OnCreatingTicket` handler is the critical integration point — sample contract:

```csharp
options.Events.OnCreatingTicket = async context =>
{
    var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
    var stravaId = long.Parse(context.Identity!.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    // upsert user with tokens from context.AccessToken, context.RefreshToken, context.ExpiresIn
    // If database upsert fails (connection lost, constraint violation, etc.), rethrow the exception
    // to fail the OAuth flow cleanly. This prevents silent corruption of session state where the user
    // gets a cookie but no persisted tokens, leading to broken sessions after app restart.
};
```

#### 3. CORS configuration

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Allow the Angular dev server (`http://localhost:4200`) to make credentialed cross-origin requests.

**Contract**: Add a named CORS policy "Frontend" allowing origin from `Frontend:Origin` config, any header, any method, and `AllowCredentials()`. Apply with `app.UseCors("Frontend")`. Add `Frontend:Origin` to `appsettings.Development.json` with value `http://localhost:4200`.

#### 4. Auth endpoints

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Expose the four auth endpoints the frontend needs.

**Contract**:
- `GET /auth/login` — issues `ChallengeResult` for the Strava scheme with `RedirectUri` pointing to the frontend dashboard (`{Frontend:Origin}/dashboard`)
- `GET /auth/callback` — handled automatically by the Strava middleware (no manual endpoint needed)
- `GET /api/auth/me` — requires authorization; returns `{ stravaAthleteId, displayName }` from the authenticated user's claims/DB record
- `POST /api/auth/logout` — calls `HttpContext.SignOutAsync()`, returns 200

#### 5. Middleware ordering

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Ensure middleware is ordered correctly: CORS → Authentication → Authorization → endpoints.

**Contract**: After `builder.Build()`, the pipeline order must be: `app.UseCors("Frontend")` → `app.UseAuthentication()` → `app.UseAuthorization()` → endpoint mappings.

#### 6. Fix docker-compose callback URL

**File**: `docker-compose.yml`

**Intent**: The current `Strava__CallbackUrl` points to the frontend (`localhost:4200/auth/callback`). With BFF pattern, the callback is handled by the backend. However, `AspNet.Security.OAuth.Strava` uses `CallbackPath` (relative) and constructs the full URL from the request host — so this env var is actually unused by the middleware. Remove it to avoid confusion.

**Contract**: Remove the `Strava__CallbackUrl` line from the backend service environment. Add `Frontend__Origin: "http://localhost:4200"` for CORS config.

### Success Criteria:

#### Automated Verification:

- Project builds cleanly: `dotnet build`
- `GET /auth/login` returns a 302 redirect to `strava.com/oauth/authorize` with correct `client_id` and `scope=activity:read_all`
- `GET /api/auth/me` returns 401 when unauthenticated
- `POST /api/auth/logout` returns 200

#### Manual Verification:

- Start backend (`dotnet run`) + PostgreSQL (`docker compose up postgres`)
- Visit `http://localhost:5000/auth/login` in a browser — redirects to Strava authorization page
- After authorizing on Strava, the callback completes and a cookie is set
- `GET /api/auth/me` returns the user's Strava display name
- Check PostgreSQL: `Users` table has a row with correct `StravaAthleteId`, `AccessToken`, `RefreshToken`
- `POST /api/auth/logout` clears the session; subsequent `GET /api/auth/me` returns 401

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 3: Frontend Auth Flow

### Overview

Build the Angular auth layer: an auth service, a functional route guard, a login page component, and a dashboard component with empty state. Wire up routing and HTTP client configuration.

### Changes Required:

#### 1. HTTP client configuration

**File**: `strava-segments-performance/src/app/app.config.ts`

**Intent**: Register `HttpClient` for dependency injection so services can make API calls.

**Contract**: Add `provideHttpClient(withFetch())` to the providers array. Import from `@angular/common/http`.

#### 2. Auth service

**File**: `strava-segments-performance/src/app/auth/auth.service.ts`

**Intent**: Encapsulate all auth-related API calls. Provides methods to get the current user, initiate login, and logout. Caches the user state as a signal.

**Contract**: Injectable service with:
- `user` signal holding `{ stravaAthleteId: number, displayName: string } | null`
- `checkAuth()` — calls `GET {apiBaseUrl}/api/auth/me` with `withCredentials: true`, updates `user` signal. Returns an Observable.
- `login()` — sets `window.location.href` to `{apiBaseUrl}/auth/login`
- `logout()` — calls `POST {apiBaseUrl}/api/auth/logout` with `withCredentials: true`, clears `user` signal, navigates to `/login`

#### 3. Auth guard

**File**: `strava-segments-performance/src/app/auth/auth.guard.ts`

**Intent**: Protect routes that require authentication. Redirects to `/login` if the user is not authenticated.

**Contract**: Functional `CanActivateFn` that injects `AuthService`, calls `checkAuth()`. If the API returns a user, allow navigation. If 401, redirect to `/login` via `Router`. Return an `Observable<boolean | UrlTree>`.

#### 4. Login page component

**File**: `strava-segments-performance/src/app/login/login.component.ts` (+ `.html`, `.scss`)

**Intent**: Simple login page with a "Connect with Strava" button. Shows an error message if redirected back with an error query param.

**Contract**: Standalone component at route `/login`. Template contains a heading, brief app description, and a button that calls `authService.login()`. Reads `?error=` query param from `ActivatedRoute` and displays a user-friendly message when present (e.g., "Authentication failed. Please try again.").

#### 5. Dashboard component

**File**: `strava-segments-performance/src/app/dashboard/dashboard.component.ts` (+ `.html`, `.scss`)

**Intent**: Authenticated landing page showing the user's Strava name and an empty state placeholder for future workout data (S-02). Includes a logout button.

**Contract**: Standalone component at route `/dashboard` (guarded by `authGuard`). Reads user from `AuthService.user` signal. Displays: greeting with `displayName`, empty state message ("No workouts analyzed yet — this is where your fitness trends will appear"), and a logout button calling `authService.logout()`.

#### 6. Routing

**File**: `strava-segments-performance/src/app/app.routes.ts`

**Intent**: Define the app's route structure with auth protection.

**Contract**:
- `/login` → `LoginComponent`
- `/dashboard` → `DashboardComponent`, guarded by `authGuard`
- `/` → redirect to `/dashboard`
- `**` → redirect to `/dashboard`

#### 7. Root component cleanup

**File**: `strava-segments-performance/src/app/app.html`

**Intent**: Replace the Angular boilerplate welcome page with just the router outlet.

**Contract**: Replace entire template content with `<router-outlet />` (the routed components handle all UI).

### Success Criteria:

#### Automated Verification:

- Frontend builds cleanly: `npm run build` in frontend directory
- TypeScript compilation passes with strict mode

#### Manual Verification:

- `ng serve` → visiting `http://localhost:4200` redirects to `/login`
- Login page shows "Connect with Strava" button
- Clicking the button navigates to Strava OAuth (via backend)
- After auth, user lands on `/dashboard` with their Strava name displayed
- Logout button works: clears session, redirects to `/login`
- Visiting `/dashboard` while unauthenticated redirects to `/login`
- Visiting `/login?error=denied` shows an error message

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Phase 4: Integration & Verification

### Overview

End-to-end verification of the full OAuth flow through Docker Compose. Fix any integration issues between the three containers. Handle OAuth error cases (user denies, Strava down). Update backend to redirect to login with error param on OAuth failure.

### Changes Required:

#### 1. OAuth failure handling

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: When Strava OAuth fails (user denies access, network error, invalid state), redirect the user to the frontend login page with an error query parameter instead of showing a server error page.

**Contract**: In the Strava OAuth options, configure `Events.OnRemoteFailure` to redirect to `{Frontend:Origin}/login?error=auth_failed`. Prevent the default server-side error handling with `context.HandleResponse()`.

#### 2. Backend Dockerfile — EF Core migrations on startup

**File**: `strava-segments-performance-backend/Program.cs`

**Intent**: Automatically apply pending EF Core migrations when the app starts, so the Docker container doesn't need a separate migration step.

**Contract**: After building the app and before running it, create a scope, get `AppDbContext`, call `Database.MigrateAsync()`. This ensures the database schema is up-to-date on every container start.

#### 3. Docker Compose callback URL registration note

**File**: `docker-compose.yml`

**Intent**: Add a comment documenting that the Strava app's "Authorization Callback Domain" must be set to `localhost` in Strava API settings for local development.

**Contract**: Add a YAML comment in the backend service section.

### Success Criteria:

#### Automated Verification:

- `docker compose build` succeeds for all three services
- `docker compose up` starts all containers without errors
- Backend health check passes: `curl http://localhost:5000/health`

#### Manual Verification:

- Full flow in Docker: visit `http://localhost:4200`, click login, authorize on Strava, land on dashboard with name
- Deny access on Strava → redirected to login page with error message
- Refresh dashboard page → session persists (cookie + DB)
- Restart backend container (`docker compose restart backend`) → session persists (tokens in DB, cookie still valid)
- Logout → session cleared, redirected to login

**Implementation Note**: After completing this phase and all automated verification passes, pause here for manual confirmation from the human that the manual testing was successful before proceeding to the next phase.

---

## Testing Strategy

### Unit Tests:

- Auth guard: returns true for authenticated user, redirects for 401
- Auth service: `checkAuth()` parses response correctly, handles 401
- Login component: renders button, shows error message when query param present

### Integration Tests:

- Backend: `GET /auth/login` returns 302 to Strava
- Backend: `GET /api/auth/me` returns 401 when unauthenticated
- Backend: `POST /api/auth/logout` clears auth cookie

### Manual Testing Steps:

1. Start full stack: `docker compose up`
2. Visit `http://localhost:4200` — should redirect to `/login`
3. Click "Connect with Strava" — should redirect to Strava authorization
4. Authorize on Strava — should land on `/dashboard` with your name
5. Refresh page — session should persist
6. Click logout — should redirect to `/login`
7. Try to visit `/dashboard` directly — should redirect to `/login`
8. Repeat step 3 but deny access on Strava — should see error message on login page
9. Restart backend container — revisit `/dashboard`, session should persist

## Performance Considerations

- Cookie-based auth adds minimal overhead per request (just cookie validation)
- `GET /api/auth/me` is called once per route guard activation — consider caching in the Angular service (already handled via the `user` signal)
- No token refresh in this phase — 6-hour access token lifetime is sufficient for MVP usage patterns

## Migration Notes

- Initial EF Core migration creates the `Users` table — this is the first database schema in the project
- Auto-migration on startup (`Database.MigrateAsync()`) is acceptable for MVP; production should use explicit migration steps
- The User entity schema will expand in S-02 (workout data) — designed to be additive

## References

- Research: `context/changes/strava-oauth-login/research.md`
- Roadmap slice: `context/foundation/roadmap.md` (S-01)
- Docker Compose: `docker-compose.yml`
- Backend entry point: `strava-segments-performance-backend/Program.cs`
- Frontend config: `strava-segments-performance/src/app/app.config.ts`
- Frontend environments: `strava-segments-performance/src/environments/environment.ts`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Database Foundation

#### Automated

- [x] 1.1 Project builds cleanly: `dotnet build`
- [x] 1.2 Migration files generated in `Migrations/` directory
- [x] 1.3 Database created: `dotnet ef database update` succeeds

#### Manual

- [x] 1.4 `Users` table exists in PostgreSQL with correct columns and index

### Phase 2: Backend OAuth Pipeline

#### Automated

- [x] 2.1 Project builds cleanly: `dotnet build`
- [x] 2.2 `GET /auth/login` returns 302 to Strava with correct params
- [x] 2.3 `GET /api/auth/me` returns 401 when unauthenticated
- [x] 2.4 `POST /api/auth/logout` returns 200

#### Manual

- [x] 2.5 `/auth/login` redirects to Strava authorization page
- [x] 2.6 After Strava auth, cookie is set and `/api/auth/me` returns user data
- [x] 2.7 User row exists in PostgreSQL with tokens
- [x] 2.8 Logout clears session

### Phase 3: Frontend Auth Flow

#### Automated

- [ ] 3.1 Frontend builds cleanly: `npm run build`
- [ ] 3.2 TypeScript strict mode passes

#### Manual

- [ ] 3.3 Unauthenticated visit redirects to `/login`
- [ ] 3.4 Login page shows "Connect with Strava" button
- [ ] 3.5 Full auth flow lands on `/dashboard` with user name
- [ ] 3.6 Logout works and redirects to `/login`
- [ ] 3.7 Error query param displays message on login page

### Phase 4: Integration & Verification

#### Automated

- [ ] 4.1 `docker compose build` succeeds
- [ ] 4.2 `docker compose up` starts without errors
- [ ] 4.3 Health check passes: `curl http://localhost:5000/health`

#### Manual

- [ ] 4.4 Full Docker flow: login → dashboard → logout
- [ ] 4.5 Strava deny → error message on login page
- [ ] 4.6 Page refresh preserves session
- [ ] 4.7 Backend restart preserves session (tokens in DB)
