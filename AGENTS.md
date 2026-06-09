# CLAUDE.md

## Project Overview

**Strava Segments Performance** — a web app that scores a cyclist's fitness (0–100) per workout by comparing segment elapsed time + heart rate against personal history on those same segments. Same time at lower HR = fitness gain.

Two independently deployable projects:
- `strava-segments-performance-backend/` — .NET 10 minimal-API REST backend (C#)
- `strava-segments-performance/` — Angular 21 SPA frontend (TypeScript + SCSS)

## Strava API Constraints

Rate limits must be respected at all times. The backend is the sole consumer of the Strava API — the frontend never calls Strava directly. Previously fetched workouts are cached and reused on subsequent analyses.

## Key Business Logic

The fitness score is self-relative (0 = user's personal worst, 100 = personal best within the analyzed window). Scoring inputs per segment effort: elapsed time + average heart rate. Multiple segment scores within one workout aggregate into a single workout score. Cached workouts must be reused — never re-fetch a workout already stored locally.

## Architecture

### Backend
- Entry point: `strava-segments-performance-backend/Program.cs` — minimal API wiring only (no controllers)
- Namespace: `strava_segments_performance_backend`
- Nullable and implicit usings enabled; target framework `net10.0`
- Background jobs for Strava data fetching use .NET hosted services (`IHostedService`)
- Strava OAuth handled via ASP.NET Core OAuth middleware
- OpenAPI served at `/openapi` in development

### Frontend
- Entry: `strava-segments-performance/src/main.ts` → `app.config.ts` → `app.ts`
- Routing configured in `app.routes.ts`; standalone components (no NgModule)
- SCSS for styles; strict TypeScript (`tsconfig.json`)
- Communicates with backend over HTTP; no SSR

## Commands

### Backend (`strava-segments-performance-backend/`)
```
dotnet run          # start dev server
dotnet build        # build
dotnet test         # run tests
```

### Frontend (`strava-segments-performance/`)
```
npm start           # ng serve (dev server)
npm run build       # ng build (production)
npm test            # ng test (Karma)
npm run watch       # ng build --watch --configuration development
```