using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// Starts the whole AppHost, including the Cosmos DB emulator and Azurite as containers.
/// Needs a running container runtime. To skip it: dotnet test --filter "Category!=Integration".
/// </summary>
[Trait("Category", "Integration")]
public class AppHostTests
{
    // Generous, because a first run pulls the emulator images.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Api_is_healthy_with_both_emulators_attached()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var cancellationToken = timeout.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FluentEscritor_AppHost>(cancellationToken);
        // Supplied here so the test neither needs nor writes the developer's user secrets.
        appHost.Configuration["Parameters:encryption-secret"] = TestSecrets.NewSecret();

        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("cosmos", cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("storage", cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);

        // /health pings Cosmos DB and Blob Storage through the API's own storage interfaces.
        using var client = app.CreateHttpClient("api");
        using var response = await client.GetAsync("/health", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
