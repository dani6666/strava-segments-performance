using System.Net.Http.Headers;
using System.Net.Http.Json;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Services;

public class StravaApiClient
{
    private readonly HttpClient _httpClient;
    private readonly TokenEncryptionService _tokenEncryption;

    public StravaApiClient(HttpClient httpClient, TokenEncryptionService tokenEncryption)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://www.strava.com/api/v3/");
        _tokenEncryption = tokenEncryption;
    }

    public async Task<IReadOnlyList<StravaActivitySummary>> ListActivitiesPageAsync(
        User user, int page, int perPage, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"athlete/activities?page={page}&per_page={perPage}");
        AttachAuthorization(request, user);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var activities = await response.Content.ReadFromJsonAsync<List<StravaActivitySummary>>(cancellationToken: ct);
        return activities ?? [];
    }

    public async Task<StravaActivityDetail> GetActivityDetailAsync(User user, long stravaActivityId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"activities/{stravaActivityId}?include_all_efforts=true");
        AttachAuthorization(request, user);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var detail = await response.Content.ReadFromJsonAsync<StravaActivityDetail>(cancellationToken: ct);
        return detail ?? throw new InvalidOperationException($"Strava returned an empty body for activity {stravaActivityId}.");
    }

    private void AttachAuthorization(HttpRequestMessage request, User user)
    {
        var accessToken = _tokenEncryption.Decrypt(user.AccessToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
