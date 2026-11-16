using FluentEscritor.Api.Common;

namespace FluentEscritor.Api.Api;

internal static class ApiExtensions
{
    /// <summary>Registers what the data endpoints are built from.</summary>
    public static IHostApplicationBuilder AddDataApi(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ApiKeyProtector>();
        builder.Services.AddSingleton<PlanResolver>();
        builder.Services.AddSingleton<UsageService>();
        builder.Services.AddSingleton<ImageStore>();
        builder.Services.AddSingleton<ErrorReporter>();
        return builder;
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
        return app;
    }
}
