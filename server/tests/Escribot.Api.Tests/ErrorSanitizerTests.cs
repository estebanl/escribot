using Escribot.Api.Data.Cosmos;

namespace Escribot.Api.Tests;

public class ErrorSanitizerTests
{
    [Fact]
    public void Clean_redacts_google_api_keys_and_key_query_parameters()
    {
        // Generated at runtime so nothing shaped like a credential is stored in the repository.
        var googleKey = $"AIza{Guid.NewGuid():N}xyz";
        var shortQuerySecret = $"val-{Guid.NewGuid():N}"[..16];

        Assert.Equal(39, googleKey.Length);

        var bareGoogleKey = ErrorSanitizer.Clean($"Gemini rejected {googleKey} during call", ErrorSanitizer.MaxMessage);
        Assert.NotNull(bareGoogleKey);
        Assert.DoesNotContain(googleKey, bareGoogleKey, StringComparison.Ordinal);
        Assert.Contains(ErrorSanitizer.Redacted, bareGoogleKey, StringComparison.Ordinal);

        var firstQueryParam = ErrorSanitizer.Clean(
            $"POST https://generativelanguage.googleapis.com/v1beta/models/gemini:generateContent?key={shortQuerySecret}&alt=json failed",
            ErrorSanitizer.MaxMessage);
        Assert.NotNull(firstQueryParam);
        Assert.DoesNotContain(shortQuerySecret, firstQueryParam, StringComparison.Ordinal);
        Assert.Contains($"?key={ErrorSanitizer.Redacted}&alt=json", firstQueryParam, StringComparison.Ordinal);

        var subsequentQueryParam = ErrorSanitizer.Clean(
            $"GET https://example.test/api?foo=1&KEY={shortQuerySecret} failed",
            ErrorSanitizer.MaxMessage);
        Assert.NotNull(subsequentQueryParam);
        Assert.DoesNotContain(shortQuerySecret, subsequentQueryParam, StringComparison.Ordinal);
        Assert.Contains($"&KEY={ErrorSanitizer.Redacted}", subsequentQueryParam, StringComparison.Ordinal);
    }

    // A 39-character Google-shaped key built at runtime, ending in the given character.
    private static string GoogleKeyEndingWith(char last) => $"AIza{Guid.NewGuid():N}xy{last}";

    private static string Clean(string text) => ErrorSanitizer.Clean(text, ErrorSanitizer.MaxMessage)!;

    [Theory]
    [InlineData("Gemini rejected {0} during call")]
    [InlineData("Gemini rejected {0}")]
    [InlineData("Gemini rejected \"{0}\"")]
    [InlineData("GEMINI_KEY_{0}")]
    [InlineData("key{0}")]
    [InlineData("x{0}y")]
    public void Clean_redacts_google_keys_whatever_surrounds_them(string template)
    {
        foreach (var key in new[] { GoogleKeyEndingWith('-'), GoogleKeyEndingWith('_'), GoogleKeyEndingWith('z') })
        {
            Assert.Equal(39, key.Length);
            var cleaned = Clean(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, key));
            Assert.DoesNotContain(key, cleaned, StringComparison.Ordinal);
            Assert.DoesNotContain(key[4..], cleaned, StringComparison.Ordinal);
            Assert.Contains(ErrorSanitizer.Redacted, cleaned, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("config key={0} loaded", "key=")]
    [InlineData("config KEY: {0} loaded", "KEY: ")]
    [InlineData("{{\"key\":\"{0}\"}}", "\"key\":\"")]
    [InlineData("GEMINI_KEY={0}", "GEMINI_KEY=")]
    public void Clean_redacts_bare_key_values(string template, string keptPrefix)
    {
        var secret = TestSecrets.NewSecret()[..16];
        var cleaned = Clean(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, secret));
        Assert.DoesNotContain(secret, cleaned, StringComparison.Ordinal);
        Assert.Contains($"{keptPrefix}{ErrorSanitizer.Redacted}", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_leaves_words_ending_in_key_alone()
    {
        Assert.Equal("monkey=banana", Clean("monkey=banana"));
    }

    [Fact]
    public void Clean_keeps_the_text_after_a_query_key()
    {
        var secret = TestSecrets.NewSecret()[..16];

        Assert.Equal(
            $"GET https://x/api?key={ErrorSanitizer.Redacted}), status 400",
            Clean($"GET https://x/api?key={secret}), status 400"));
        Assert.Equal(
            $"GET https://x/api?key={ErrorSanitizer.Redacted}, retrying",
            Clean($"GET https://x/api?key={secret}, retrying"));
        Assert.Equal(
            $"GET https://x/api?key={ErrorSanitizer.Redacted}#top",
            Clean($"GET https://x/api?key={secret}#top"));
    }

    [Theory]
    [InlineData("%3Fkey%3D")]
    [InlineData("%26key%3D")]
    [InlineData("%3fKEY%3d")]
    public void Clean_redacts_percent_encoded_query_keys(string prefix)
    {
        var secret = TestSecrets.NewSecret()[..16];
        var cleaned = Clean($"redirect=https%3A%2F%2Fx%2Fapi{prefix}{secret} failed");
        Assert.DoesNotContain(secret, cleaned, StringComparison.Ordinal);
        Assert.Contains($"{prefix}{ErrorSanitizer.Redacted} failed", cleaned, StringComparison.Ordinal);
    }
}
