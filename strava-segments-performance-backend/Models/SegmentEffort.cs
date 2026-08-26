namespace StravaSegmentsPerformanceBackend.Models;

public class SegmentEffort
{
    public int Id { get; set; }
    public int ActivityId { get; set; }
    public long StravaSegmentEffortId { get; set; }
    public long StravaSegmentId { get; set; }
    public string SegmentName { get; set; } = string.Empty;
    public int ElapsedTimeSeconds { get; set; }
    public double? AverageHeartRate { get; set; }
    public DateTime StartDateUtc { get; set; }
}
