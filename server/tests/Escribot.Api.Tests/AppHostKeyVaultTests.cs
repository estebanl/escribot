using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Escribot.Api.Tests;

/// <summary>
/// Where the AppHost puts the API's secrets. These tests only build the application model;
/// nothing is started or deployed, so they need neither a container runtime nor Azure.
/// </summary>
public class AppHostKeyVaultTests
{
    private static readonly string[] SecretVariables = ["App__EncryptionSecret", "STRIPE_SECRET_KEY", "STRIPE_WEBHOOK_SECRET"];

    [Fact]
    public async Task Published_api_reads_every_secret_from_key_vault()
    {
        await using var app = await BuildAsync(publish: true);
        var vault = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureKeyVaultResource>());

        var environment = await GetApiEnvironmentAsync(app);

        foreach (var variable in SecretVariables)
        {
            Assert.StartsWith($"{{{vault.Name}.secrets.", environment[variable], StringComparison.Ordinal);
        }

        Assert.DoesNotContain(environment.Values, value => value.Contains("sk_test_", StringComparison.Ordinal) || value.Contains("whsec_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Local_run_has_no_key_vault()
    {
        await using var app = await BuildAsync(publish: false);

        Assert.Empty(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<AzureKeyVaultResource>());
    }

    private static async Task<DistributedApplication> BuildAsync(bool publish)
    {
        List<string> args =
        [
            "--STRIPE_SECRET_KEY=sk_test_not_a_real_key",
            "--STRIPE_WEBHOOK_SECRET=whsec_not_a_real_secret",
        ];
        if (publish)
        {
            args.AddRange(["--operation", "publish", "--publisher", "manifest", "--output-path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]);
        }

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Escribot_AppHost>([.. args]);
        appHost.Configuration["Parameters:encryption-secret"] = TestSecrets.NewSecret();

        return await appHost.BuildAsync();
    }

    private static async Task<Dictionary<string, string>> GetApiEnvironmentAsync(DistributedApplication app)
    {
        var api = app.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<IResourceWithEnvironment>()
            .Single(r => r.Name == "api");

        var configuration = await ExecutionConfigurationBuilder.Create(api)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), NullLogger.Instance, CancellationToken.None);

        return configuration.EnvironmentVariables.ToDictionary(v => v.Key, v => v.Value);
    }
}
