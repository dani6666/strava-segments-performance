namespace StravaSegmentsPerformanceBackend.Models;

public class User
{
    public int Id { get; set; }
    public long StravaAthleteId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime TokenExpiresAtUtc { get; set; }
}
