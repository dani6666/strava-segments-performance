---
project: "Strava Segments Performance"
context_type: greenfield
created: 2026-05-29
updated: 2026-05-29
product_type: web-app
target_scale:
  users: small
  qps: low
  data_volume: small
timeline_budget:
  mvp_weeks: 3
  hard_deadline: 2026-07-15
  after_hours_only: true
checkpoint:
  current_phase: 8
  phases_completed: [1, 2, 3, 4, 5, 6, 7]
  gray_areas_resolved:
    - topic: "pain category"
      decision: "Data trapped somewhere — Strava has the data but doesn't surface segment-level fitness trends"
    - topic: "insight"
      decision: "Segment-level comparison eliminates surface/elevation bias; HR + time combined detects fitness gains invisible to time-only tools"
    - topic: "primary persona scope"
      decision: "Individuals across many orgs — any Strava user who repeats segments"
    - topic: "auth strategy"
      decision: "OAuth via Strava; no separate account"
    - topic: "role model"
      decision: "Flat — all users equal, each sees only their own data"
    - topic: "product type"
      decision: "web-app"
    - topic: "target scale"
      decision: "small — just me or a handful; domain rule is scale-invariant"
    - topic: "timeline"
      decision: "3 weeks after-hours; hard deadline 2026-07-15"
  frs_drafted: 4
  quality_check_status: accepted
---

## Vision & Problem Statement

Strava traps segment-level performance data without surfacing personal fitness trends. The raw data exists but isn't synthesized into a usable progress signal. After cycling workouts, users reviewing their segment results cannot determine whether their fitness is improving or declining — Strava shows relative effort and fitness using raw workout-level data, and existing tools compare absolute times, but neither accounts for the relationship between time and heart rate on specific segments.

The insight: by comparing performance on concrete segments (not full workouts), surface and elevation variables are controlled. By incorporating heart rate alongside time, the app detects fitness gains invisible to time-only comparisons — same segment time at lower heart rate means improved fitness. This segment-level, HR-aware scoring is what distinguishes the product from Strava's built-in analytics and from third-party tools that compare absolute times alone.

## User & Persona

**Primary persona:** A cyclist who uses Strava, repeats the same road segments regularly, and wants to understand their fitness trajectory over time. They train seriously (whether amateur or competitive), track workouts with a heart rate monitor, and are frustrated that existing tools don't tell them whether their training is actually working at the segment level. They are individuals across many contexts — not tied to a single team or organization.

## Access Control

OAuth via Strava. No separate account creation — authentication and data access come together through the Strava OAuth flow. Flat user model: all users are equal, each sees only their own data. No admin role, no sharing between users, no role separation in the MVP.

## Success Criteria

### Primary
- User logs in via Strava OAuth, selects a timeframe, the app fetches and analyzes segment performances (time + HR), and displays a fitness trend chart (0–100 score over time).

### Secondary
- Cached workouts are reused on repeat analysis — no redundant re-fetching from Strava, making subsequent analyses faster.

### Guardrails
- Strava API compliance: rate limits are respected, user's Strava account is never put at risk by the app's behavior.

## Functional Requirements

- FR-001: User can authenticate via Strava OAuth. Priority: must-have
  > Socrates: Counter-argument considered: "Strava controls your API access — platform dependency risk." Resolution: kept; Strava is the only viable source for segment data with HR — the dependency is inherent to the product. Accept the risk.
- FR-002: User can select a timeframe for workout analysis. Priority: nice-to-have
  > Socrates: Counter-argument considered: "Analyzing all data by default could be too slow." Resolution: kept as nice-to-have; default to all data for v1, add filtering later. Chart IS the timeframe view.
- FR-003: User can trigger analysis of their workouts (fetching from Strava happens automatically; previously fetched workouts are reused). Priority: must-have
  > Socrates: Counter-argument considered: "Long fetch times (Strava rate limits) make a single trigger button misleading — user thinks it's broken." Resolution: kept with UX clarity; UI shows fetch progress separately from analysis so the user knows it's working.
- FR-004: User can view a fitness trend chart (0–100 score over time). Priority: must-have
  > Socrates: Counter-argument considered: "If the scoring formula is flawed, the chart is garbage-in-garbage-out." Resolution: kept; formula validation is the core risk to iterate on — correctness is the primary thing to test and refine.

## User Stories

### US-01: User views their fitness trend after analysis

- **Given** a user authenticated via Strava OAuth with cycling workouts containing segment data and HR
- **When** they trigger analysis of their workouts
- **Then** they see a fitness trend chart showing a 0–100 score over time, with one data point per workout

#### Acceptance Criteria
- Chart displays one data point per workout — multiple segment results within a workout are aggregated into a single overall fitness score
- Score of 100 = peak fitness within the analyzed window; 0 = lowest fitness
- Previously fetched workouts are reused without re-fetching from Strava
- Analysis of up to 1000 workouts completes within 30 seconds

## Business Logic

The app scores each workout's fitness level (0–100) by comparing segment elapsed times and average heart rates against the user's historical performances on those same segments, where improved fitness = same or faster time at same or lower heart rate.

The rule consumes two inputs per segment effort: the elapsed time on the segment and the average heart rate during that effort. Multiple segment scores within a single workout are aggregated into one overall fitness score for that workout.

The output is a 0–100 fitness score per workout, self-relative to the user's own history within the analyzed window. A score of 100 represents the user's peak fitness (best combined time + HR performance) and 0 represents their lowest fitness within that window.

The user encounters this score as one data point per workout on the fitness trend chart — a longitudinal view of their fitness trajectory over time.

## Non-Functional Requirements

- User-perceived analysis time for up to 1000 workouts is under 30 seconds from trigger to chart render (fetching from Strava may take longer and is shown separately with progress indication).

## Non-Goals

- Weather/surface conditions are NOT factored into scoring. Segment comparison already controls for elevation; weather/wind/temperature are too complex for v1.
- No support for other fitness platforms beyond Strava (Garmin Connect, Wahoo, Polar, etc.). Strava is the sole data source.
- No social features: no sharing, no comparing with other users, no leaderboards.
- No user-configurable scoring parameters. The scoring formula ships as-is; no user knobs to tweak weights.

## Quality cross-check

All elements present. No gaps. Status: accepted.
