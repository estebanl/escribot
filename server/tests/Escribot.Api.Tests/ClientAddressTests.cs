using System.Net;
using System.Net.Http.Json;
using Escribot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Escribot.Api.Tests;

/// <summary>
/// The client address that per-address rate limits key on, behind the Container Apps
/// ingress alone and behind Cloudflare's proxy in front of it.
/// </summary>
public class ClientAddressTests
{
    private const string CloudflareEdge = "172.64.10.20";
    private const string Visitor = "203.0.113.7";

    private static Dictionary<string, string?> BehindCloudflare() => new()
    {
        ["App:TrustForwardedHeaders"] = "true",
        // Two of Cloudflare's ranges are enough to exercise the check.
        ["App:CloudflareIpRanges"] = "172.64.0.0/13, 2606:4700::/32",
    };

    [Fact]
    public async Task Behind_cloudflare_the_visitor_address_comes_from_cf_connecting_ip()
    {
        await using var factory = new ApiFactory { Settings = BehindCloudflare() };

        // The ingress appends the Cloudflare edge it saw to what Cloudflare sent.
        await SignInFromAsync(factory, $"{Visitor}, {CloudflareEdge}", cfConnectingIp: Visitor);

        Assert.Contains(factory.Data.RateCounts.Keys, key => key.Contains($":{Visitor}:", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Data.RateCounts.Keys, key => key.Contains(CloudflareEdge, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Behind_cloudflare_visitors_sharing_an_edge_server_are_limited_separately()
    {
        await using var factory = new ApiFactory { Settings = BehindCloudflare() };
        factory.Clock.Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 - (1_800_000_000 % 60));

        for (var i = 0; i < 5; i++)
        {
            (await SignInFromAsync(factory, CloudflareEdge, cfConnectingIp: "198.51.100.1")).Dispose();
        }

        using var abuser = await SignInFromAsync(factory, CloudflareEdge, cfConnectingIp: "198.51.100.1");
        using var someoneElse = await SignInFromAsync(factory, CloudflareEdge, cfConnectingIp: "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, abuser.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, someoneElse.StatusCode);
    }

    [Fact]
    public async Task Cf_connecting_ip_is_ignored_when_the_request_did_not_come_from_cloudflare()
    {
        await using var factory = new ApiFactory { Settings = BehindCloudflare() };
        const string direct = "198.51.100.9";

        await SignInFromAsync(factory, direct, cfConnectingIp: Visitor);

        Assert.All(factory.Data.RateCounts.Keys, key => Assert.Contains($":{direct}:", key));
    }

    [Fact]
    public async Task Cf_connecting_ip_is_ignored_when_cloudflare_is_not_configured()
    {
        await using var factory = new ApiFactory { Settings = new() { ["App:TrustForwardedHeaders"] = "true" } };

        await SignInFromAsync(factory, CloudflareEdge, cfConnectingIp: Visitor);

        Assert.All(factory.Data.RateCounts.Keys, key => Assert.Contains($":{CloudflareEdge}:", key));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7, 198.51.100.1")]
    public async Task An_unreadable_cf_connecting_ip_leaves_the_cloudflare_address(string header)
    {
        await using var factory = new ApiFactory { Settings = BehindCloudflare() };

        await SignInFromAsync(factory, CloudflareEdge, header);

        Assert.All(factory.Data.RateCounts.Keys, key => Assert.Contains($":{CloudflareEdge}:", key));
    }

    [Fact]
    public void Cloudflare_ranges_need_trusted_forwarded_headers()
    {
        var result = Validate(new AppOptions { CloudflareIpRanges = "172.64.0.0/13" });

        Assert.Contains("App:CloudflareIpRanges requires App:TrustForwardedHeaders.", result.Failures!);
    }

    [Fact]
    public void Cloudflare_ranges_must_be_cidr_ranges()
    {
        var result = Validate(new AppOptions { TrustForwardedHeaders = true, CloudflareIpRanges = "172.64.0.0/13, cloudflare" });

        Assert.Contains("App:CloudflareIpRanges must be a comma-separated list of CIDR ranges.", result.Failures!);
    }

    [Fact]
    public async Task Spoofed_leading_x_forwarded_for_entry_is_ignored()
    {
        await using var factory = new ApiFactory { Settings = new() { ["App:TrustForwardedHeaders"] = "true" } };
        const string spoofed = "198.51.100.99";

        await SignInFromAsync(factory, $"{spoofed}, {Visitor}");

        Assert.NotEmpty(factory.Data.RateCounts.Keys);
        Assert.All(factory.Data.RateCounts.Keys, key => Assert.Contains($":{Visitor}:", key));
        Assert.DoesNotContain(factory.Data.RateCounts.Keys, key => key.Contains(spoofed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Behind_cloudflare_a_spoofed_leading_x_forwarded_for_entry_still_uses_cf_connecting_ip()
    {
        await using var factory = new ApiFactory { Settings = BehindCloudflare() };
        const string spoofed = "198.51.100.99";

        await SignInFromAsync(factory, $"{spoofed}, {Visitor}, {CloudflareEdge}", cfConnectingIp: Visitor);

        Assert.Contains(factory.Data.RateCounts.Keys, key => key.Contains($":{Visitor}:", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Data.RateCounts.Keys, key => key.Contains(spoofed, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Data.RateCounts.Keys, key => key.Contains(CloudflareEdge, StringComparison.Ordinal));
    }

    [Fact]
    public void Container_app_bicep_does_not_enable_duplicate_forwarded_headers_middleware()
    {
        var path = Path.Combine(RepositoryRoot(), "infra", "api", "api-containerapp.bicep");
        var bicep = File.ReadAllText(path);

        Assert.Contains("App__TrustForwardedHeaders", bicep, StringComparison.Ordinal);
        Assert.DoesNotContain("ASPNETCORE_FORWARDEDHEADERS_ENABLED", bicep, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_deployed_cloudflare_ranges_parse()
    {
        var path = Path.Combine(RepositoryRoot(), "infra", "cloudflare-ips.json");
        var ranges = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))!;

        var networks = new AppOptions { CloudflareIpRanges = string.Join(',', ranges) }.CloudflareNetworks();

        Assert.NotEmpty(networks);
        Assert.Contains(networks, n => n.Contains(System.Net.IPAddress.Parse(CloudflareEdge)));
    }

    private static async Task<HttpResponseMessage> SignInFromAsync(ApiFactory factory, string forwardedFor, string? cfConnectingIp = null)
    {
        using var browser = factory.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in/email")
        {
            Content = JsonContent.Create(new { email = "nobody@example.test", password = "wrong-password" }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (cfConnectingIp is not null)
        {
            request.Headers.Add(Auth.CloudflareClientAddress.HeaderName, cfConnectingIp);
        }

        return await browser.SendAsync(request);
    }

    private static ValidateOptionsResult Validate(AppOptions options)
    {
        options.EncryptionSecret = TestSecrets.NewSecret();
        return new AppOptionsValidator(new TestHostEnvironment()).Validate(null, options);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "infra")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Escribot.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
