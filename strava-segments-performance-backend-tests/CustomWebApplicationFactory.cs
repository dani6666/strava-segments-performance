using System.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StravaSegmentsPerformanceBackend.Data;

namespace strava_segments_performance_backend_tests;

/// <summary>
/// Boots the real app in the "Testing" environment against a private EF Core InMemory
/// database, with <see cref="TestAuthHandler"/> standing in for the Strava OAuth cookie flow.
/// Each instance owns its own in-memory database, isolated from every other instance -
/// construct a fresh factory per test to avoid state bleeding between facts.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    static CustomWebApplicationFactory()
    {
        // Program.cs reads Frontend:Origin (and AddStrava reads Strava:ClientId/ClientSecret)
        // directly off builder.Configuration BEFORE builder.Build() runs, i.e. before
        // WebApplicationFactory's ConfigureWebHost/ConfigureAppConfiguration hooks take effect.
        // Environment variables are read by WebApplication.CreateBuilder itself at the very
        // start, so they're the only override channel visible to those early reads.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", "Host=localhost;Database=unused-in-tests");
        Environment.SetEnvironmentVariable("Frontend__Origin", "http://localhost:4200");
        Environment.SetEnvironmentVariable("Strava__ClientId", "test-client-id");
        Environment.SetEnvironmentVariable("Strava__ClientSecret", "test-client-secret");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Remove ALL descriptors for the AppDbContext registration chain (options,
            // context, and the internal EF service-provider cache) rather than a single
            // SingleOrDefault removal - order relative to Program.cs's own AddDbContext
            // call is not guaranteed under the minimal-hosting test host, so Npgsql's
            // provider registration can otherwise linger alongside InMemory's.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();

            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>Creates a scope, ensures the in-memory database exists, and runs <paramref name="seed"/> against it.</summary>
    public async Task SeedAsync(Action<AppDbContext> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>An HttpClient whose requests authenticate as the given Strava athlete id.</summary>
    public HttpClient CreateClientAs(long stravaAthleteId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, stravaAthleteId.ToString());
        return client;
    }
}
