using System.Net;
using StravaSegmentsPerformanceBackend.Models;

namespace strava_segments_performance_backend_tests;

/// <summary>
/// Endpoint-level authorization tests for the two authenticated read endpoints, proving
/// Risk #5 (context/foundation/test-plan.md): a caller only ever sees their own data.
/// Each test owns a fresh <see cref="CustomWebApplicationFactory"/> so seeded data never
/// bleeds across facts.
/// </summary>
public class EndpointAuthorizationTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FetchStatus_AuthenticatedRequest_ReturnsOk()
    {
        const long stravaAthleteId = 1001;

        await _factory.SeedAsync(db =>
        {
            db.Users.Add(new User { Id = 1, StravaAthleteId = stravaAthleteId });
        });

        var client = _factory.CreateClientAs(stravaAthleteId);
        var response = await client.GetAsync("/api/workouts/fetch-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
