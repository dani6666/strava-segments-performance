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
        options.Cookie.SameSite = builder.Environment.IsDevelopment()
            ? SameSiteMode.Lax
            : SameSiteMode.None;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
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

static DateTime? NormalizeUtc(DateTime? value) =>
    value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

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

app.Run();

record FetchWorkoutsRequest(DateTime? After, DateTime? Before);
