using System.Net.Http.Json;
using System.Text.Json.Serialization;
using StravaSegmentsPerformanceBackend.Data;
using StravaSegmentsPerformanceBackend.Models;

namespace StravaSegmentsPerformanceBackend.Services;

public record StravaTokenRefreshResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_at")] long ExpiresAt);

public class StravaTokenService
{
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);
    private const string TokenEndpoint = "https://www.strava.com/oauth/token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppDbContext _db;
    private readonly TokenEncryptionService _tokenEncryption;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public StravaTokenService(
        IHttpClientFactory httpClientFactory,
        AppDbContext db,
        TokenEncryptionService tokenEncryption,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _db = db;
        _tokenEncryption = tokenEncryption;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    public async Task<string> GetValidAccessTokenAsync(User user, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now + RefreshBuffer >= user.TokenExpiresAtUtc)
            return await ForceRefreshAsync(user, ct);

        return _tokenEncryption.Decrypt(user.AccessToken);
    }

    public async Task<string> ForceRefreshAsync(User user, CancellationToken ct)
    {
        var refreshToken = _tokenEncryption.Decrypt(user.RefreshToken);
        var httpClient = _httpClientFactory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _configuration["Strava:ClientId"]!,
                ["client_secret"] = _configuration["Strava:ClientSecret"]!,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StravaTokenRefreshResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Strava returned an empty token refresh response.");

        user.AccessToken = _tokenEncryption.Encrypt(payload.AccessToken);
        user.RefreshToken = _tokenEncryption.Encrypt(payload.RefreshToken);
        user.TokenExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt).UtcDateTime;

        await _db.SaveChangesAsync(ct);

        return payload.AccessToken;
    }
}
