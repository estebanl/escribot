using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace FluentEscritor.Api.Auth;

/// <summary>
/// Protects the state value of the Google sign-in round trip with a key derived from
/// <c>App:EncryptionSecret</c>. ASP.NET Core's own data protection keeps a key ring that
/// would have to be stored and shared: the app runs as several instances that start from
/// nothing, and the instance that receives the callback is not always the one that began
/// the sign-in. A key derived from the secret every instance already has needs no storage.
/// Each purpose gets its own key (HKDF-SHA256), and values are sealed with AES-256-GCM.
/// </summary>
internal sealed class SecretDataProtectionProvider(string secret) : IDataProtectionProvider
{
    private readonly byte[] _secret = Encoding.UTF8.GetBytes(secret);

    public IDataProtector CreateProtector(string purpose) => new Protector(_secret, [purpose]);

    private sealed class Protector(byte[] secret, string[] purposes) : IDataProtector
    {
        private const int NonceSize = 12;
        private const int TagSize = 16;

        private readonly byte[] _key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            secret,
            outputLength: 32,
            // Lengths are included so that different purpose chains can never produce the same text.
            info: Encoding.UTF8.GetBytes("fluentescritor/data-protection/v1" + string.Concat(purposes.Select(p => $"\n{p.Length}:{p}"))));

        public IDataProtector CreateProtector(string purpose) => new Protector(secret, [.. purposes, purpose]);

        public byte[] Protect(byte[] plaintext)
        {
            var output = new byte[NonceSize + TagSize + plaintext.Length];
            var nonce = output.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);

            using var aes = new AesGcm(_key, TagSize);
            aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
            return output;
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length < NonceSize + TagSize)
            {
                throw new CryptographicException("The protected value is too short.");
            }

            var plaintext = new byte[protectedData.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(protectedData.AsSpan(0, NonceSize), protectedData.AsSpan(NonceSize + TagSize), protectedData.AsSpan(NonceSize, TagSize), plaintext);
            return plaintext;
        }
    }
}
