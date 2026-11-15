using FluentEscritor.Api.Data.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FluentEscritor.Api.Data;

internal static class DataExtensions
{
    /// <summary>How the Cosmos DB client must be set up for the repositories.</summary>
    public static void ConfigureCosmosClient(CosmosClientOptions options)
    {
        // Query results are read with the same JSON rules documents are written with.
        options.UseSystemTextJsonSerializerWithOptions = DocumentJson.Options;
    }

    /// <summary>Registers the repositories over the Cosmos DB client, and the startup seeding.</summary>
    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<CosmosStore>();
        services.AddSingleton<UserDocuments>();
        services.AddSingleton<LookupStore>();

        services.AddSingleton<IUserAccountRepository, CosmosUserAccountRepository>();
        services.AddSingleton<IAuthSessionStore, CosmosAuthSessionStore>();
        services.AddSingleton<ISettingsRepository, CosmosSettingsRepository>();
        services.AddSingleton<ISubscriptionRepository, CosmosSubscriptionRepository>();

        services.AddSingleton<CosmosPromptRepository>();
        services.AddSingleton<IPromptRepository>(s => s.GetRequiredService<CosmosPromptRepository>());
        services.AddSingleton<ICuratedPromptStore>(s => s.GetRequiredService<CosmosPromptRepository>());

        services.AddSingleton<IWritingRepository, CosmosWritingRepository>();
        services.AddSingleton<IVaultRepository, CosmosVaultRepository>();
        services.AddSingleton<IDrillRepository, CosmosDrillRepository>();
        services.AddSingleton<IAiUsageRepository, CosmosAiUsageRepository>();
        services.AddSingleton<IImageRepository, CosmosImageRepository>();

        services.AddSingleton<IRateLimiter, CosmosRateLimiter>();
        services.AddSingleton<IFreeAiDailyCounter, CosmosFreeAiDailyCounter>();

        services.AddSingleton<CosmosErrorEventRepository>();
        services.AddSingleton<IErrorEventRepository>(s => s.GetRequiredService<CosmosErrorEventRepository>());

        services.AddSingleton<IUserDataRepository, CosmosUserDataRepository>();
        services.AddSingleton<IAdminRepository, CosmosAdminRepository>();

        services.AddSingleton<CuratedPromptSynchronizer>();
        services.AddHostedService<CuratedPromptSeeder>();

        return services;
    }
}
