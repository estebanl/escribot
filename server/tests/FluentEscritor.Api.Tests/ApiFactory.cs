using System.Security.Cryptography;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// Hosts the API in memory with the storage interfaces replaced by fakes, so these
/// tests need no emulators and no container runtime.
/// </summary>
// AppOptions only identifies the API assembly: the test project also references the
// AppHost, so the name "Program" alone would be ambiguous.
public sealed class ApiFactory : WebApplicationFactory<AppOptions>
{
    public string EnvironmentName { get; init; } = "Development";

    /// <summary>Set to null to start the app without the secret.</summary>
    public string? EncryptionSecret { get; init; } = TestSecrets.NewSecret();

    public string? PublicUrl { get; init; }

    public Exception? DocumentDatabaseFailure { get; init; }

    public Exception? FileStoreFailure { get; init; }

    /// <summary>Set to false to keep the real Cosmos DB and Blob Storage clients.</summary>
    public bool UseFakeStorage { get; init; } = true;

    /// <summary>Extra configuration values, as environment variables would supply them.</summary>
    public Dictionary<string, string?> Settings { get; init; } = [];

    public Action<IServiceCollection>? ConfigureServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);

        // Empty values override anything a developer has in their own environment.
        builder.UseSetting("App:EncryptionSecret", EncryptionSecret ?? string.Empty);
        builder.UseSetting("App:PublicUrl", PublicUrl);

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            if (UseFakeStorage)
            {
                services.RemoveAll<IDocumentDatabase>();
                services.RemoveAll<IFileStore>();
                services.AddSingleton<IDocumentDatabase>(new FakeStore(DocumentDatabaseFailure));
                services.AddSingleton<IFileStore>(new FakeStore(FileStoreFailure));

                // Nothing to seed into: there is no database behind the fakes.
                var seeder = services.Single(d => d.ImplementationType == typeof(CuratedPromptSeeder));
                services.Remove(seeder);
            }

            ConfigureServices?.Invoke(services);
        });
    }

    private sealed class FakeStore(Exception? failure) : IDocumentDatabase, IFileStore
    {
        public Task PingAsync(CancellationToken cancellationToken) =>
            failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
}

internal static class TestSecrets
{
    /// <summary>A fresh random value per call, so no secret is written in the repository.</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
