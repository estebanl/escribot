using System.Net;
using FluentEscritor.Api.Data;
using Microsoft.Extensions.DependencyInjection;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// How the API serves the browser app: its files, and index.html for page addresses with
/// the status the page will show. The checks on pages follow scripts/test-isolation.mjs.
/// </summary>
public sealed class ClientAppTests : IDisposable
{
    private const string IndexHtml = "<!doctype html><title>synthetic app</title><div id=\"root\"></div>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("fluentescritor-wwwroot-").FullName;

    public ClientAppTests()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc123.js"), "console.log('synthetic');");
        File.WriteAllText(Path.Combine(_webRoot, "icon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
    }

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    // In Development the app also serves the project's own wwwroot (static web assets), which
    // may hold a local build. The published app, like these tests, serves only its web root.
    private const string Environment = "Testing";

    private ApiFactory CreateFactory(Dictionary<string, string?>? settings = null) =>
        new() { EnvironmentName = Environment, WebRoot = _webRoot, Settings = settings ?? [] };

    private static async Task<HttpClient> SignUpAsync(ApiFactory factory, string? email = null)
    {
        var browser = factory.CreateBrowser();
        using var response = await AuthEndpointTests.SignUpAsync(browser, email ?? $"client-{Guid.NewGuid():N}@example.test", $"pw-{TestSecrets.NewSecret()}", "Client Tester");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return browser;
    }

    private static async Task AssertPageAsync(HttpClient browser, string path, HttpStatusCode expected)
    {
        using var response = await browser.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login")]
    [InlineData("/signup")]
    [InlineData("/forgot-password")]
    [InlineData("/reset-password?token=abc")]
    [InlineData("/pricing")]
    [InlineData("/terms")]
    [InlineData("/privacy/")]
    public async Task Public_pages_are_the_app_for_everyone(string path)
    {
        await using var factory = CreateFactory();
        await AssertPageAsync(factory.CreateBrowser(), path, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/vault", "/login?next=%2Fvault")]
    [InlineData("/settings?billing=success", "/login?next=%2Fsettings%3Fbilling%3Dsuccess")]
    [InlineData("/write/sess-123", "/login?next=%2Fwrite%2Fsess-123")]
    [InlineData("/admin", "/login?next=%2Fadmin")]
    [InlineData("/no-such-page", "/login?next=%2Fno-such-page")]
    public async Task Signed_out_visitors_are_sent_to_sign_in(string path, string location)
    {
        await using var factory = CreateFactory();
        using var response = await factory.CreateBrowser().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Signed_in_pages_answer_with_the_status_the_page_shows()
    {
        await using var factory = CreateFactory();
        var browser = await SignUpAsync(factory);

        foreach (var path in new[] { "/", "/welcome", "/vault", "/drills?focus=LR", "/analytics", "/settings", "/pricing" })
        {
            await AssertPageAsync(browser, path, HttpStatusCode.OK);
        }

        await AssertPageAsync(browser, "/no-such-page", HttpStatusCode.NotFound);
        await AssertPageAsync(browser, "/vault/extra", HttpStatusCode.NotFound);
        // To anyone but an admin the admin page does not exist.
        await AssertPageAsync(browser, "/admin", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_writing_session_page_exists_only_for_its_owner()
    {
        await using var factory = CreateFactory();
        var owner = await SignUpAsync(factory);
        var ownerId = factory.Data.Accounts.Values.Single().Id;
        await factory.Services.GetRequiredService<IWritingRepository>().SaveSessionAsync(ownerId, TestData.Session("sess-owned"));
        var other = await SignUpAsync(factory);

        await AssertPageAsync(owner, "/write/sess-owned", HttpStatusCode.OK);
        await AssertPageAsync(owner, "/session/sess-owned", HttpStatusCode.OK);
        await AssertPageAsync(other, "/write/sess-owned", HttpStatusCode.NotFound);
        await AssertPageAsync(other, "/session/sess-owned", HttpStatusCode.NotFound);
        await AssertPageAsync(owner, "/session/sess-missing", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_admin_gets_the_admin_page()
    {
        const string adminEmail = "admin@example.test";
        await using var factory = CreateFactory(new() { ["ADMIN_EMAILS"] = adminEmail });
        var admin = await SignUpAsync(factory, adminEmail);
        var id = factory.Data.Accounts.Values.Single().Id;
        factory.Data.Accounts[id] = factory.Data.Accounts[id] with { EmailVerified = true };

        await AssertPageAsync(admin, "/admin", HttpStatusCode.OK);
    }

    [Fact]
    public async Task Files_are_served_as_they_are_and_built_assets_are_cached_for_good()
    {
        await using var factory = CreateFactory();
        var browser = factory.CreateBrowser();

        using var asset = await browser.GetAsync("/assets/index-abc123.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());

        using var icon = await browser.GetAsync("/icon.svg");
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("no-cache", icon.Headers.CacheControl?.ToString());

        // A missing file is a 404, not the page.
        using var missing = await browser.GetAsync("/assets/index-old.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.NotEqual(IndexHtml, await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_api_address_is_an_api_error_not_the_page()
    {
        await using var factory = CreateFactory();
        using (var anonymous = await factory.CreateBrowser().GetAsync("/api/no-such-endpoint"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var browser = await SignUpAsync(factory);
        using var response = await browser.GetAsync("/api/no-such-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Not found.", (await AuthEndpointTests.Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Without_a_built_app_page_addresses_say_so()
    {
        var empty = Directory.CreateTempSubdirectory("fluentescritor-empty-").FullName;
        try
        {
            await using var factory = new ApiFactory { EnvironmentName = Environment, WebRoot = empty };
            using var response = await factory.CreateBrowser().GetAsync("/login");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("browser app is not part of this build", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
