using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Testing;
using FluentEscritor.DataModel;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// Runs <c>scripts/test-isolation.mjs</c> against the published Docker image, the way it runs
/// in Azure: Production environment, with the Cosmos DB emulator and Azurite as its stores.
/// Skipped unless <c>ISOLATION_TEST_IMAGE</c> names a local image; CI builds one first:
/// <code>
/// docker build -f server/Dockerfile -t fluentescritor-api:ci .
/// ISOLATION_TEST_IMAGE=fluentescritor-api:ci dotnet test --filter "Category=Image"
/// </code>
/// Needs a container runtime and Node.js.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Image")]
public class ImageIsolationTests
{
    private const string ImageVariable = "ISOLATION_TEST_IMAGE";

    // Generous, because a first run pulls the emulator images.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(3);

    [ImageFact]
    public async Task One_account_cannot_reach_another_accounts_data_in_the_image()
    {
        var image = Environment.GetEnvironmentVariable(ImageVariable)!;
        var port = FreePort();
        var baseUrl = $"http://localhost:{port}";

        using var timeout = new CancellationTokenSource(StartTimeout);
        var cancellationToken = timeout.Token;

        // The AppHost as it is, with the image in place of the API project.
        // As arguments: the AppHost reads them while it declares its resources, before
        // CreateAsync returns. The image serves the browser app itself.
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FluentEscritor_AppHost>(
            [$"--Parameters:encryption-secret={TestSecrets.NewSecret()}", "--BrowserApp:Enabled=false"],
            cancellationToken);

        builder.Resources.Remove(builder.Resources.OfType<ProjectResource>().Single(r => r.Name == "api"));
        var database = Resource<AzureCosmosDBDatabaseResource>(builder, DataModelSchema.DatabaseName);
        var cosmos = builder.CreateResourceBuilder(database.Resource.Parent);
        var files = Resource<AzureBlobStorageContainerResource>(builder, DataModelSchema.FilesBlobContainer);
        var blobs = Resource<AzureBlobStorageResource>(builder, "blobs");

        builder.AddContainer("api", image)
            .WithImagePullPolicy(ImagePullPolicy.Never)
            .WithHttpEndpoint(port: port, targetPort: 8080, name: "http", isProxied: false)
            .WithReference(cosmos).WaitFor(database)
            .WithReference(blobs).WaitFor(files)
            // Production, as in Azure. PublicUrl must then be HTTPS; the script's requests come
            // from the API's own address, which the origin check also accepts.
            .WithEnvironment("App__PublicUrl", "https://app.example.test")
            .WithEnvironment("App__EncryptionSecret", TestSecrets.NewSecret());

        await using var app = await builder.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await WaitUntilHealthyAsync(baseUrl, cancellationToken);

        var (exitCode, output) = await RunScriptAsync(baseUrl);

        Assert.True(exitCode == 0, $"The isolation test failed (exit code {exitCode}):\n{output}");
    }

    /// <summary>
    /// Polls <c>/health</c>, which answers 200 once Cosmos DB and Blob Storage are reachable.
    /// Polled directly on the published port: that is the address the script uses.
    /// </summary>
    private static async Task WaitUntilHealthyAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static IResourceBuilder<T> Resource<T>(IDistributedApplicationTestingBuilder builder, string name)
        where T : IResource =>
        builder.CreateResourceBuilder(builder.Resources.OfType<T>().Single(r => r.Name == name));

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(string baseUrl)
    {
        var script = Path.Combine(RepositoryRoot(), "scripts", "test-isolation.mjs");
        var start = new ProcessStartInfo("node", [script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["BASE_URL"] = baseUrl;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(ScriptTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The isolation test did not finish within {ScriptTimeout}.\n{await stdout}{await stderr}");
        }

        return (process.ExitCode, $"{await stdout}{await stderr}");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "test-isolation.mjs")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("scripts/test-isolation.mjs was not found above the test's output directory.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A test that runs only when <c>ISOLATION_TEST_IMAGE</c> is set.</summary>
    private sealed class ImageFactAttribute : FactAttribute
    {
        public ImageFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ImageVariable)))
            {
                Skip = $"Set {ImageVariable} to a local image of the API to run this test.";
            }
        }
    }
}
