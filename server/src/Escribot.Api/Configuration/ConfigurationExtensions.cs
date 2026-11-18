using Microsoft.Extensions.Options;

namespace Escribot.Api.Configuration;

internal static class ConfigurationExtensions
{
    /// <summary>
    /// Binds <see cref="AppOptions"/> and validates it when the host starts, so the app
    /// refuses to run with missing or invalid settings instead of failing on first use.
    /// </summary>
    public static IHostApplicationBuilder AddAppOptions(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IValidateOptions<AppOptions>, AppOptionsValidator>();
        builder.Services.AddOptions<AppOptions>()
            .BindConfiguration(AppOptions.SectionName)
            .ValidateOnStart();

        return builder;
    }
}
