using System.Security.Cryptography;

namespace StravaSegmentsPerformanceBackend.Services;

public class TokenEncryptionService
{
    private readonly byte[] _key;

    public TokenEncryptionService(IConfiguration configuration)
    {
        var keyBase64 = configuration["TokenEncryption:Key"]
            ?? throw new InvalidOperationException("TokenEncryption:Key is not configured.");
        _key = Convert.FromBase64String(keyBase64);
    }

    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        var result = new byte[aes.IV.Length + ciphertext.Length];
        aes.IV.CopyTo(result, 0);
        ciphertext.CopyTo(result, aes.IV.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string encrypted)
    {
        var data = Convert.FromBase64String(encrypted);

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = data[..16];
        var ciphertext = data[16..];

        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plaintextBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

        return System.Text.Encoding.UTF8.GetString(plaintextBytes);
    }
}
