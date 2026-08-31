namespace StravaSegmentsPerformanceBackend.Services;

public static class FetchWindowValidator
{
    public static bool IsValidRange(DateTime? afterUtc, DateTime? beforeUtc) =>
        afterUtc is null || beforeUtc is null || afterUtc <= beforeUtc;
}
