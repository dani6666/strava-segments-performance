using Microsoft.EntityFrameworkCore;
using StravaSegmentsPerformanceBackend.Data;
using StravaSegmentsPerformanceBackend.Models;
using StravaSegmentsPerformanceBackend.Services;

namespace strava_segments_performance_backend_tests;

public class FitnessTrendQueryTests
{
    [Fact]
    public async Task GetForUserAsync_ReturnsOnlyTheCallingUsersWorkouts()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        db.Activities.AddRange(
            new Activity { Id = 1, UserId = 1, StravaActivityId = 1001, StartDateUtc = new DateTime(2026, 1, 1) },
            new Activity { Id = 2, UserId = 1, StravaActivityId = 1002, StartDateUtc = new DateTime(2026, 1, 2) },
            new Activity { Id = 3, UserId = 2, StravaActivityId = 2001, StartDateUtc = new DateTime(2026, 1, 1) },
            new Activity { Id = 4, UserId = 2, StravaActivityId = 2002, StartDateUtc = new DateTime(2026, 1, 2) });

        // Segments 10, 11, 12 (user 1) and 20, 21, 22 (user 2) each repeat the same pairing so
        // every activity clears the minimum-3-scored-efforts bar.
        db.SegmentEfforts.AddRange(
            new SegmentEffort { Id = 1, ActivityId = 1, StravaSegmentEffortId = 1, StravaSegmentId = 10, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 2, ActivityId = 2, StravaSegmentEffortId = 2, StravaSegmentId = 10, ElapsedTimeSeconds = 120, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 3, ActivityId = 1, StravaSegmentEffortId = 3, StravaSegmentId = 11, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 4, ActivityId = 2, StravaSegmentEffortId = 4, StravaSegmentId = 11, ElapsedTimeSeconds = 120, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 5, ActivityId = 1, StravaSegmentEffortId = 5, StravaSegmentId = 12, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 6, ActivityId = 2, StravaSegmentEffortId = 6, StravaSegmentId = 12, ElapsedTimeSeconds = 120, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 7, ActivityId = 3, StravaSegmentEffortId = 7, StravaSegmentId = 20, ElapsedTimeSeconds = 90, AverageHeartRate = 130, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 8, ActivityId = 4, StravaSegmentEffortId = 8, StravaSegmentId = 20, ElapsedTimeSeconds = 95, AverageHeartRate = 135, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 9, ActivityId = 3, StravaSegmentEffortId = 9, StravaSegmentId = 21, ElapsedTimeSeconds = 90, AverageHeartRate = 130, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 10, ActivityId = 4, StravaSegmentEffortId = 10, StravaSegmentId = 21, ElapsedTimeSeconds = 95, AverageHeartRate = 135, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 11, ActivityId = 3, StravaSegmentEffortId = 11, StravaSegmentId = 22, ElapsedTimeSeconds = 90, AverageHeartRate = 130, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 12, ActivityId = 4, StravaSegmentEffortId = 12, StravaSegmentId = 22, ElapsedTimeSeconds = 95, AverageHeartRate = 135, StartDateUtc = new DateTime(2026, 1, 2) });

        await db.SaveChangesAsync();

        var result = await FitnessTrendQuery.GetForUserAsync(db, userId: 1, from: null, to: null);

        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.True(p.Date == new DateTime(2026, 1, 1) || p.Date == new DateTime(2026, 1, 2)));
    }

    [Fact]
    public async Task GetForUserAsync_FromAndToNarrowTheWindowAndShiftScores()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        db.Activities.AddRange(
            new Activity { Id = 1, UserId = 1, StravaActivityId = 1001, StartDateUtc = new DateTime(2026, 1, 1) },
            new Activity { Id = 2, UserId = 1, StravaActivityId = 1002, StartDateUtc = new DateTime(2026, 1, 2) },
            new Activity { Id = 3, UserId = 1, StravaActivityId = 1003, StartDateUtc = new DateTime(2026, 1, 3) });

        // Segments 10, 11, 12 repeat the same worst/middle/best ranking so every activity
        // clears the minimum-3-scored-efforts bar.
        db.SegmentEfforts.AddRange(
            new SegmentEffort { Id = 1, ActivityId = 1, StravaSegmentEffortId = 1, StravaSegmentId = 10, ElapsedTimeSeconds = 200, AverageHeartRate = 160, StartDateUtc = new DateTime(2026, 1, 1) }, // worst
            new SegmentEffort { Id = 2, ActivityId = 2, StravaSegmentEffortId = 2, StravaSegmentId = 10, ElapsedTimeSeconds = 150, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) }, // middle
            new SegmentEffort { Id = 3, ActivityId = 3, StravaSegmentEffortId = 3, StravaSegmentId = 10, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) }, // best
            new SegmentEffort { Id = 4, ActivityId = 1, StravaSegmentEffortId = 4, StravaSegmentId = 11, ElapsedTimeSeconds = 200, AverageHeartRate = 160, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 5, ActivityId = 2, StravaSegmentEffortId = 5, StravaSegmentId = 11, ElapsedTimeSeconds = 150, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 6, ActivityId = 3, StravaSegmentEffortId = 6, StravaSegmentId = 11, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) },
            new SegmentEffort { Id = 7, ActivityId = 1, StravaSegmentEffortId = 7, StravaSegmentId = 12, ElapsedTimeSeconds = 200, AverageHeartRate = 160, StartDateUtc = new DateTime(2026, 1, 1) },
            new SegmentEffort { Id = 8, ActivityId = 2, StravaSegmentEffortId = 8, StravaSegmentId = 12, ElapsedTimeSeconds = 150, AverageHeartRate = 150, StartDateUtc = new DateTime(2026, 1, 2) },
            new SegmentEffort { Id = 9, ActivityId = 3, StravaSegmentEffortId = 9, StravaSegmentId = 12, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 3) });

        await db.SaveChangesAsync();

        var fullWindow = await FitnessTrendQuery.GetForUserAsync(db, userId: 1, from: null, to: null);
        var narrowedWindow = await FitnessTrendQuery.GetForUserAsync(db, userId: 1, from: new DateTime(2026, 1, 2), to: null);

        Assert.Equal(3, fullWindow.Count);
        Assert.Equal(2, narrowedWindow.Count);
        Assert.DoesNotContain(narrowedWindow, p => p.Date == new DateTime(2026, 1, 1));

        // Same workout (Jan 2, the "middle" effort) scores differently depending on the window:
        // it's the worst of the two remaining once Jan 1 drops out, not the middle of three.
        var middleInFullWindow = fullWindow.Single(p => p.Date == new DateTime(2026, 1, 2)).Score;
        var middleInNarrowedWindow = narrowedWindow.Single(p => p.Date == new DateTime(2026, 1, 2)).Score;
        Assert.NotEqual(middleInFullWindow, middleInNarrowedWindow);
    }

    [Fact]
    public async Task GetForUserAsync_UserWithNoRepeatedSegments_ReturnsEmpty()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        db.Activities.Add(new Activity { Id = 1, UserId = 1, StravaActivityId = 1001, StartDateUtc = new DateTime(2026, 1, 1) });
        db.SegmentEfforts.Add(new SegmentEffort { Id = 1, ActivityId = 1, StravaSegmentEffortId = 1, StravaSegmentId = 10, ElapsedTimeSeconds = 100, AverageHeartRate = 140, StartDateUtc = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();

        var result = await FitnessTrendQuery.GetForUserAsync(db, userId: 1, from: null, to: null);

        Assert.Empty(result);
    }
}
