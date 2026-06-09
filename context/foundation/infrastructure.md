---
project: strava-segments-performance
researched_at: 2026-06-09
recommended_platform: Railway
runner_up: Fly.io
context_type: mvp
tech_stack:
  language: C# / TypeScript
  framework: .NET 10 Minimal API + Angular 21
  runtime: Docker (images published to Docker Hub)
---

## Recommendation

**Deploy on Railway.**

Railway is the strongest fit for this stack: it supports Docker Hub image deployment with GA Docker Compose import, imposes no execution timeout on background worker services (critical for 30–120 min Strava fetch jobs), ships an official Claude Code MCP server, and starts at $5/month on the Hobby plan. The scoring advantage over Fly.io is clear on two dimensions — GA MCP integration and confirmed no-timeout background workers without the 5-min kill_timeout risk present on Fly. Render was eliminated on cost (~$39/month minimum) and missing CLI rollback.

---

## Platform Comparison

Cloudflare Workers/Pages, Vercel, and Netlify were **eliminated by hard filter**: none support .NET 10 Docker containers, and all cap function execution well below the 30–120 min background worker requirement (Cloudflare: 30s, Vercel: 15 min max, Netlify: 15 min max).

| Platform | CLI-first | Managed infra | Agent docs | Stable deploy API | MCP / Integration | Total |
|---|---|---|---|---|---|---|
| Railway | Pass | Pass | Pass | Pass | Pass (GA) | **5/5** |
| Fly.io | Pass | Pass | Pass | Partial | Partial (Experimental) | 3.5/5 |
| Render | Partial | Pass | Pass | Partial | Partial (limited) | 3/5 |
| Cloudflare | — | — | — | — | — | ❌ eliminated |
| Vercel | — | — | — | — | — | ❌ eliminated |
| Netlify | — | — | — | — | — | ❌ eliminated |

### Shortlisted Platforms

#### 1. Railway (Recommended)

Railway runs persistent container services with no platform-enforced execution time limit — the `IHostedService` background worker pattern works without any configuration workaround. Docker Compose import is GA (partial key support). The official `@railway/mcp-server` is GA and integrates directly with Claude Code. The `railway` CLI covers deploy, logs, rollback, and secrets; `railway.com/llms.txt` and a full markdown doc export are available for agent context. Hobby plan at $5/month includes $5 of resource credit. The single-region GCP deployment (us-west2) may introduce latency for European users but is acceptable for MVP.

**GitHub Actions CI/CD:** Build → push to Docker Hub → call `railway redeploy --service <name> --yes` via the Railway CLI in CI. Required secret: `RAILWAY_TOKEN`. No official marketplace action for image-backed deploys; use the CLI directly. Community action `bervProject/railway-deploy` exists but targets source deploys — avoid it for Docker Hub flows.

#### 2. Fly.io

Fly.io has the best CLI surface (`flyctl`) and the most complete documentation (llms.txt, GitHub markdown source, per-page markdown export). Any Docker image including .NET 10 deploys via `fly deploy --image docker.io/<user>/<repo>:<tag>`. The `fly mcp server` is **experimental** as of 2026. The critical limitation for this project is the 5-minute `kill_timeout` cap: if a deploy or autostop event fires during a 90-minute Strava fetch, the job is forcibly killed after 5 minutes of graceful shutdown. Mitigable with `auto_stop_machines = "off"` and resumable job state, but requires extra engineering. Docker Compose compatibility is **new/experimental** (announced 2025). Estimated cost ~$15–20/month.

**GitHub Actions CI/CD:** `superfly/flyctl-actions/setup-flyctl@master` installs flyctl; then `flyctl deploy --image docker.io/<user>/<repo>:<tag>`. Required secret: `FLY_API_TOKEN` (scoped deploy token). Official action is unpinned (`@master`) — pin to a SHA for reproducibility. Auto-deploys immediately on workflow completion; no manual step needed.

#### 3. Render

Render's Background Worker service type imposes no execution timeout — a clean match for long-running Strava jobs. The Render CLI is solid for deploy and logs but has **no rollback command** (rollback requires Dashboard UI or REST API). Image-backed services **do not auto-deploy** when a new Docker Hub image is pushed — a deploy hook webhook call is required from CI. Docker Compose is not supported; the equivalent is a `render.yaml` Blueprint where the database must be a Render-managed Postgres instance (not a sidecar container), which conflicts with the user's Docker Compose mental model. MCP server exists (GA) but cannot trigger deploys or create Background Workers. Minimum viable cost ~$39/month.

**GitHub Actions CI/CD:** Build → push to Docker Hub → `curl "$RENDER_DEPLOY_HOOK_URL"`. No official action; community wrappers are thin `curl` wrappers. Required secret: `RENDER_DEPLOY_HOOK_URL`. Hook call returns 202 immediately — deploy is async; no native wait mechanism in the hook itself (use Render API + `RENDER_API_KEY` to poll status).

---

## Anti-Bias Cross-Check: Railway

### Devil's Advocate — Weaknesses

1. **72-hour rollback window (Hobby plan)**: Deployment history is retained for only 72 hours. For a solo after-hours project, a broken deploy discovered on day 4 has no rollback target.
2. **`$PORT` injection is a .NET gotcha**: Railway injects `PORT` at runtime and it changes between deploys. An `ASPNETCORE_URLS` value hardcoded to `http://+:8080` will silently bind the wrong port — the container passes health checks but never receives real traffic.
3. **Docker Compose import is partial**: `depends_on:`, `healthcheck:`, named volumes, and network aliases have incomplete support. A `compose.yml` that works locally will need adaptation and the parser drops unsupported fields silently with no error.
4. **Single-region GCP (us-west2)**: No region selection on Hobby. For a European developer, every deploy, log tail, and CLI command round-trips to the US west coast.
5. **1 GB RAM ceiling per service (Hobby)**: The .NET runtime + background worker doing large HTTP response parsing from Strava can push toward this ceiling; an OOM kill mid-fetch produces silent failure with limited log retention to diagnose it.

### Pre-Mortem — How This Could Fail

The developer deploys to Railway and everything looks fine at launch. Three weeks in, the Strava background worker hits memory pressure during large athlete data fetches — the 1 GB Hobby ceiling triggers an OOM kill mid-job. Because the developer works after hours and Railway's Hobby log retention is limited, the OOM events scroll off before they're noticed. Two days later, users report that "analysis never finishes" but there's no log evidence to diagnose. The developer tries to roll back to the last stable image but the 72-hour window has expired. Upgrading to the Pro plan for more RAM and longer retention bumps cost from $5 to $20+/month — a fine trade, but one that was predictable from day one given the app's memory profile. The initial "Hobby is enough" assumption was never verified against the .NET runtime's baseline memory consumption.

### Unknown Unknowns

- **`$PORT` is dynamic per deployment** — not just per service. Angular's frontend build must use a relative API base path or read `RAILWAY_PUBLIC_DOMAIN` at runtime, not a hardcoded port. Any `environment.ts` with `http://localhost:5000` will break in production.
- **Docker Compose import silently drops unsupported fields** — `healthcheck:` and `depends_on:` are ignored without errors. Verify the imported service definition in the Railway dashboard matches intent, especially for the database service startup sequencing.
- **`railway up` deploys from local filesystem by default** — the `--image` flag is required to deploy a pre-built Docker Hub image. Without it, Railway attempts a source build. GitHub Actions workflows must be explicit: `railway redeploy --service <name>` after image push, not `railway up`.
- **Service sleep on Hobby plan** — HTTP web services with no inbound traffic for a configurable idle period may sleep. Background workers are exempt, but the .NET API service itself will have cold-start latency for infrequent users.
- **MCP token grants broad project access** — the `RAILWAY_TOKEN` in Claude Code settings should be a project-scoped token, not an account token. An account token compromised via a settings leak gives an attacker full Railway account access.

---

## Operational Story

- **Preview deploys**: Railway creates a separate environment per branch when "PR Environments" is enabled in project settings (GA). Each PR gets a unique public URL. Environments are destroyed when the PR closes. Requires explicit opt-in per project.
- **Secrets**: Environment variables stored in Railway's encrypted variable store, scoped per service and environment. Set via `railway variables --set KEY=VALUE` or the MCP server. Rotation: update in Railway dashboard or CLI; service restarts automatically to pick up the new value.
- **Rollback**: `railway deployment list` shows deployments with IDs. Redeploy a prior deployment via the dashboard "Redeploy" button or `railway redeploy --deployment <id>`. **72-hour retention on Hobby** — plan accordingly. Database migrations do not roll back automatically.
- **Approval**: Railway project owner can restrict production deploys via team settings (Teams plan). On Hobby, all CLI and MCP operations run unattended. Sensitive operations (delete service, drop database) require Dashboard confirmation — the MCP server cannot perform deletions.
- **Logs**: `railway logs --service <name> --tail` for real-time streaming. Historical: `railway logs --since 1h --lines 500`. MCP: Railway MCP `get_logs` tool returns logs read-only without needing the CLI.

---

## Risk Register

| Risk | Source | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| `$PORT` injection causes silent port mismatch | Unknown unknowns | High | High | Set `ASPNETCORE_URLS=http://+:$PORT` in Railway env vars; never hardcode port in appsettings.json |
| OOM kill mid-Strava fetch (1 GB Hobby cap) | Pre-mortem | Medium | High | Profile .NET runtime memory baseline before launch; budget for Hobby → Pro upgrade (~$20/mo) if needed |
| 72-hour rollback window expires before bad deploy is caught | Devil's advocate | Medium | Medium | Tag Docker Hub images with git SHA, not just `:latest`; keep last 3 SHAs available for manual image redeploy |
| Docker Compose import silently drops healthcheck / depends_on | Devil's advocate | High | Medium | Implement retry/backoff in .NET startup code for DB connection; don't rely on Compose startup ordering |
| Background worker OOM or crash leaves analysis in unknown state | Research finding | Medium | High | Persist job progress state to DB; implement idempotent resume logic so a restart continues, not re-starts |
| `railway redeploy` re-pulls same tag, missing digest change | Research finding | Medium | Low | Always push unique tags (`:git-<sha>`) and update `RAILWAY_DOCKER_IMAGE` variable to the new tag before redeploying |
| MCP account token leak via settings | Unknown unknowns | Low | High | Use a project-scoped deploy token, not an account token; rotate after any machine compromise |
| Single-region GCP latency for EU users | Devil's advocate | High | Low | Acceptable at MVP scale; document as a known limitation, revisit at scale |

---

## Getting Started

1. **Install the Railway CLI** and authenticate:
   ```
   npm i -g @railway/cli
   railway login
   ```

2. **Create a Railway project** and import your `docker-compose.yml`:
   ```
   railway init
   railway up --compose docker-compose.yml
   ```
   Verify the imported services in the Railway dashboard; re-add any `healthcheck:` or `depends_on:` logic as application-level startup retry.

3. **Configure the .NET service's port** — add this environment variable to the backend service in Railway:
   ```
   ASPNETCORE_URLS=http://+:${PORT}
   ```
   Railway injects `$PORT` at runtime; the app must bind to it, not to a hardcoded value.

4. **Set up the GitHub Actions CI/CD pipeline** — a minimal workflow for the backend:
   ```yaml
   - uses: docker/login-action@v3
     with:
       username: ${{ secrets.DOCKER_USERNAME }}
       password: ${{ secrets.DOCKER_PASSWORD }}
   - uses: docker/build-push-action@v5
     with:
       push: true
       tags: yourdockerhubuser/strava-backend:${{ github.sha }}
   - run: |
       npm i -g @railway/cli
       railway redeploy --service backend --yes
     env:
       RAILWAY_TOKEN: ${{ secrets.RAILWAY_TOKEN }}
   ```
   Use `github.sha` as the image tag (not `:latest`) so rollback targets are pinned and traceable.

5. **Serve the Angular SPA as a Railway static site** — in `render.yaml` / Railway project, add a second service pointing to the `dist/` build output or a separate Nginx Docker image. Alternatively use Railway's static file hosting for the SPA and keep the backend as the only paid service.

---

## Out of Scope

The following were not evaluated in this research:
- Docker image configuration and multi-stage build optimization
- CI/CD pipeline implementation details beyond the triggering pattern
- Production-scale architecture (multi-region, HA, DR)
- Database schema migration strategies and Railway-aware rollback procedures
