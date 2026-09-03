using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Services;

public class StravaApiClient
{
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SingleCallRetryTimeout = TimeSpan.FromHours(1);

    private readonly HttpClient _httpClient;
    private readonly IStravaTokenService _tokenService;
    private readonly TimeProvider _timeProvider;

    public StravaApiClient(HttpClient httpClient, IStravaTokenService tokenService, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://www.strava.com/api/v3/");
        _tokenService = tokenService;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<StravaActivitySummary>> ListActivitiesPageAsync(
        User user, int page, int perPage, DateTime? afterUtc, DateTime? beforeUtc, CancellationToken ct)
    {
        var query = $"athlete/activities?page={page}&per_page={perPage}";
        if (afterUtc is not null)
            query += $"&after={new DateTimeOffset(afterUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}";
        if (beforeUtc is not null)
            query += $"&before={new DateTimeOffset(beforeUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds()}";

        using var response = await SendAsync(
            user, () => new HttpRequestMessage(HttpMethod.Get, query), ct);

        var activities = await response.Content.ReadFromJsonAsync<List<StravaActivitySummary>>(cancellationToken: ct);
        return activities ?? [];
    }

    public async Task<StravaActivityDetail> GetActivityDetailAsync(User user, long stravaActivityId, CancellationToken ct)
    {
        using var response = await SendAsync(
            user, () => new HttpRequestMessage(HttpMethod.Get, $"activities/{stravaActivityId}?include_all_efforts=true"), ct);

        var detail = await response.Content.ReadFromJsonAsync<StravaActivityDetail>(cancellationToken: ct);
        return detail ?? throw new InvalidOperationException($"Strava returned an empty body for activity {stravaActivityId}.");
    }

    private async Task<HttpResponseMessage> SendAsync(User user, Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var accessToken = await _tokenService.GetValidAccessTokenAsync(user, ct);
        var hasRetriedAfterUnauthorized = false;
        var retryDeadline = _timeProvider.GetUtcNow() + SingleCallRetryTimeout;

        while (true)
        {
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && !hasRetriedAfterUnauthorized)
            {
                hasRetriedAfterUnauthorized = true;
                response.Dispose();
                accessToken = await _tokenService.ForceRefreshAsync(user, ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? DefaultRateLimitWait;
                response.Dispose();
                if (_timeProvider.GetUtcNow() + wait < retryDeadline)
                    throw new TimeoutException(
                        $"Strava rate-limit retries for '{request.RequestUri}' exceeded the {SingleCallRetryTimeout.TotalHours}h limit.");
                await Task.Delay(wait, _timeProvider, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dispose on the failure path; the caller owns disposal of a successful response.
                try
                {
                    response.EnsureSuccessStatusCode();
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            return response;
        }
    }
}
