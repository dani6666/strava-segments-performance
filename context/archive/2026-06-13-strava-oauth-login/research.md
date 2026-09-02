---
title: Strava OAuth Research
change_id: strava-oauth-login
created: 2026-06-13
---

# Strava OAuth Implementation Research

## Flow Architecture

Backend-driven OAuth (BFF pattern). The .NET backend owns all tokens — Angular never touches them.

```
Angular                  .NET Backend              Strava
  |                          |                        |
  |-- GET /auth/login ------>|                        |
  |                          |-- 302 redirect ------->|
  |                          |   strava.com/oauth/authorize
  |                          |                        |
  |                          |<-- redirect w/ code ---|
  |                          |   (to /auth/callback)  |
  |                          |                        |
  |                          |-- POST /oauth/token -->|
  |                          |<-- access + refresh ---|
  |                          |                        |
  |                          | store tokens server-side
  |                          | set HTTP-only cookie   |
  |<-- redirect + cookie ----|                        |
  |   (back to Angular)      |                        |
```

Angular checks auth state via `GET /api/auth/me` — returns user profile or 401.

## Strava OAuth Endpoints

| Purpose | URL |
|---|---|
| Authorization | `GET https://www.strava.com/oauth/authorize` |
| Token exchange | `POST https://www.strava.com/oauth/token` |
| Token refresh | `POST https://www.strava.com/oauth/token` (grant_type=refresh_token) |
| Deauthorize | `POST https://www.strava.com/oauth/deauthorize` |

Authorization URL parameters:
- `client_id` — registered app ID
- `response_type=code`
- `redirect_uri` — must exactly match Strava app settings
- `scope=activity:read_all` — required for segment efforts + heart rate
- `approval_prompt=auto` (or `force` to always show consent)

## Required Scope

`activity:read_all` — covers workout activities, segment efforts, and heart rate data. `read` alone is insufficient.

## Token Lifecycle

- Access tokens expire every **6 hours**
- Each refresh returns a **new refresh token** (old one is invalidated)
- Refresh token must be stored server-side only
- Refresh flow: `POST /oauth/token` with `grant_type=refresh_token&refresh_token=...`

## Backend: NuGet Package

**`AspNet.Security.OAuth.Strava` v10.0.0** — aspnet-contrib provider, matches .NET 10.

```
dotnet add package AspNet.Security.OAuth.Strava
```

Wires into standard ASP.NET Core auth pipeline:

```csharp
builder.Services.AddAuthentication(options => {
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options => {
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
})
.AddStrava(options => {
    options.ClientId = builder.Configuration["Strava:ClientId"];
    options.ClientSecret = builder.Configuration["Strava:ClientSecret"];
    options.Scope.Add("activity:read_all");
    options.SaveTokens = true;
});
```

**Note:** The aspnet-contrib library does NOT auto-refresh tokens. A background job or middleware must check `expires_at` and refresh proactively.

## Backend Endpoints Needed

| Endpoint | Purpose |
|---|---|
| `GET /auth/login` | Initiates OAuth redirect to Strava |
| `GET /auth/callback` | Handled by middleware; exchanges code for tokens, sets cookie, redirects to Angular |
| `GET /api/auth/me` | Returns authenticated user profile or 401 |
| `POST /api/auth/logout` | Clears server session + cookie |

## Frontend: Angular

No OAuth library needed. The backend drives the full flow.

**Auth service responsibilities:**
1. `login()` — `window.location.href = '/api/auth/login'`
2. `getUser()` — `GET /api/auth/me` (returns user or throws 401)
3. `logout()` — `POST /api/auth/logout`

**Route guard:** calls `getUser()`; if 401, redirects to a login page.

All HTTP requests to the backend use `withCredentials: true` so the session cookie is sent cross-origin.

## CORS Configuration (Backend)

Angular dev server runs on a different port — backend must allow it:

```csharp
builder.Services.AddCors(options => {
    options.AddPolicy("Frontend", policy => {
        policy.WithOrigins(builder.Configuration["Frontend:Origin"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // required for cookies
    });
});
```

## Environment Variables Needed

| Variable | Where | Description |
|---|---|---|
| `Strava__ClientId` | Backend | Strava app client ID |
| `Strava__ClientSecret` | Backend | Strava app client secret |
| `Frontend__Origin` | Backend | Angular app URL for CORS (e.g. `http://localhost:4200`) |
| `apiBaseUrl` | Frontend env | Backend base URL |

## Critical Details

- Redirect URI registered in Strava app settings must exactly match the callback URL
- Refresh token lives **only on the backend** — never sent to Angular
- Cookie: `HttpOnly`, `Secure`, `SameSite=Lax`
- Strava app must be registered at https://www.strava.com/settings/api (user prerequisite)

## Sources

- [Strava OAuth Authentication](https://developers.strava.com/docs/authentication/)
- [NuGet: AspNet.Security.OAuth.Strava 10.0.0](https://www.nuget.org/packages/AspNet.Security.OAuth.Strava)
- [aspnet-contrib/AspNet.Security.OAuth.Providers](https://github.com/aspnet-contrib/AspNet.Security.OAuth.Providers)
- [OAuth Token Management with Strava](https://www.zachliibbe.com/blog/oauth-token-management-with-automatic-refresh-a-strava-api-case-study)
