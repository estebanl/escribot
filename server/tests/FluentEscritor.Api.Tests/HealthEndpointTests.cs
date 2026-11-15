using System.Net;

namespace FluentEscritor.Api.Tests;

public class HealthEndpointTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_endpoints_return_ok_when_storage_is_reachable(string path)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_endpoints_are_available_in_production()
    {
        await using var factory = new ApiFactory { EnvironmentName = "Production", PublicUrl = "https://app.example.test" };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_fails_without_details_when_a_store_is_unreachable()
    {
        const string internalDetail = "account-name.documents.example.test refused the connection";
        await using var factory = new ApiFactory { DocumentDatabaseFailure = new HttpRequestException(internalDetail) };
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health");
        using var alive = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        // Liveness does not depend on storage, so the container is not restarted for an outage elsewhere.
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }
}
