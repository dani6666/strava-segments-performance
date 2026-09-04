using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StravaSegmentsPerformanceBackend.Data;
using StravaSegmentsPerformanceBackend.Models;
using StravaSegmentsPerformanceBackend.Services;
using Xunit;

namespace strava_segments_performance_backend_tests;

public class WorkoutFetchWorkerTests
{
    private static readonly User TestUser = new() { Id = 1, StravaAthleteId = 42 };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeTokenService : IStravaTokenService
    {
        public Task<string> GetValidAccessTokenAsync(User user, CancellationToken ct) => Task.FromResult("token");
        public Task<string> ForceRefreshAsync(User user, CancellationToken ct) => Task.FromResult("token");
    }

    private static HttpResponseMessage JsonResponse(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    // Stage 1 (listing) reports no new activities — the test seeds them directly — so the
    // worker moves straight to stage 2 and fetches details for every seeded activity.
    private static HttpResponseMessage RespondToStrava(HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.Contains("athlete/activities"))
            return JsonResponse(Array.Empty<object>());

        var activityId = long.Parse(request.RequestUri.AbsolutePath.Split('/').Last());
        return JsonResponse(new
        {
            id = activityId,
            name = "Ride",
            sport_type = "Ride",
            start_date = "2026-01-01T00:00:00Z",
            distance = 1000.0,
            moving_time = 60,
            elapsed_time = 65,
            has_heartrate = true,
            segment_efforts = new[]
            {
                new
                {
                    id = activityId * 10,
                    segment = new { id = 100, name = "Seg" },
                    elapsed_time = 30,
                    average_heartrate = 140.0,
                    start_date = "2026-01-01T00:00:00Z"
                }
            }
        });
    }

    [Fact]
    public async Task ProcessUserAsync_FetchesDetailsForEveryPendingActivity_NotJustTheFirstTen()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IStravaTokenService, FakeTokenService>();
        services.AddSingleton(new HttpClient(new RoutingHandler(RespondToStrava)));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddScoped<StravaApiClient>();
        var provider = services.BuildServiceProvider();

        var contextOptions = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        await using (var seed = new AppDbContext(contextOptions))
        {
            seed.Users.Add(TestUser);
            seed.WorkoutFetchStatuses.Add(new WorkoutFetchStatus { UserId = TestUser.Id, Status = FetchStatusState.Idle });
            for (var i = 1; i <= 12; i++)
                seed.Activities.Add(new Activity { UserId = TestUser.Id, StravaActivityId = i, DetailsFetched = false, StartDateUtc = new DateTime(2026, 1, i) });
            await seed.SaveChangesAsync();
        }

        var channel = new WorkoutFetchChannel();
        var worker = new WorkoutFetchWorker(channel, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<WorkoutFetchWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await channel.Writer.WriteAsync(new FetchRequest(TestUser.Id, null, null));

        await using var assertDb = new AppDbContext(contextOptions);
        WorkoutFetchStatus? status = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            status = await assertDb.WorkoutFetchStatuses.AsNoTracking().FirstAsync(s => s.UserId == TestUser.Id);
            if (status.Status is FetchStatusState.Completed or FetchStatusState.Failed)
                break;
            await Task.Delay(50);
        }

        channel.Writer.Complete();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(FetchStatusState.Completed, status!.Status);

        // Every seeded activity must have its segment efforts fetched — a job reported
        // "Completed" must not silently leave some activities un-fetched, or the chart
        // (which only shows activities with fetched segment efforts) will render partial data.
        var detailsFetchedCount = await assertDb.Activities.CountAsync(a => a.UserId == TestUser.Id && a.DetailsFetched);
        Assert.Equal(12, detailsFetchedCount);
    }
}
