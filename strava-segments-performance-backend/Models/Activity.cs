namespace StravaSegmentsPerformanceBackend.Models;

public class Activity
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public long StravaActivityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SportType { get; set; } = string.Empty;
    public DateTime StartDateUtc { get; set; }
    public double DistanceMeters { get; set; }
    public int MovingTimeSeconds { get; set; }
    public int ElapsedTimeSeconds { get; set; }
    public bool DetailsFetched { get; set; }
    public DateTime FetchedAtUtc { get; set; }
}
