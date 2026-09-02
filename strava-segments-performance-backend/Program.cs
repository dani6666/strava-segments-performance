using System.Net;
using System.Security.Claims;
using AspNet.Security.OAuth.Strava;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using StravaSegmentsPerformanceBackend.Data;
using StravaSegmentsPerformanceBackend.Models;
using StravaSegmentsPerformanceBackend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

var frontendOrigin = builder.Configuration["Frontend:Origin"]!;

// E2E is a local/CI, plain-http environment. Treat it as dev-like for cookie policy: with the
// non-Development defaults (SameSite=None + Secure=Always) the auth cookie is dropped over
// http-localhost, so the browser would land on /dashboard and immediately bounce back to /login.
var isE2E = builder.Environment.IsEnvironment("E2E");
var useDevLikeCookies = builder.Environment.IsDevelopment() || isE2E;

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(frontendOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = useDevLikeCookies
            ? SameSiteMode.Lax
            : SameSiteMode.None;
        options.Cookie.SecurePolicy = useDevLikeCookies
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;

        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddStrava(options =>
    {
        options.ClientId = builder.Configuration["Strava:ClientId"]!;
        options.ClientSecret = builder.Configuration["Strava:ClientSecret"]!;
        options.Scope.Add("activity:read_all");
        options.SaveTokens = true;
        options.CallbackPath = "/auth/callback";

        options.Events.OnCreatingTicket = async context =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var encryption = context.HttpContext.RequestServices.GetRequiredService<TokenEncryptionService>();
            var stravaId = long.Parse(context.Identity!.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var displayName = context.Identity.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
            var accessToken = encryption.Encrypt(context.AccessToken!);
            var refreshToken = encryption.Encrypt(context.RefreshToken!);
            var expiresAt = context.ExpiresIn.HasValue
                ? DateTime.UtcNow.AddSeconds(context.ExpiresIn.Value.TotalSeconds)
                : DateTime.UtcNow.AddHours(6);

            var user = await db.Users.FirstOrDefaultAsync(u => u.StravaAthleteId == stravaId);
            if (user is null)
            {
                user = new User { StravaAthleteId = stravaId };
                db.Users.Add(user);
            }

            user.DisplayName = displayName;
            user.AccessToken = accessToken;
            user.RefreshToken = refreshToken;
            user.TokenExpiresAtUtc = expiresAt;

            await db.SaveChangesAsync();
        };

        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect($"{frontendOrigin}/login?error=auth_failed");
            return Task.CompletedTask;
        };
    });

if (isE2E)
{
    // E2E-only: point the Strava OAuth handler at the in-process stub authorize/token/athlete
    // endpoints (mapped below, also E2E-gated). Everything else about the handler — CallbackPath,
    // scope, OnCreatingTicket, OnRemoteFailure — is unchanged, so the test drives the real
    // challenge/callback/backchannel logic against a stub instead of the live Strava.
    builder.Services.PostConfigure<StravaAuthenticationOptions>(
        StravaAuthenticationDefaults.AuthenticationScheme,
        options =>
        {
            var stubBase = builder.Configuration["E2E:StubBaseUrl"] ?? "http://localhost:5000";
            options.AuthorizationEndpoint = $"{stubBase}/e2e-stub/oauth/authorize";
            options.TokenEndpoint = $"{stubBase}/e2e-stub/oauth/token";
            options.UserInformationEndpoint = $"{stubBase}/e2e-stub/api/athlete";
        });
}

builder.Services.AddSingleton<TokenEncryptionService>();
builder.Services.AddAuthorization();

builder.Services.AddHttpClient<StravaApiClient>();
builder.Services.AddSingleton<WorkoutFetchChannel>();
builder.Services.AddHostedService<WorkoutFetchWorker>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IStravaTokenService, StravaTokenService>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    await db.WorkoutFetchStatuses
        .Where(s => s.Status == FetchStatusState.Running || s.Status == FetchStatusState.Pending)
        .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.Status, FetchStatusState.Interrupted));
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithName("HealthCheck");

app.MapGet("/auth/login", (HttpContext ctx, string? returnUrl) =>
{
    var redirectUri = $"{frontendOrigin}/dashboard";
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = redirectUri },
        [StravaAuthenticationDefaults.AuthenticationScheme]);
});

app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    var user = ctx.User;
    if (user.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var stravaId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    var displayName = user.FindFirst(ClaimTypes.Name)?.Value;

    return Results.Ok(new { stravaAthleteId = stravaId, displayName });
}).RequireAuthorization();

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

if (app.Environment.IsEnvironment("E2E"))
{
    // E2E-only auth seam. Mints a real cookie session WITHOUT Strava OAuth so the
    // Playwright setup project (frontend e2e/auth.setup.ts) can save a genuine
    // storageState. Gated to the E2E environment — this endpoint is not mapped in
    // Development or Production. Never run the app with ASPNETCORE_ENVIRONMENT=E2E
    // in production: it would let anyone sign in as any athlete.
    app.MapGet("/auth/test-login", async (HttpContext ctx, AppDbContext db, long athleteId, string name) =>
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.StravaAthleteId == athleteId);
        if (user is null)
        {
            user = new User { StravaAthleteId = athleteId };
            db.Users.Add(user);
        }
        user.DisplayName = name;
        await db.SaveChangesAsync();

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, athleteId.ToString()),
                new Claim(ClaimTypes.Name, name)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Results.Ok(new { stravaAthleteId = athleteId, displayName = name });
    });
}

if (app.Environment.IsEnvironment("E2E"))
{
    // E2E-only stub Strava. Impersonates the authorize/token/athlete endpoints the OAuth handler
    // was repointed at (see the PostConfigure above), so a browser can complete a full login with
    // no real Strava. Never mapped outside the E2E environment. The canned athlete below is what
    // the browser handshake spec asserts against.
    app.MapGet("/e2e-stub/oauth/authorize", (string redirect_uri, string state) =>
    {
        // Bounce straight back to the OAuth callback with a canned code, echoing state unchanged
        // so the handler's correlation/state validation passes.
        var separator = redirect_uri.Contains('?') ? '&' : '?';
        return Results.Redirect(
            $"{redirect_uri}{separator}code=e2e-auth-code&state={Uri.EscapeDataString(state)}");
    });

    app.MapPost("/e2e-stub/oauth/token", () => Results.Json(new
    {
        token_type = "Bearer",
        access_token = "e2e-access-token",
        refresh_token = "e2e-refresh-token",
        expires_in = 21600,
        expires_at = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds()
    }));

    // Shape mirrors Strava's /api/v3/athlete — the provider maps id -> NameIdentifier and
    // firstname -> Name, which OnCreatingTicket reads to upsert the user.
    app.MapGet("/e2e-stub/api/athlete", () => Results.Json(new
    {
        id = 99999L,
        username = "e2e_rider",
        firstname = "E2E",
        lastname = "Rider"
    }));
}

static DateTime? NormalizeUtc(DateTime? value) => value switch
{
    null => null,
    { Kind: DateTimeKind.Utc } => value,
    { Kind: DateTimeKind.Local } => value.Value.ToUniversalTime(),
    // Unspecified (payload had no offset): assume the documented UTC contract.
    _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
};

static object ToFetchStatusDto(WorkoutFetchStatus status) => new
{
    status = status.Status switch
    {
        FetchStatusState.Idle => "idle",
        FetchStatusState.Pending => "pending",
        FetchStatusState.Running => "running",
        FetchStatusState.Completed => "completed",
        FetchStatusState.Failed => "failed",
        FetchStatusState.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    },
    stage = status.Stage switch
    {
        FetchStage.ListingActivities => "listing",
        FetchStage.FetchingDetails => "fetching_details",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    },
    activitiesProcessed = status.ActivitiesProcessed,
    totalToProcess = status.TotalToProcess,
    errorMessage = status.ErrorMessage
};

app.MapPost("/api/workouts/fetch", async (HttpContext ctx, AppDbContext db, WorkoutFetchChannel channel, FetchWorkoutsRequest? request) =>
{
    var afterUtc = NormalizeUtc(request?.After);
    var beforeUtc = NormalizeUtc(request?.Before);

    if (!FetchWindowValidator.IsValidRange(afterUtc, beforeUtc))
        return Results.BadRequest(new { error = "'after' must not be later than 'before'." });

    var stravaId = long.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var user = await db.Users.FirstAsync(u => u.StravaAthleteId == stravaId);

    var rowsUpdated = await db.WorkoutFetchStatuses
        .Where(s => s.UserId == user.Id && s.Status != FetchStatusState.Pending && s.Status != FetchStatusState.Running)
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(s => s.Status, FetchStatusState.Pending)
            .SetProperty(s => s.Stage, (FetchStage?)null)
            .SetProperty(s => s.ActivitiesProcessed, 0)
            .SetProperty(s => s.TotalToProcess, (int?)null)
            .SetProperty(s => s.ErrorMessage, (string?)null)
            .SetProperty(s => s.StartedAtUtc, DateTime.UtcNow)
            .SetProperty(s => s.CompletedAtUtc, (DateTime?)null));

    if (rowsUpdated == 0)
    {
        var existing = await db.WorkoutFetchStatuses.FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (existing is null)
        {
            db.WorkoutFetchStatuses.Add(new WorkoutFetchStatus
            {
                UserId = user.Id,
                Status = FetchStatusState.Pending,
                ActivitiesProcessed = 0,
                StartedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        else
        {
            return Results.Ok(ToFetchStatusDto(existing));
        }
    }

    await channel.Writer.WriteAsync(new FetchRequest(user.Id, afterUtc, beforeUtc));

    var status = await db.WorkoutFetchStatuses.FirstAsync(s => s.UserId == user.Id);
    return Results.Accepted(value: ToFetchStatusDto(status));
}).RequireAuthorization();

app.MapGet("/api/workouts/fetch-status", async (HttpContext ctx, AppDbContext db) =>
{
    var stravaId = long.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var user = await db.Users.FirstAsync(u => u.StravaAthleteId == stravaId);

    var status = await db.WorkoutFetchStatuses.FirstOrDefaultAsync(s => s.UserId == user.Id);
    return Results.Ok(ToFetchStatusDto(status ?? new WorkoutFetchStatus { Status = FetchStatusState.Idle }));
}).RequireAuthorization();

app.MapGet("/api/analysis/fitness-trend", async (HttpContext ctx, AppDbContext db, DateTime? from, DateTime? to) =>
{
    var stravaId = long.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var user = await db.Users.FirstAsync(u => u.StravaAthleteId == stravaId);

    var series = await FitnessTrendQuery.GetForUserAsync(db, user.Id, from, to);
    return Results.Ok(series);
}).RequireAuthorization();

app.Run();

record FetchWorkoutsRequest(DateTime? After, DateTime? Before);

// Exposes the top-level Program to the test project's WebApplicationFactory<Program>.
public partial class Program { }
