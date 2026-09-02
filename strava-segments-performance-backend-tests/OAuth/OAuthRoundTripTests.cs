using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace strava_segments_performance_backend_tests.OAuth;

/// <summary>
/// Risk #2 (test-plan.md) integration deltas — the prod-only / negative cases the browser e2e
/// cannot reach on localhost-http. The successful handshake itself is proven by the Phase 2
/// browser e2e; this layer does NOT re-assert it.
/// </summary>
public class OAuthRoundTripTests(OAuthRoundTripFixture fixture) : IClassFixture<OAuthRoundTripFixture>
{
    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // The actual prod incident: the redirect_uri must be built from the forwarded proto/host,
    // not the raw request — so behind an https proxy it is https://<host>/auth/callback.
    [Fact]
    public async Task Challenge_BuildsHttpsCallbackFromForwardedHeaders()
    {
        using var factory = fixture.CreateFactory("Development");
        using var client = NoRedirectClient(factory);

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "example.test");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var authorizeUrl = response.Headers.Location!;
        Assert.StartsWith("https://www.strava.com/oauth/authorize", authorizeUrl.ToString());

        var query = QueryHelpers.ParseQuery(authorizeUrl.Query);
        Assert.Equal("https://example.test/auth/callback", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Contains("activity:read_all", query["scope"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(query["state"].ToString()));
    }

    // The dev/prod cookie divergence is a named instance of Risk #2: prod must be cross-site
    // capable (None/Always), dev must stay usable over http (Lax/SameAsRequest).
    [Theory]
    [InlineData("Development", SameSiteMode.Lax, CookieSecurePolicy.SameAsRequest)]
    [InlineData("Production", SameSiteMode.None, CookieSecurePolicy.Always)]
    public void CookiePolicy_ResolvesPerEnvironment(
        string environment, SameSiteMode expectedSameSite, CookieSecurePolicy expectedSecurePolicy)
    {
        using var factory = fixture.CreateFactory(environment);

        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(expectedSameSite, options.Cookie.SameSite);
        Assert.Equal(expectedSecurePolicy, options.Cookie.SecurePolicy);
    }

    // Any remote failure (here: missing correlation + unprotectable state, the same path a failed
    // code exchange takes) must land on the frontend's login page with the error flag.
    [Fact]
    public async Task Callback_OnRemoteFailure_RedirectsToLoginError()
    {
        using var factory = fixture.CreateFactory("Development");
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync("/auth/callback?code=irrelevant&state=invalid");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "http://localhost:4200/login?error=auth_failed",
            response.Headers.Location!.ToString());
    }

    // Unauthenticated API calls must get a clean 401, never a 302 to the OAuth login.
    [Fact]
    public async Task ApiEndpoint_Unauthenticated_Returns401_NotRedirect()
    {
        using var factory = fixture.CreateFactory("Development");
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
