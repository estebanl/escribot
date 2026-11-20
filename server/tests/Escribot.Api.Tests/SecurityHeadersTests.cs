using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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

    [Fact]
    public void Csp_allows_the_inline_theme_script_in_index_html_by_its_hash()
    {
        var html = File.ReadAllText(Path.Combine(RepositoryRoot(), "client", "index.html"));
        var scripts = Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline);

        var script = Assert.Single(scripts).Groups[1].Value;
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        Assert.Equal(SecurityHeaders.ThemeScriptHash, $"sha256-{hash}");
    }

    [Fact]
    public async Task Hsts_is_sent_when_the_proxy_reports_https()
    {
        // Container Apps ends TLS and passes the request on as plain HTTP, with the scheme the
        // visitor used in X-Forwarded-Proto.
        var settings = ApiFactory.EmailConfigured();
        settings["App:TrustForwardedHeaders"] = "true";
        await using var factory = new ApiFactory
        {
            EnvironmentName = "Production",
            WebRoot = _webRoot,
            PublicUrl = "https://escribot.example.test",
            Settings = settings,
        };
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("http://escribot.example.test"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(Header(response, "Strict-Transport-Security")));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "client", "index.html")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("client/index.html was not found above the test's output directory.");
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
