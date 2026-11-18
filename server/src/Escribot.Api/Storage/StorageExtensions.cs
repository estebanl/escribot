using Escribot.Api.Data;

namespace Escribot.Api.Storage;

internal static class StorageExtensions
{
    /// <summary>Connection name of the Cosmos DB account, as declared in the AppHost.</summary>
    public const string CosmosConnectionName = "cosmos";

    /// <summary>Connection name of the blob service, as declared in the AppHost.</summary>
    public const string BlobsConnectionName = "blobs";

    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Registers the Cosmos DB and Blob Storage clients through the Aspire client
    /// integrations, and the app's own interfaces over them.
    /// </summary>
    public static IHostApplicationBuilder AddStorage(this IHostApplicationBuilder builder)
    {
        // The integrations' own health checks are off: readiness goes through our
        // interfaces instead, so it tests the same path the app uses.
        builder.AddAzureCosmosClient(
            CosmosConnectionName,
            settings => settings.DisableHealthChecks = true,
            (_, options) => DataExtensions.ConfigureCosmosClient(options));
        builder.AddAzureBlobServiceClient(BlobsConnectionName, settings => settings.DisableHealthChecks = true);

        builder.Services.AddSingleton<IDocumentDatabase, CosmosDocumentDatabase>();
        builder.Services.AddSingleton<IFileStore, BlobFileStore>();
        builder.Services.AddRepositories();

        builder.Services.AddHealthChecks()
            .AddCheck<DocumentDatabaseHealthCheck>("document-database", tags: ["ready"], timeout: HealthCheckTimeout)
            .AddCheck<FileStoreHealthCheck>("file-store", tags: ["ready"], timeout: HealthCheckTimeout);

        return builder;
    }

    /// <summary>
    /// Creates the storage clients before the app accepts requests, so a missing connection
    /// setting stops the app at startup instead of failing on the first request. Creating
    /// the clients makes no network call; reachability is what the health checks report.
    /// </summary>
    public static WebApplication EnsureStorageConfigured(this WebApplication app)
    {
        app.Services.GetRequiredService<IDocumentDatabase>();
        app.Services.GetRequiredService<IFileStore>();

        return app;
    }
}
