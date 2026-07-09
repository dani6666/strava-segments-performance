# Lessons Learned

> Append-only register of recurring rules and patterns. Re-read at start by /10x-frame, /10x-research, /10x-plan, /10x-plan-review, /10x-implement, /10x-impl-review.

## Always assume master is the main branch

- **Context**: All phases — any skill that interacts with the git repository (branching, PRs, diffs, commits, CI)
- **Problem**: Skills guess the wrong base branch; PRs target the wrong ref, diffs are computed against non-existent branches, or CI runs against an unexpected base
- **Rule**: Always assume `master` is the main branch; never assume `main`, `develop`, or `trunk` unless the repo's HEAD or git config explicitly says otherwise.
- **Applies to**: all

## Always add .dockerignore before building .NET or Node images on Windows

- **Context**: Docker builds for backend (.NET) and frontend (Node/Angular)
- **Problem**: Without `.dockerignore`, the Windows `bin/`/`obj/` (or `node_modules/`) folders are sent into the Linux build context. The Windows-built NuGet package cache paths are incompatible with Linux, causing `dotnet publish --no-restore` to fail with `ResolvePackageAssets` errors.
- **Rule**: Every service with a Dockerfile must have a `.dockerignore` next to it. For .NET: exclude `bin/` and `obj/`. For Node: exclude `node_modules/` and `dist/`.
- **Applies to**: any Docker build step

## Angular production build uses environment.prod.ts — all backend routes must go through nginx

- **Context**: Angular + nginx Docker setup where `environment.prod.ts` has `apiBaseUrl: ''`
- **Problem**: The Docker frontend build runs `ng build --configuration production`, which swaps in `environment.prod.ts`. If `apiBaseUrl` is empty, all API and auth calls become relative URLs (e.g. `/auth/login`). If nginx doesn't proxy those paths, the SPA serves `index.html` for them and the auth flow breaks silently.
- **Rule**: In nginx.conf, proxy every backend path prefix the app uses (`/api/`, `/auth/`). Use `$http_host` (not `$host`) in `proxy_set_header Host` so the backend receives the correct host+port and constructs OAuth callback URLs correctly.
- **Applies to**: frontend Docker + nginx builds

## Store secrets in .env and propagate to CI workflows

- **Context**: Adding any new secret or sensitive configuration (API keys, encryption keys, client secrets)
- **Problem**: Secrets added to `appsettings.*.json` get committed to the repo. Secrets added to `docker-compose.yml` or `.env` but not to GitHub Actions workflows cause CI/CD deployments to run without them, leading to runtime crashes in production.
- **Rule**: Never store secrets in appsettings files. Store them in `.env` (gitignored), reference via `${VAR}` in `docker-compose.yml`, and always update `.github/workflows/` to pass the secret from `${{ secrets.VAR }}` to the deployment target. Treat these three locations as a checklist: `.env` + `docker-compose.yml` + CI workflow.
- **Applies to**: any change that introduces a new secret or sensitive configuration value

## Do not apply EF Core migrations to a shared/local dev database until the user has reviewed the model changes

- **Context**: `/10x-implement` phases that add or rename entity properties backed by an EF Core migration (e.g. S-02 workout-data-fetch Phase 1)
- **Problem**: Generating a migration and immediately running `dotnet ef database update` locks in a schema before the user has had a chance to review naming/shape. If they then want a rename (e.g. `StravaEffortId` → `StravaSegmentEffortId`), the migration already applied has to be rolled back (`dotnet ef database update <previous-migration>`), the migration file removed (`dotnet ef migrations remove --force`), and a new one regenerated — extra churn that review-before-apply avoids.
- **Rule**: After generating a migration, stop and let the user review the entity/model diff before running `dotnet ef database update`. Only apply once they explicitly confirm. This is now the default for new migrations during implementation, not just when the user asks for it once.
- **Applies to**: any `/10x-implement` phase that generates an EF Core (or equivalent ORM) migration
