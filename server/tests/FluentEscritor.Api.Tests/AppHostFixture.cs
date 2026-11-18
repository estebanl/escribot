using Aspire.Hosting;
using Aspire.Hosting.Testing;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// Starts the whole AppHost once for all integration tests: the API, the Cosmos DB
/// emulator and Azurite, as containers. Needs a running container runtime.
/// To skip these tests: dotnet test --filter "Category!=Integration".
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    // Generous, because a first run pulls the emulator images.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private IHost? _repositoryHost;

    public DistributedApplication App { get; private set; } = null!;

    /// <summary>
    /// The repositories, registered exactly as in the API and pointed at the emulator.
    /// This host is never started, so the startup seeding does not run in the test process.
    /// </summary>
    public IServiceProvider Services => _repositoryHost!.Services;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        var cancellationToken = timeout.Token;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.FluentEscritor_AppHost>(cancellationToken);
        // Supplied here so the tests neither need nor write the developer's user secrets.
        appHost.Configuration["Parameters:encryption-secret"] = TestSecrets.NewSecret();
        // These tests call the API directly; the Vite dev server would only need npm install.
        appHost.Configuration["BrowserApp:Enabled"] = "false";

        App = await appHost.BuildAsync(cancellationToken);
        await App.StartAsync(cancellationToken);

        await App.ResourceNotifications.WaitForResourceHealthyAsync("cosmos", cancellationToken);
        await App.ResourceNotifications.WaitForResourceHealthyAsync("storage", cancellationToken);
        await App.ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration[$"ConnectionStrings:{StorageExtensions.CosmosConnectionName}"] =
            await App.GetConnectionStringAsync(StorageExtensions.CosmosConnectionName, cancellationToken);
        builder.AddAzureCosmosClient(
            StorageExtensions.CosmosConnectionName,
            settings => settings.DisableHealthChecks = true,
            DataExtensions.ConfigureCosmosClient);
        builder.Services.AddRepositories();
        _repositoryHost = builder.Build();

        // The API again, in this process and on the same emulators, so tests can call its
        // endpoints with their own settings. Rate limits are counted in memory here:
        // counted in the database, the tests' many sign-ups would exhaust the hourly limit.
        var rateLimits = new TestRateLimiter();
        Api = new ApiFactory
        {
            UseFakeStorage = false,
            Settings = new()
            {
                [$"ConnectionStrings:{StorageExtensions.CosmosConnectionName}"] = await App.GetConnectionStringAsync(StorageExtensions.CosmosConnectionName, cancellationToken),
                [$"ConnectionStrings:{StorageExtensions.BlobsConnectionName}"] = await App.GetConnectionStringAsync(StorageExtensions.BlobsConnectionName, cancellationToken),
            },
            ConfigureServices = services =>
            {
                services.RemoveAll<IRateLimiter>();
                services.AddSingleton<IRateLimiter>(rateLimits);
            },
        };

        // Before any test writes: what the API itself seeded when it started.
        var shipped = CuratedPromptSeed.LoadEmbedded().ToDictionary(s => s.Prompt.Id, s => s.Hash);
        while (!cancellationToken.IsCancellationRequested)
        {
            CuratedPromptsSeededByApi = await Get<ICuratedPromptStore>().GetSeedHashesAsync(cancellationToken);
            if (CuratedPromptsSeededByApi.Count == shipped.Count)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    /// <summary>The curated prompts found in the database once the API had started.</summary>
    public IReadOnlyDictionary<string, string> CuratedPromptsSeededByApi { get; private set; } = new Dictionary<string, string>();

    /// <summary>The API hosted in the test process, on the real Cosmos DB and Blob Storage emulators.</summary>
    public ApiFactory Api { get; private set; } = null!;

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        _repositoryHost?.Dispose();
        await App.DisposeAsync();
    }

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>A fresh synthetic user id, so tests never see each other's data.</summary>
    public static string NewUserId() => $"test-user-{Guid.NewGuid():N}";

    public static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}

[CollectionDefinition(Name)]
public sealed class AppHostCollection : ICollectionFixture<AppHostFixture>
{
    public const string Name = "AppHost";
}

/// <summary>Counts in memory, and lets the sign-in endpoints through however often the tests call them.</summary>
internal sealed class TestRateLimiter : IRateLimiter
{
    private readonly InMemoryData _counts = new(TimeProvider.System);

    public Task<Data.Models.RateLimitResult> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default) =>
        key.StartsWith("auth:", StringComparison.Ordinal)
            ? Task.FromResult(new Data.Models.RateLimitResult(true, 1))
            : _counts.CheckAsync(key, limit, window, cancellationToken);
}

/// <summary>A clock the test sets, for code that depends on the time.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
