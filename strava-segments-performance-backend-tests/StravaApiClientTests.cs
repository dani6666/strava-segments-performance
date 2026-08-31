using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StravaSegmentsPerformanceBackend.Models;
using StravaSegmentsPerformanceBackend.Services;
using Xunit;

namespace strava_segments_performance_backend_tests;

public class StravaApiClientTests
{
    private static readonly User TestUser = new() { Id = 1, StravaAthleteId = 42 };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public List<HttpRequestMessage> Requests { get; } = [];

        public StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var factory = _responses.Count > 0 ? _responses.Dequeue() : _responses.Last();
            return Task.FromResult(factory(request));
        }
    }

    private sealed class FakeTokenService : IStravaTokenService
    {
        public string CurrentToken = "initial-token";
        public int ForceRefreshCalls;

        public Task<string> GetValidAccessTokenAsync(User user, CancellationToken ct) => Task.FromResult(CurrentToken);

        public Task<string> ForceRefreshAsync(User user, CancellationToken ct)
        {
            ForceRefreshCalls++;
            CurrentToken = "refreshed-token";
            return Task.FromResult(CurrentToken);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) => new(statusCode)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task ListActivitiesPageAsync_ReturnsFewerThanPerPage_WhenStravaReturnsShortPage()
    {
        var shortPage = new[]
        {
            new { id = 1, name = "Ride 1", sport_type = "Ride", start_date = "2026-01-01T00:00:00Z", distance = 1000.0, moving_time = 60, elapsed_time = 65, has_heartrate = true },
            new { id = 2, name = "Ride 2", sport_type = "Ride", start_date = "2026-01-02T00:00:00Z", distance = 2000.0, moving_time = 120, elapsed_time = 125, has_heartrate = true }
        };
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, shortPage));
        var client = new StravaApiClient(new HttpClient(handler), new FakeTokenService(), new FakeTimeProvider());

        var result = await client.ListActivitiesPageAsync(TestUser, page: 1, perPage: 50, afterUtc: null, beforeUtc: null, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.True(result.Count < 50);
    }

    [Fact]
    public async Task ListActivitiesPageAsync_AppendsAfterAndBefore_WhenBothBoundsGiven()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        var client = new StravaApiClient(new HttpClient(handler), new FakeTokenService(), new FakeTimeProvider());
        var afterUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var beforeUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await client.ListActivitiesPageAsync(TestUser, page: 1, perPage: 50, afterUtc, beforeUtc, CancellationToken.None);

        var query = handler.Requests[0].RequestUri!.Query;
        Assert.Contains($"after={new DateTimeOffset(afterUtc).ToUnixTimeSeconds()}", query);
        Assert.Contains($"before={new DateTimeOffset(beforeUtc).ToUnixTimeSeconds()}", query);
    }

    [Fact]
    public async Task ListActivitiesPageAsync_OmitsAfterAndBefore_WhenBoundsAreNull()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        var client = new StravaApiClient(new HttpClient(handler), new FakeTokenService(), new FakeTimeProvider());

        await client.ListActivitiesPageAsync(TestUser, page: 1, perPage: 50, afterUtc: null, beforeUtc: null, CancellationToken.None);

        var query = handler.Requests[0].RequestUri!.Query;
        Assert.DoesNotContain("after=", query);
        Assert.DoesNotContain("before=", query);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("2026-01-01", null, true)]
    [InlineData(null, "2026-01-01", true)]
    [InlineData("2026-01-01", "2026-02-01", true)]
    [InlineData("2026-01-01", "2026-01-01", true)]
    [InlineData("2026-02-01", "2026-01-01", false)]
    public void FetchWindowValidator_IsValidRange_RejectsOnlyWhenAfterIsLaterThanBefore(
        string? after, string? before, bool expected)
    {
        DateTime? afterUtc = after is null ? null : DateTime.Parse(after);
        DateTime? beforeUtc = before is null ? null : DateTime.Parse(before);

        Assert.Equal(expected, FetchWindowValidator.IsValidRange(afterUtc, beforeUtc));
    }

    [Fact]
    public async Task SendAsync_OnUnauthorized_RefreshesTokenExactlyOnceAndRetriesWithNewToken()
    {
        var tokenService = new FakeTokenService();
        var handler = new StubHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        var client = new StravaApiClient(new HttpClient(handler), tokenService, new FakeTimeProvider());

        await client.ListActivitiesPageAsync(TestUser, page: 1, perPage: 50, afterUtc: null, beforeUtc: null, CancellationToken.None);

        Assert.Equal(1, tokenService.ForceRefreshCalls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("refreshed-token", handler.Requests[1].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SendAsync_OnTooManyRequests_WaitsViaTimeProviderThenRetriesSameRequest()
    {
        var timeProvider = new FakeTimeProvider();
        var handler = new StubHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            },
            _ => JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        var client = new StravaApiClient(new HttpClient(handler), new FakeTokenService(), timeProvider);

        var task = client.ListActivitiesPageAsync(TestUser, page: 1, perPage: 50, afterUtc: null, beforeUtc: null, CancellationToken.None);

        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var result = await task;

        Assert.Empty(result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].RequestUri, handler.Requests[1].RequestUri);
    }

    [Fact]
    public async Task SendAsync_WhenRateLimitWaitWouldExceedThePerCallCap_ThrowsTimeoutInsteadOfWaiting()
    {
        // Retry-After longer than the 1h per-call retry cap: the client must give up
        // rather than schedule a wait that pushes past the deadline.
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(61));
            return response;
        });
        var client = new StravaApiClient(new HttpClient(handler), new FakeTokenService(), new FakeTimeProvider());

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.ListActivitiesPageAsync(TestUser, page: 1, perPage: 50, afterUtc: null, beforeUtc: null, CancellationToken.None));

        // One attempt, then aborted — never retried past the cap.
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("Ride", true, true)]
    [InlineData("MountainBikeRide", true, true)]
    [InlineData("Run", true, false)]
    [InlineData("Ride", false, false)]
    public void IsRelevantCyclingActivity_FiltersOnSportTypeAndHeartRate(string sportType, bool hasHeartrate, bool expected)
    {
        var summary = new StravaActivitySummary(1, "Test", sportType, DateTime.UtcNow, 1000, 60, 65, hasHeartrate);

        Assert.Equal(expected, summary.IsRelevantCyclingActivity());
    }

    [Fact]
    public void ToSegmentEfforts_HandlesNullAverageHeartrate_WithoutThrowing()
    {
        var detail = new StravaActivityDetail(
            1, "Test", "Ride", DateTime.UtcNow, 1000, 60, 65, true,
            [new StravaSegmentEffort(10, new StravaSegmentSummary(100, "Segment"), 30, null, DateTime.UtcNow)]);

        var efforts = detail.ToSegmentEfforts(activityId: 5).ToList();

        Assert.Single(efforts);
        Assert.Null(efforts[0].AverageHeartRate);
    }
}
