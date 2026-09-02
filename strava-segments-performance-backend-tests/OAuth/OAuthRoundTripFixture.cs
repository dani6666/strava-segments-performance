using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace strava_segments_performance_backend_tests.OAuth;

/// <summary>
/// Boots a throwaway Postgres and builds per-environment WebApplicationFactory instances.
/// A real relational DB is required because the host's startup runs MigrateAsync /
/// ExecuteUpdateAsync (Program.cs) — the EF Core InMemory provider cannot back it.
///
/// Config is injected via environment variables (not ConfigureAppConfiguration): Program reads
/// the connection string / Frontend origin / Strava creds EAGERLY at builder time, before the
/// factory's config overrides would apply under minimal hosting. Env vars are read eagerly by
/// WebApplication.CreateBuilder and override appsettings, so they win.
/// </summary>
public sealed class OAuthRoundTripFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Frontend__Origin", "http://localhost:4200");
        Environment.SetEnvironmentVariable("TokenEncryption__Key", "c3RyYXZhLWUyZS1vYXV0aC10ZXN0LWtleS0zMmJ5dGU=");
        Environment.SetEnvironmentVariable("Strava__ClientId", "test-client-id");
        Environment.SetEnvironmentVariable("Strava__ClientSecret", "test-client-secret");
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("Frontend__Origin", null);
        Environment.SetEnvironmentVariable("TokenEncryption__Key", null);
        Environment.SetEnvironmentVariable("Strava__ClientId", null);
        Environment.SetEnvironmentVariable("Strava__ClientSecret", null);
        await _postgres.DisposeAsync();
    }

    /// <summary>A factory running Program under the given ASPNETCORE_ENVIRONMENT.</summary>
    public WebApplicationFactory<Program> CreateFactory(string environment) =>
        new OAuthWebAppFactory(environment);
}

file sealed class OAuthWebAppFactory(string environment) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment);
}
