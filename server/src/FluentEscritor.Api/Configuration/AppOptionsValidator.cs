using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Configuration;

/// <summary>
/// Checks <see cref="AppOptions"/> at startup. Messages name the setting and never include its value.
/// </summary>
internal sealed class AppOptionsValidator(IHostEnvironment environment) : IValidateOptions<AppOptions>
{
    public ValidateOptionsResult Validate(string? name, AppOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.EncryptionSecret))
        {
            failures.Add("App:EncryptionSecret is required (environment variable App__EncryptionSecret).");
        }
        else if (options.EncryptionSecret.Length < AppOptions.MinimumEncryptionSecretLength)
        {
            failures.Add($"App:EncryptionSecret must be at least {AppOptions.MinimumEncryptionSecretLength} characters long.");
        }

        if (options.PublicUrl is null)
        {
            if (environment.IsProduction())
            {
                failures.Add("App:PublicUrl is required in production (environment variable App__PublicUrl).");
            }
        }
        else if (!options.PublicUrl.IsAbsoluteUri)
        {
            failures.Add("App:PublicUrl must be an absolute URL.");
        }
        else if (environment.IsProduction() && options.PublicUrl.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("App:PublicUrl must use HTTPS in production.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
