namespace StravaSegmentsPerformanceBackend.Models;

public enum FetchStatusState
{
    Idle,
    Running,
    Completed,
    Failed,
    Interrupted
}

public enum FetchStage
{
    ListingActivities,
    FetchingDetails
}

public class WorkoutFetchStatus
{
    public int UserId { get; set; }
    public FetchStatusState Status { get; set; }
    public FetchStage? Stage { get; set; }
    public int ActivitiesProcessed { get; set; }
    public int? TotalToProcess { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
