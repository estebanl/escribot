using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Hosting;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Every page address in the browser app's router, with route parameters filled in,
    /// plus an unknown address for the not-found page.
    /// </summary>
    public static TheoryData<string> ClientRoutes()
    {
        var router = File.ReadAllText(Path.Combine(RepositoryRoot(), "client", "src", "router.tsx"));
        var paths = Regex.Matches(router, @"path:\s*""(/[^""]*)""")
            .Select(m => Regex.Replace(m.Groups[1].Value, @":\w+", "0f8fad5b-d9cb-469f-a165-70867728950e"))
            .Append("/no-such-page")
            .Distinct()
            .ToList();
        Assert.Contains("/", paths);
        return new TheoryData<string>(paths);
    }

    [Theory]
    [MemberData(nameof(ClientRoutes))]
    public async Task Every_html_page_keeps_the_strict_app_csp(string path)
    {
        // Endpoints may set their own CSP (uploaded SVGs do), and the middleware keeps it.
        // No page of the browser app may end up with anything but the app's policy. Signed in,
        // so every address answers with the page (signed out, most redirect to /login).
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();
        using var signUp = await AuthEndpointTests.SignUpAsync(
            browser,
            $"csp-{Guid.NewGuid():N}@example.test",
            $"pw-{TestSecrets.NewSecret()}",
            "CSP Tester");
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);

        using var response = await browser.GetAsync(path);

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task Only_uploaded_svgs_get_the_sandbox_csp()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();
        using var signUp = await AuthEndpointTests.SignUpAsync(
            browser,
            $"csp-{Guid.NewGuid():N}@example.test",
            $"pw-{TestSecrets.NewSecret()}",
            "CSP Tester");
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        var userId = (await AuthEndpointTests.Json(signUp)).GetProperty("user").GetProperty("id").GetString()!;
        // SVG uploads are a Pro feature.
        await factory.Services.GetRequiredService<ISettingsRepository>().UpdateAsync(userId, s => s with { Plan = Plans.Pro });

        async Task<HttpResponseMessage> UploadAndFetchAsync(string imageDataUrl)
        {
            using var created = await browser.PostAsJsonAsync("/api/prompts", new
            {
                taskType = "task1",
                category = "bar",
                promptText = "Summarise the chart.",
                imageDataUrl,
            });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var imageUrl = (await AuthEndpointTests.Json(created)).GetProperty("prompt").GetProperty("imageDataUrl").GetString()!;
            return await browser.GetAsync(imageUrl);
        }

        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10'/></svg>";
        using (var svgResponse = await UploadAndFetchAsync($"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}"))
        {
            Assert.Equal("image/svg+xml", svgResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal(SecurityHeaders.UploadedSvgContentSecurityPolicy, Header(svgResponse, "Content-Security-Policy"));
        }

        using var pngResponse = await UploadAndFetchAsync($"data:image/png;base64,{OnePixelPng}");
        Assert.Equal("image/png", pngResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, Header(pngResponse, "Content-Security-Policy"));
    }

    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

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
    public void Cloudflare_serves_the_browser_app_with_the_same_headers_as_the_api()
    {
        // client/cloudflare/_headers: a path line, then its headers indented below it.
        var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (var line in File.ReadAllLines(Path.Combine(RepositoryRoot(), "client", "cloudflare", "_headers")))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                rules[line.Trim()] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else if (line.Trim() is var header && !header.StartsWith('!'))
            {
                var colon = header.IndexOf(':');
                current![header[..colon].Trim()] = header[(colon + 1)..].Trim();
            }
        }

        var all = rules["/*"];
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy, all["Content-Security-Policy"]);
        Assert.Equal(SecurityHeaders.PermissionsPolicy, all["Permissions-Policy"]);
        Assert.Equal("strict-origin-when-cross-origin", all["Referrer-Policy"]);
        Assert.Equal("nosniff", all["X-Content-Type-Options"]);
        Assert.Equal("DENY", all["X-Frame-Options"]);
        // UseHsts with its defaults, as outside Development.
        Assert.Equal("max-age=2592000", all["Strict-Transport-Security"]);
        Assert.Equal("no-cache", all["Cache-Control"]);
        Assert.Equal("public, max-age=31536000, immutable", rules["/assets/*"]["Cache-Control"]);
        Assert.Equal("no-referrer", rules["/reset-password"]["Referrer-Policy"]);
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
