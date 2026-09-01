using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StravaSegmentsPerformanceBackend.Models;

namespace strava_segments_performance_backend_tests;

/// <summary>
/// Endpoint-level authorization tests for the two authenticated read endpoints, proving
/// Risk #5 (context/foundation/test-plan.md): a caller only ever sees their own data.
/// Each test owns a fresh <see cref="CustomWebApplicationFactory"/> so seeded data never
/// bleeds across facts.
/// </summary>
public class EndpointAuthorizationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CustomWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private sealed record TrendPointDto(DateTime Date, double Score);

    private sealed record FetchStatusDto(string Status, string? Stage, int ActivitiesProcessed, int? TotalToProcess, string? ErrorMessage);

    [Fact]
    public async Task FetchStatus_AuthenticatedRequest_ReturnsOk()
    {
        const long stravaAthleteId = 1001;

        await _factory.SeedAsync(db =>
        {
            db.Users.Add(new User { Id = 1, StravaAthleteId = stravaAthleteId });
        });

        var client = _factory.CreateClientAs(stravaAthleteId);
        var response = await client.GetAsync("/api/workouts/fetch-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FitnessTrend_ReturnsOnlyCallingUsersPoints()
    {
        const long athleteA = 2001;
        const long athleteB = 2002;

        // Athlete A (user 1) has 2 workout dates; athlete B (user 2) has 3 - deliberately
        // asymmetric so a leak or a swapped user resolution changes the returned count/dates,
        // not just coincidentally matches.
        await _factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new User { Id = 1, StravaAthleteId = athleteA },
                new User { Id = 2, StravaAthleteId = athleteB });

            db.Activities.AddRange(
                new Activity { Id = 1, UserId = 1, StravaActivityId = 1001, StartDateUtc = new DateTime(2026, 1, 1) },
                new Activity { Id = 2, UserId = 1, StravaActivityId = 1002, StartDateUtc = new DateTime(2026, 1, 2) },
                new Activity { Id = 3, UserId = 2, StravaActivityId = 2001, StartDateUtc = new DateTime(2026, 1, 1) },
                new Activity { Id = 4, UserId = 2, StravaActivityId = 2002, StartDateUtc = new DateTime(2026, 1, 2) },
                new Activity { Id = 5, UserId = 2, StravaActivityId = 2003, StartDateUtc = new DateTime(2026, 1, 3) });

            // Segments 10/11/12 (user 1) and 20/21/22 (user 2) each repeat across every one
            // of their user's activities, so every activity clears the min-3-scored-efforts bar.
            db.SegmentEfforts.AddRange(
                new SegmentEffort { Id = 1, ActivityId = 1, StravaSegmentEffortId = 1, StravaSegmentId = 10, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 2, ActivityId = 2, StravaSegmentEffortId = 2, StravaSegmentId = 10, ElapsedTimeSeconds = 120, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 3, ActivityId = 1, StravaSegmentEffortId = 3, StravaSegmentId = 11, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 4, ActivityId = 2, StravaSegmentEffortId = 4, StravaSegmentId = 11, ElapsedTimeSeconds = 120, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 5, ActivityId = 1, StravaSegmentEffortId = 5, StravaSegmentId = 12, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 6, ActivityId = 2, StravaSegmentEffortId = 6, StravaSegmentId = 12, ElapsedTimeSeconds = 120, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 7, ActivityId = 3, StravaSegmentEffortId = 7, StravaSegmentId = 20, ElapsedTimeSeconds = 90, AverageHeartRate = 130, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 8, ActivityId = 4, StravaSegmentEffortId = 8, StravaSegmentId = 20, ElapsedTimeSeconds = 95, AverageHeartRate = 135, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 9, ActivityId = 5, StravaSegmentEffortId = 9, StravaSegmentId = 20, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) },
                new SegmentEffort { Id = 10, ActivityId = 3, StravaSegmentEffortId = 10, StravaSegmentId = 21, ElapsedTimeSeconds = 90, AverageHeartRate = 130, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 11, ActivityId = 4, StravaSegmentEffortId = 11, StravaSegmentId = 21, ElapsedTimeSeconds = 95, AverageHeartRate = 135, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 12, ActivityId = 5, StravaSegmentEffortId = 12, StravaSegmentId = 21, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) },
                new SegmentEffort { Id = 13, ActivityId = 3, StravaSegmentEffortId = 13, StravaSegmentId = 22, ElapsedTimeSeconds = 90, AverageHeartRate = 130, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 14, ActivityId = 4, StravaSegmentEffortId = 14, StravaSegmentId = 22, ElapsedTimeSeconds = 95, AverageHeartRate = 135, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 15, ActivityId = 5, StravaSegmentEffortId = 15, StravaSegmentId = 22, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) });
        });

        var pointsA = await _factory.CreateClientAs(athleteA)
            .GetFromJsonAsync<List<TrendPointDto>>("/api/analysis/fitness-trend", JsonOptions);
        var pointsB = await _factory.CreateClientAs(athleteB)
            .GetFromJsonAsync<List<TrendPointDto>>("/api/analysis/fitness-trend", JsonOptions);

        Assert.NotNull(pointsA);
        Assert.NotNull(pointsB);
        Assert.Equal(2, pointsA!.Count);
        Assert.Equal(3, pointsB!.Count);
        Assert.Equal([new DateTime(2026, 1, 1), new DateTime(2026, 1, 2)], pointsA.Select(p => p.Date).OrderBy(d => d));
        Assert.Equal(
            [new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), new DateTime(2026, 1, 3)],
            pointsB.Select(p => p.Date).OrderBy(d => d));
    }

    [Fact]
    public async Task FitnessTrend_FromNarrowsTheWindowThroughTheEndpoint()
    {
        const long stravaAthleteId = 2101;

        // Same worst/middle/best pattern as FitnessTrendQueryTests: segments repeat their
        // ranking across 3 dates so every activity clears the min-3-scored-efforts bar.
        await _factory.SeedAsync(db =>
        {
            db.Users.Add(new User { Id = 1, StravaAthleteId = stravaAthleteId });

            db.Activities.AddRange(
                new Activity { Id = 1, UserId = 1, StravaActivityId = 1001, StartDateUtc = new DateTime(2026, 1, 1) },
                new Activity { Id = 2, UserId = 1, StravaActivityId = 1002, StartDateUtc = new DateTime(2026, 1, 2) },
                new Activity { Id = 3, UserId = 1, StravaActivityId = 1003, StartDateUtc = new DateTime(2026, 1, 3) });

            db.SegmentEfforts.AddRange(
                new SegmentEffort { Id = 1, ActivityId = 1, StravaSegmentEffortId = 1, StravaSegmentId = 10, ElapsedTimeSeconds = 200, AverageHeartRate = 160, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 2, ActivityId = 2, StravaSegmentEffortId = 2, StravaSegmentId = 10, ElapsedTimeSeconds = 150, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 3, ActivityId = 3, StravaSegmentEffortId = 3, StravaSegmentId = 10, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) },
                new SegmentEffort { Id = 4, ActivityId = 1, StravaSegmentEffortId = 4, StravaSegmentId = 11, ElapsedTimeSeconds = 200, AverageHeartRate = 160, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 5, ActivityId = 2, StravaSegmentEffortId = 5, StravaSegmentId = 11, ElapsedTimeSeconds = 150, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 6, ActivityId = 3, StravaSegmentEffortId = 6, StravaSegmentId = 11, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) },
                new SegmentEffort { Id = 7, ActivityId = 1, StravaSegmentEffortId = 7, StravaSegmentId = 12, ElapsedTimeSeconds = 200, AverageHeartRate = 160, StartDateUtc = new DateTime(2026, 1, 1) },
                new SegmentEffort { Id = 8, ActivityId = 2, StravaSegmentEffortId = 8, StravaSegmentId = 12, ElapsedTimeSeconds = 150, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
                new SegmentEffort { Id = 9, ActivityId = 3, StravaSegmentEffortId = 9, StravaSegmentId = 12, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) });
        });

        var client = _factory.CreateClientAs(stravaAthleteId);

        var fullWindow = await client.GetFromJsonAsync<List<TrendPointDto>>("/api/analysis/fitness-trend", JsonOptions);
        var narrowedWindow = await client.GetFromJsonAsync<List<TrendPointDto>>("/api/analysis/fitness-trend?from=2026-01-02", JsonOptions);

        Assert.NotNull(fullWindow);
        Assert.NotNull(narrowedWindow);
        Assert.Equal(3, fullWindow!.Count);
        Assert.Equal(2, narrowedWindow!.Count);
        Assert.DoesNotContain(narrowedWindow, p => p.Date == new DateTime(2026, 1, 1));

        // Same workout (Jan 2, the "middle" effort) scores differently depending on the window -
        // it's the worst of the two remaining once Jan 1 drops out, not the middle of three.
        var middleInFullWindow = fullWindow.Single(p => p.Date == new DateTime(2026, 1, 2)).Score;
        var middleInNarrowedWindow = narrowedWindow.Single(p => p.Date == new DateTime(2026, 1, 2)).Score;
        Assert.NotEqual(middleInFullWindow, middleInNarrowedWindow);
    }

    [Fact]
    public async Task FetchStatus_ReturnsOnlyCallingUsersStatus()
    {
        const long athleteA = 2201;
        const long athleteB = 2202;

        await _factory.SeedAsync(db =>
        {
            db.Users.AddRange(
                new User { Id = 1, StravaAthleteId = athleteA },
                new User { Id = 2, StravaAthleteId = athleteB });

            db.WorkoutFetchStatuses.AddRange(
                new WorkoutFetchStatus { UserId = 1, Status = FetchStatusState.Running, Stage = FetchStage.FetchingDetails, ActivitiesProcessed = 5, TotalToProcess = 10 },
                new WorkoutFetchStatus { UserId = 2, Status = FetchStatusState.Completed, ActivitiesProcessed = 20, TotalToProcess = 20 });
        });

        var statusA = await _factory.CreateClientAs(athleteA)
            .GetFromJsonAsync<FetchStatusDto>("/api/workouts/fetch-status", JsonOptions);
        var statusB = await _factory.CreateClientAs(athleteB)
            .GetFromJsonAsync<FetchStatusDto>("/api/workouts/fetch-status", JsonOptions);

        Assert.NotNull(statusA);
        Assert.Equal("running", statusA!.Status);
        Assert.Equal(5, statusA.ActivitiesProcessed);
        Assert.Equal(10, statusA.TotalToProcess);

        Assert.NotNull(statusB);
        Assert.Equal("completed", statusB!.Status);
        Assert.Equal(20, statusB.ActivitiesProcessed);
        Assert.Equal(20, statusB.TotalToProcess);
    }

    [Fact]
    public async Task FetchStatus_NoRowForCaller_ReturnsIdle()
    {
        const long stravaAthleteId = 2301;

        await _factory.SeedAsync(db =>
        {
            db.Users.Add(new User { Id = 1, StravaAthleteId = stravaAthleteId });
        });

        var status = await _factory.CreateClientAs(stravaAthleteId)
            .GetFromJsonAsync<FetchStatusDto>("/api/workouts/fetch-status", JsonOptions);

        Assert.NotNull(status);
        Assert.Equal("idle", status!.Status);
        Assert.Equal(0, status.ActivitiesProcessed);
        Assert.Null(status.TotalToProcess);
    }
}

