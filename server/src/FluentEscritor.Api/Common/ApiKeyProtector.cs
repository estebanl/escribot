using System.Security.Cryptography;
using System.Text;
using FluentEscritor.Api.Configuration;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Common;

/// <summary>
/// Encrypts the provider API keys users save in Settings with AES-256-GCM. The key is
/// the SHA-256 of <c>App:EncryptionSecret</c> and the stored form is
/// <c>iv:tag:ciphertext</c>, each part Base64.
/// </summary>
public sealed class ApiKeyProtector(IOptions<AppOptions> options)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.EncryptionSecret.Trim()));

    public string Encrypt(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var encrypted = new byte[data.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, encrypted, tag);
        return $"{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(tag)}:{Convert.ToBase64String(encrypted)}";
    }

    /// <summary>Returns null for a value that is malformed, altered, or encrypted with another secret.</summary>
    public string? Decrypt(string? payload)
    {
        var parts = payload?.Split(':');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            var nonce = Convert.FromBase64String(parts[0]);
            var tag = Convert.FromBase64String(parts[1]);
            var encrypted = Convert.FromBase64String(parts[2]);
            if (nonce.Length != NonceSize || tag.Length != TagSize)
            {
                return null;
            }

            var data = new byte[encrypted.Length];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, encrypted, tag, data);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>A saved key as it may be shown: its first and last characters only.</summary>
    public static string? Mask(string? key)
    {
        var trimmed = key?.Trim();
        if (trimmed is null || trimmed.Length < 6)
        {
            return null;
        }

        return trimmed.Length <= 10
            ? $"{trimmed[..3]}••••{trimmed[^2..]}"
            : $"{trimmed[..5]}••••••{trimmed[^4..]}";
    }
}
