using Escribot.Api.Ai;
using Escribot.Api.Ai.Agents;
using Escribot.Api.Billing;
using Escribot.Api.Common;

namespace Escribot.Api.Api;

internal static class ApiExtensions
{
    /// <summary>Registers what the data endpoints are built from.</summary>
    public static IHostApplicationBuilder AddDataApi(this IHostApplicationBuilder builder)
    {
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<ApiKeyProtector>();
        builder.Services.AddSingleton<OpenRouterCatalogCache>();
        builder.Services.AddSingleton<PlanResolver>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddSingleton<ImageStore>();
        builder.Services.AddSingleton<ErrorReporter>();

        // AI provider HTTP clients: timeouts are enforced per call by AiCompletionService so
        // Aspire's default 10s resilience attempt timeout does not cut off 35-45s LLM calls or
        // retry non-idempotent POST streams.
        AddAiHttpClient(builder.Services, UserChatClientFactory.OpenAiHttpClientName);
        AddAiHttpClient(builder.Services, UserChatClientFactory.OpenRouterHttpClientName);
        AddAiHttpClient(builder.Services, UserChatClientFactory.DeepSeekHttpClientName);
        AddAiHttpClient(builder.Services, UserChatClientFactory.GeminiHttpClientName);
        AddAiHttpClient(builder.Services, UserChatClientFactory.AnthropicHttpClientName);

#pragma warning disable EXTEXP0001
        builder.Services.AddHttpClient<OpenRouterModelsClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllResilienceHandlers();
        builder.Services.AddHttpClient<ProviderModelsClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.Services.AddSingleton<IAiChatClientFactory, UserChatClientFactory>();
        builder.Services.AddSingleton<EscribotAgentFactory>();
        builder.Services.AddScoped<UserHistoryTools>();
        builder.Services.AddSingleton<AiConfigResolver>();
        builder.Services.AddSingleton<AiQuotaTracker>();
        builder.Services.AddScoped<AiCompletionService>();
        builder.Services.AddScoped<AiAccessGuard>();
        builder.Services.AddScoped<ExaminerService>();

        // Billing. Stripe.net retries by itself (and with idempotency keys), so the default
        // resilience handlers, which would also retry POSTs, are removed.
        builder.Services.AddSingleton<StripeSettings>();
        builder.Services.AddSingleton<IStripeGateway, StripeGateway>();
#pragma warning disable EXTEXP0001
        builder.Services.AddHttpClient(StripeGateway.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(80))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<SubscriptionSync>();
        builder.Services.AddSingleton<StripeWebhookReader>();
        builder.Services.AddSingleton<PriceCatalog>();

        return builder;
    }

    private static void AddAiHttpClient(IServiceCollection services, string name)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }

    /// <summary>
    /// Maps the data endpoints under <c>/api</c>. They need no authorization of their own:
    /// <see cref="Auth.ApiGuard"/> has already refused every signed-out request to them.
    /// </summary>
    public static WebApplication MapDataApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapWritingEndpoints();
        api.MapAccountEndpoints();
        api.MapBackupEndpoints();
        api.MapAiEndpoints();
        api.MapBillingEndpoints();
        api.MapAdminEndpoints();
        api.MapPageEndpoints();
        return app;
    }
}
