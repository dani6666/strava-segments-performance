using Microsoft.Extensions.Configuration;
using StravaSegmentsPerformanceBackend.Services;

namespace strava_segments_performance_backend_tests;

public class TokenEncryptionServiceTests
{
    // Any valid base64 32-byte (AES-256) key; test-only.
    private const string TestKey = "c3RyYXZhLWUyZS1vYXV0aC10ZXN0LWtleS0zMmJ5dGU=";

    private static TokenEncryptionService Create(string? key)
    {
        var settings = new Dictionary<string, string?>();
        if (key is not null) settings["TokenEncryption:Key"] = key;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new TokenEncryptionService(configuration);
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTripsToOriginalPlaintext()
    {
        var service = Create(TestKey);
        const string plaintext = "strava-access-token-value";

        var encrypted = service.Encrypt(plaintext);

        Assert.NotEqual(plaintext, encrypted);
        Assert.Equal(plaintext, service.Decrypt(encrypted));
    }

    [Fact]
    public void Encrypt_UsesAFreshIv_SoCiphertextsDiffer()
    {
        var service = Create(TestKey);

        Assert.NotEqual(service.Encrypt("same-input"), service.Encrypt("same-input"));
    }

    [Fact]
    public void Constructor_WithoutKey_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Create(null));
    }
}
