using System.Net;
using Escribot.Api.Hosting;

namespace Escribot.Api.Tests;

/// <summary>
/// Verifies that defensive HTTP security headers are applied across HTML pages, static assets,
/// and API responses, with stricter referrer rules on token-bearing URLs and HSTS outside Development.
/// </summary>
public sealed class SecurityHeadersTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><title>synthetic app</title><div id=\"root\"></div>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("escribot-sec-wwwroot-").FullName;

    public SecurityHeadersTests()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        File.WriteAllText(Path.Combine(_webRoot, "theme-init.js"), "// theme init");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc123.js"), "console.log('synthetic');");
        File.WriteAllText(Path.Combine(_webRoot, "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private ApiFactory CreateFactory(string environmentName = "Testing") =>
        new() { EnvironmentName = environmentName, WebRoot = _webRoot };

    private static void AssertSecurityHeaders(HttpResponseMessage response, string expectedReferrerPolicy)
    {
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal(expectedReferrerPolicy, Header(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeaders.PermissionsPolicy, Header(response, "Permissions-Policy"));
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/pricing")]
    public async Task Html_pages_include_security_headers(string path)
    {
        await using var factory = CreateFactory();
        using var response = await factory.CreateBrowser().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response, "strict-origin-when-cross-origin");
    }

    [Theory]
    [InlineData("/assets/index-abc123.js", "public, max-age=31536000, immutable")]
    [InlineData("/icon.svg", "no-cache")]
    [InlineData("/theme-init.js", "no-cache")]
    public async Task Static_files_include_security_headers_and_expected_caching(string path, string expectedCacheControl)
    {
        await using var factory = CreateFactory();
        using var response = await factory.CreateBrowser().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedCacheControl, response.Headers.CacheControl?.ToString());
        AssertSecurityHeaders(response, "strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task Api_responses_include_security_headers()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        using (var signedOut = await browser.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
            AssertSecurityHeaders(signedOut, "strict-origin-when-cross-origin");
        }

        using var signUp = await AuthEndpointTests.SignUpAsync(
            browser,
            $"sec-{Guid.NewGuid():N}@example.test",
            $"pw-{TestSecrets.NewSecret()}",
            "Security Tester");
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        AssertSecurityHeaders(signUp, "strict-origin-when-cross-origin");

        using var signedIn = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        AssertSecurityHeaders(signedIn, "strict-origin-when-cross-origin");
    }

    [Theory]
    [InlineData("/reset-password")]
    [InlineData("/reset-password?token=secret-token")]
    [InlineData("/api/auth/verify-email")]
    [InlineData("/api/auth/verify-email?token=secret-token")]
    public async Task Sensitive_token_routes_set_no_referrer_policy(string path)
    {
        await using var factory = CreateFactory();
        using var response = await factory.CreateBrowser().GetAsync(path);

        AssertSecurityHeaders(response, "no-referrer");
    }

    [Fact]
    public async Task Hsts_is_enabled_outside_development_and_omitted_in_development()
    {
        await using (var prodFactory = new ApiFactory
        {
            EnvironmentName = "Production",
            WebRoot = _webRoot,
            PublicUrl = "https://escribot.example.test",
            Settings = ApiFactory.EmailConfigured(),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://escribot.example.test/");
            using var response = await prodFactory.CreateBrowser().SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(string.IsNullOrEmpty(Header(response, "Strict-Transport-Security")));
        }

        await using (var devFactory = CreateFactory(environmentName: "Development"))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://escribot.example.test/");
            using var response = await devFactory.CreateBrowser().SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(Header(response, "Strict-Transport-Security"));
        }
    }
}
