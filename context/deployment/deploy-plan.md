---
project: strava-segments-performance
created: 2026-06-10
platform: Railway (Docker Hub image deployment)
status: finalized
---

# Strava Segments Performance — Deployment Plan

## Corrections to infrastructure.md

The following items in `context/foundation/infrastructure.md` are **incorrect** and must not be followed:

1. **Getting Started Step 2 uses `railway up --compose docker-compose.yml`** — this attempts a source deploy from the local filesystem. Do not use this. Services are created in the Railway dashboard and configured to pull Docker Hub images. `docker-compose.yml` is for local development only.
2. **Getting Started Step 4 calls `railway redeploy` without updating the image variable first** — this re-deploys the previously configured image SHA. Always run `railway variables --set RAILWAY_DOCKER_IMAGE=...<sha>` before `railway redeploy` (see CI workflows below).
3. **Getting Started Step 5 references `render.yaml`** — Render was eliminated. Disregard entirely; use Railway service configuration as described in this document.

---

## Repository Structure

All files below are created as part of this plan:

```
strava-segments-performance-backend/
  Dockerfile                              ← .NET 10 multi-stage build

strava-segments-performance/
  Dockerfile                              ← Angular build → nginx serve
  nginx.conf                              ← SPA routing + /api/* proxy
  src/environments/
    environment.ts                        ← dev: apiBaseUrl = localhost:5000
    environment.prod.ts                   ← prod: apiBaseUrl = '' (relative)

docker-compose.yml                        ← local dev (backend + frontend + postgres)

.github/workflows/
  backend-ci.yml                          ← build → Docker Hub → Railway redeploy
  frontend-ci.yml                         ← build → Docker Hub → Railway redeploy
```

---

## Files

### strava-segments-performance-backend/Dockerfile

```dockerfile
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY strava-segments-performance-backend.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Fallback for local docker run. Railway overrides this at runtime with
# ASPNETCORE_URLS=http://+:${PORT} (set as a Railway env var, not here).
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "strava-segments-performance-backend.dll"]
```

**Important:** HTTPS redirection (`app.UseHttpsRedirection()`) must NOT be present in `Program.cs`. Railway terminates TLS at the edge; the container runs plain HTTP.

### strava-segments-performance/nginx.conf

```nginx
server {
    listen 80;
    server_name _;

    root /usr/share/nginx/html;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;
    }

    # ${BACKEND_ORIGIN} is replaced at container start via envsubst.
    # Set BACKEND_ORIGIN=https://<backend-railway-domain> in Railway frontend service.
    location /api/ {
        proxy_pass        ${BACKEND_ORIGIN};
        proxy_set_header  Host              $host;
        proxy_set_header  X-Real-IP         $remote_addr;
        proxy_set_header  X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header  X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;
    }

    add_header X-Frame-Options        "SAMEORIGIN"    always;
    add_header X-Content-Type-Options  "nosniff"       always;
    add_header Referrer-Policy         "strict-origin" always;

    gzip on;
    gzip_types text/plain text/css application/javascript application/json image/svg+xml;
    gzip_min_length 1024;
}
```

### strava-segments-performance/Dockerfile

```dockerfile
# syntax=docker/dockerfile:1

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . ./
RUN npm run build -- --configuration production

FROM nginx:1.27-alpine AS runtime

RUN rm /etc/nginx/conf.d/default.conf

COPY nginx.conf /etc/nginx/conf.d/app.conf.template

# Output path for @angular/build:application defaults to dist/<project>/browser.
# Verify after first local build if the project name slug differs.
COPY --from=build /app/dist/strava-segments-performance/browser /usr/share/nginx/html

EXPOSE 80
CMD ["/bin/sh", "-c", "envsubst '${BACKEND_ORIGIN}' < /etc/nginx/conf.d/app.conf.template > /etc/nginx/conf.d/app.conf && nginx -g 'daemon off;'"]
```

### strava-segments-performance/src/environments/environment.ts

```typescript
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5000',
};
```

### strava-segments-performance/src/environments/environment.prod.ts

```typescript
export const environment = {
  production: true,
  apiBaseUrl: '',  // relative — nginx proxies /api/* to backend
};
```

`angular.json` production configuration must include:
```json
"fileReplacements": [
  {
    "replace": "src/environments/environment.ts",
    "with": "src/environments/environment.prod.ts"
  }
]
```

All Angular services must build API URLs as:
```typescript
`${environment.apiBaseUrl}/api/workouts`
// Dev  → http://localhost:5000/api/workouts
// Prod → /api/workouts  (nginx proxies to backend)
```

### docker-compose.yml (repo root — local dev only)

```yaml
version: "3.9"

services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: strava_segments
      POSTGRES_USER: strava_user
      POSTGRES_PASSWORD: strava_local_password
    ports:
      - "5432:5432"
    volumes:
      - postgres_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U strava_user -d strava_segments"]
      interval: 5s
      timeout: 5s
      retries: 10

  backend:
    build:
      context: ./strava-segments-performance-backend
      dockerfile: Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_URLS: http://+:5000
      ConnectionStrings__DefaultConnection: >-
        Host=postgres;Port=5432;Database=strava_segments;
        Username=strava_user;Password=strava_local_password
      Strava__ClientId: "${STRAVA_CLIENT_ID}"
      Strava__ClientSecret: "${STRAVA_CLIENT_SECRET}"
      Strava__CallbackUrl: "http://localhost:4200/auth/callback"
    ports:
      - "5000:5000"
    depends_on:
      postgres:
        condition: service_healthy

  frontend:
    build:
      context: ./strava-segments-performance
      dockerfile: Dockerfile
    environment:
      BACKEND_ORIGIN: "http://backend:5000"
    ports:
      - "4200:80"
    depends_on:
      - backend

volumes:
  postgres_data:
```

Create a `.env` file (gitignored) at the repo root:
```
STRAVA_CLIENT_ID=<your dev Strava app client ID>
STRAVA_CLIENT_SECRET=<your dev Strava app client secret>
```

### .github/workflows/backend-ci.yml

```yaml
name: Backend CI/CD

on:
  push:
    branches: [main]
    paths:
      - "strava-segments-performance-backend/**"
      - ".github/workflows/backend-ci.yml"
  pull_request:
    branches: [main]
    paths:
      - "strava-segments-performance-backend/**"

env:
  DOCKER_IMAGE: ${{ secrets.DOCKER_USERNAME }}/strava-backend

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4

      - uses: docker/setup-buildx-action@v3

      - uses: docker/login-action@v3
        with:
          username: ${{ secrets.DOCKER_USERNAME }}
          password: ${{ secrets.DOCKER_PASSWORD }}

      - uses: docker/build-push-action@v6
        with:
          context: strava-segments-performance-backend
          file: strava-segments-performance-backend/Dockerfile
          push: ${{ github.ref == 'refs/heads/main' }}
          tags: |
            ${{ env.DOCKER_IMAGE }}:${{ github.sha }}
            ${{ env.DOCKER_IMAGE }}:latest
          cache-from: type=gha
          cache-to: type=gha,mode=max

      - name: Install Railway CLI
        if: github.ref == 'refs/heads/main'
        run: npm i -g @railway/cli

      - name: Update image variable and redeploy backend
        if: github.ref == 'refs/heads/main'
        env:
          RAILWAY_TOKEN: ${{ secrets.RAILWAY_TOKEN }}
        run: |
          railway variables --service backend --set \
            "RAILWAY_DOCKER_IMAGE=${{ env.DOCKER_IMAGE }}:${{ github.sha }}"
          railway redeploy --service backend --yes
```

### .github/workflows/frontend-ci.yml

```yaml
name: Frontend CI/CD

on:
  push:
    branches: [main]
    paths:
      - "strava-segments-performance/**"
      - ".github/workflows/frontend-ci.yml"
  pull_request:
    branches: [main]
    paths:
      - "strava-segments-performance/**"

env:
  DOCKER_IMAGE: ${{ secrets.DOCKER_USERNAME }}/strava-frontend

jobs:
  build-and-deploy:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4

      - uses: docker/setup-buildx-action@v3

      - uses: docker/login-action@v3
        with:
          username: ${{ secrets.DOCKER_USERNAME }}
          password: ${{ secrets.DOCKER_PASSWORD }}

      - uses: docker/build-push-action@v6
        with:
          context: strava-segments-performance
          file: strava-segments-performance/Dockerfile
          push: ${{ github.ref == 'refs/heads/main' }}
          tags: |
            ${{ env.DOCKER_IMAGE }}:${{ github.sha }}
            ${{ env.DOCKER_IMAGE }}:latest
          cache-from: type=gha
          cache-to: type=gha,mode=max

      - name: Install Railway CLI
        if: github.ref == 'refs/heads/main'
        run: npm i -g @railway/cli

      - name: Update image variable and redeploy frontend
        if: github.ref == 'refs/heads/main'
        env:
          RAILWAY_TOKEN: ${{ secrets.RAILWAY_TOKEN }}
        run: |
          railway variables --service frontend --set \
            "RAILWAY_DOCKER_IMAGE=${{ env.DOCKER_IMAGE }}:${{ github.sha }}"
          railway redeploy --service frontend --yes
```

---

## Railway Setup (Manual, One-Time)

### 1. Install CLI and authenticate

```bash
npm i -g @railway/cli
railway login
```

### 2. Create project and link

```bash
railway init          # name: strava-segments-performance
railway link
```

### 3. Create services

In the Railway dashboard: create three services manually:
- `backend` — Docker Hub image deployment
- `frontend` — Docker Hub image deployment
- `postgres` — click "New" → "Database" → "PostgreSQL" (managed plugin)

**Do not use `railway up --compose` — it attempts a source deploy, not an image deploy.**

### 4. Set backend environment variables

| Variable | Value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | `http://+:${PORT}` |
| `ConnectionStrings__DefaultConnection` | Npgsql connection string from Railway PostgreSQL plugin panel → Connect |
| `Strava__ClientId` | Strava app client ID |
| `Strava__ClientSecret` | Strava app client secret |
| `Strava__CallbackUrl` | `https://<backend-railway-domain>/auth/callback` |
| `RAILWAY_DOCKER_IMAGE` | `yourdockerhubuser/strava-backend:latest` (CI updates this to SHA on each deploy) |

Railway's PostgreSQL connection string format (Npgsql):
```
Host=<host>;Port=5432;Database=railway;Username=postgres;Password=<password>;SSL Mode=Require;Trust Server Certificate=true
```

### 5. Set frontend environment variables

| Variable | Value |
|---|---|
| `BACKEND_ORIGIN` | `https://<backend-railway-domain>` |
| `RAILWAY_DOCKER_IMAGE` | `yourdockerhubuser/strava-frontend:latest` (CI updates this to SHA on each deploy) |

### 6. Generate public domains

In Railway dashboard: Settings → Networking → Generate Domain for both `backend` and `frontend` services. Record these domains — they are stable and do not change between deploys.

Update `Strava__CallbackUrl` with the actual backend domain, then register that callback URL in the Strava developer portal.

### 7. Create a project-scoped Railway token

Account Settings → Tokens → New Token → scope to this project only. Store as `RAILWAY_TOKEN` in GitHub repository Secrets. **Never use an account-level token** — limits blast radius if the secret leaks.

### 8. Add GitHub repository secrets

Navigate to GitHub repo → Settings → Secrets and variables → Actions:

| Secret | Value |
|---|---|
| `DOCKER_USERNAME` | Docker Hub username |
| `DOCKER_PASSWORD` | Docker Hub access token (not account password) |
| `RAILWAY_TOKEN` | Project-scoped Railway deploy token from step 7 |

---

## Database

Railway provides a managed PostgreSQL 16 plugin. The backend connects via `ConnectionStrings__DefaultConnection`.

### DB startup retry (required — Railway drops `depends_on`)

Railway's Docker Compose import silently drops `depends_on` and `healthcheck`. The backend must retry the DB connection on startup. Add this to `Program.cs` **after** EF Core is wired up, before `app.Run()`:

```csharp
// DB startup retry — Railway does not honor depends_on
var maxRetries = 10;
var delay = TimeSpan.FromSeconds(3);
for (var attempt = 1; attempt <= maxRetries; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.CanConnectAsync();
        break;
    }
    catch (Exception ex) when (attempt < maxRetries)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning("DB not ready (attempt {Attempt}/{Max}): {Message}. Retrying in {Delay}s...",
            attempt, maxRetries, ex.Message, delay.TotalSeconds);
        await Task.Delay(delay);
        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
    }
}
```

### Migrations

Use EF Core with `MigrateAsync` on startup (Option A — simplest for solo MVP):

1. Add NuGet packages to `strava-segments-performance-backend.csproj`:
   - `Microsoft.EntityFrameworkCore`
   - `Npgsql.EntityFrameworkCore.PostgreSQL`
   - `Microsoft.EntityFrameworkCore.Design` (dev only)
2. Create `ApplicationDbContext`.
3. Run `dotnet ef migrations add InitialCreate`.
4. Call `await db.Database.MigrateAsync()` inside the startup retry block above.

---

## Verification

### Local smoke test

```bash
# Build and start
docker compose up --build -d

# Backend health
curl http://localhost:5000/health
# Expected: {"status":"healthy"}

# Frontend loads
curl -s http://localhost:4200 | grep -i "strava"

# nginx proxy
curl http://localhost:4200/api/health

# Tear down
docker compose down -v
```

### Railway post-deploy verification

```bash
# Tail logs after first push to main
railway logs --service backend --tail

# Backend health
curl https://<backend-railway-domain>/health

# Frontend loads
curl -sI https://<frontend-railway-domain>
# Expected: HTTP/2 200

# nginx proxy in production
curl https://<frontend-railway-domain>/api/health

# Confirm deployment used correct SHA
railway deployment list --service backend
```

### End-to-end Strava OAuth smoke test

1. Navigate to `https://<frontend-railway-domain>`
2. Click "Connect with Strava"
3. Authorize in the Strava OAuth screen
4. Confirm redirect back to the app with an authenticated session
5. Trigger a workout analysis
6. Confirm background worker activity: `railway logs --service backend --tail`

---

## Risk Mitigations

| Risk | Mitigation |
|---|---|
| `$PORT` mismatch | `ASPNETCORE_URLS=http://+:${PORT}` set as Railway env var — overrides Dockerfile default |
| OOM kill mid-fetch (1 GB Hobby cap) | Profile .NET baseline with `dotnet-counters monitor` before launch; budget for Hobby → Pro upgrade (~$20/mo) |
| 72-hour rollback window expires | Every push tags Docker Hub with `git-sha`; rollback = set `RAILWAY_DOCKER_IMAGE` to old SHA + Redeploy |
| DB startup race on Railway | Exponential-backoff retry in `Program.cs` (see Database section) |
| Background worker job lost on container restart | Persist job state to `job_runs` DB table; `IHostedService` resumes or marks failed on startup |
| `railway redeploy` re-runs stale image | CI sets `RAILWAY_DOCKER_IMAGE` to new SHA before calling `railway redeploy` |
| Angular hardcoded API URL | `environment.prod.ts` uses `apiBaseUrl: ''` (relative); nginx proxies `/api/*` to backend |
| `RAILWAY_TOKEN` account-level leak | Project-scoped token only; rotate by revoking in Railway dashboard + updating GitHub secret |
| nginx `${BACKEND_ORIGIN}` not substituted | If `BACKEND_ORIGIN` is unset, nginx will fail to start (crash loop in Railway). Verify env var is set before first deploy. |

---

## Open TODOs

| ID | Item | Blocking? |
|---|---|---|
| TODO-01 | Add EF Core + Npgsql NuGet packages to `.csproj` | Yes — DB layer required for workout caching |
| TODO-02 | Create `ApplicationDbContext` + `job_runs` table | Yes — background worker state persistence |
| TODO-03 | Add DB startup retry + `MigrateAsync` to `Program.cs` (after EF Core wired up) | Yes — Railway drops `depends_on` |
| TODO-04 | Verify Angular build output path: `dist/strava-segments-performance/browser` | Yes — Dockerfile COPY path must match |
| TODO-05 | Record Railway backend public domain; set as `BACKEND_ORIGIN` in Railway frontend service | Yes — nginx proxy cannot route without it |
| TODO-06 | Register Strava OAuth callback URL using Railway backend domain in Strava developer portal | Yes — OAuth will fail without it |
| TODO-07 | Verify `railway variables --set` syntax for installed Railway CLI version (`railway --version`) | Yes — syntax varies between CLI major versions |

---

## Implementation Order

Execute in this sequence to avoid blocked dependencies:

1. `TODO-01` — Add EF Core packages to `.csproj`
2. `TODO-02` — Create `ApplicationDbContext` + migrations
3. `TODO-03` — Add DB retry + `MigrateAsync` to `Program.cs`
4. `docker compose up --build` — run local smoke test (Section: Verification)
5. `TODO-05` — Create Railway project and services; get public domains
6. Set all Railway environment variables (Section: Railway Setup steps 4–5)
7. Set GitHub repository secrets (Section: Railway Setup step 8)
8. Push to `main` — GitHub Actions builds and deploys both services
9. `TODO-06` — Register Railway backend domain as Strava OAuth callback
10. Run Railway and end-to-end verification (Section: Verification)
