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
}
