using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Services;

public class StravaApiClient
{
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(15);

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
        User user, int page, int perPage, CancellationToken ct)
    {
        using var response = await SendAsync(
            user, () => new HttpRequestMessage(HttpMethod.Get, $"athlete/activities?page={page}&per_page={perPage}"), ct);

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
                await Task.Delay(wait, _timeProvider, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }
    }
}
