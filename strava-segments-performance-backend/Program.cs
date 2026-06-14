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

app.Run();
