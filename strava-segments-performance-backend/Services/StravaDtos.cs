using System.Text.Json.Serialization;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Services;

public record StravaSegmentSummary(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

public record StravaSegmentEffort(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("segment")] StravaSegmentSummary Segment,
    [property: JsonPropertyName("elapsed_time")] int ElapsedTime,
    [property: JsonPropertyName("average_heartrate")] double? AverageHeartrate,
    [property: JsonPropertyName("start_date")] DateTime StartDate);

public record StravaActivitySummary(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sport_type")] string SportType,
    [property: JsonPropertyName("start_date")] DateTime StartDate,
    [property: JsonPropertyName("distance")] double Distance,
    [property: JsonPropertyName("moving_time")] int MovingTime,
    [property: JsonPropertyName("elapsed_time")] int ElapsedTime,
    [property: JsonPropertyName("has_heartrate")] bool HasHeartrate);

public record StravaActivityDetail(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sport_type")] string SportType,
    [property: JsonPropertyName("start_date")] DateTime StartDate,
    [property: JsonPropertyName("distance")] double Distance,
    [property: JsonPropertyName("moving_time")] int MovingTime,
    [property: JsonPropertyName("elapsed_time")] int ElapsedTime,
    [property: JsonPropertyName("has_heartrate")] bool HasHeartrate,
    [property: JsonPropertyName("segment_efforts")] List<StravaSegmentEffort> SegmentEfforts);

public static class StravaMappingExtensions
{
    public static readonly HashSet<string> CyclingSportTypes = new()
    {
        "Ride",
        "MountainBikeRide",
        "GravelRide",
        "VirtualRide",
        "Handcycle",
        "Velomobile"
    };

    public static Activity ToActivity(this StravaActivitySummary summary, int userId) => new()
    {
        UserId = userId,
        StravaActivityId = summary.Id,
        Name = summary.Name,
        SportType = summary.SportType,
        StartDateUtc = summary.StartDate,
        DistanceMeters = summary.Distance,
        MovingTimeSeconds = summary.MovingTime,
        ElapsedTimeSeconds = summary.ElapsedTime,
        DetailsFetched = false,
        FetchedAtUtc = DateTime.UtcNow
    };

    public static IEnumerable<SegmentEffort> ToSegmentEfforts(this StravaActivityDetail detail, int activityId) =>
        detail.SegmentEfforts.Select(effort => new SegmentEffort
        {
            ActivityId = activityId,
            StravaSegmentEffortId = effort.Id,
            StravaSegmentId = effort.Segment.Id,
            SegmentName = effort.Segment.Name,
            ElapsedTimeSeconds = effort.ElapsedTime,
            AverageHeartRate = effort.AverageHeartrate,
            StartDateUtc = effort.StartDate
        });
}
