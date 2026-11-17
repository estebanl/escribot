using System.Security.Cryptography;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Email;
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

    /// <summary>The clock of the app and of <see cref="Data"/>. Move it to let sessions, links and rate-limit windows expire.</summary>
    internal ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>What the app has stored, when <see cref="UseFakeStorage"/> is on.</summary>
    public InMemoryData Data => _data ??= new InMemoryData(Clock);

    public FakeFileStore Files { get; } = new();

    /// <summary>The emails the app sent. Set <see cref="UseRealEmailSender"/> to keep the app's own sender instead.</summary>
    public RecordingEmailSender Emails { get; } = new();

    public bool UseRealEmailSender { get; init; }

    private InMemoryData? _data;

    /// <summary>Settings that make the app treat email as configured. The values are placeholders; nothing is sent.</summary>
    public static Dictionary<string, string?> EmailConfigured() => new()
    {
        ["AZURE_COMMUNICATION_CONNECTION_STRING"] = $"endpoint=https://email.example.test/;accesskey={Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}",
        ["EMAIL_FROM"] = "FluentEscritor <no-reply@example.test>",
    };

    /// <summary>A browser-like client: keeps cookies, talks HTTPS, and does not follow redirects.</summary>
    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

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
                services.AddSingleton<IFileStore>(FileStoreFailure is null ? Files : new FakeFileStore(FileStoreFailure));

                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                Replace<IUserAccountRepository>(services);
                Replace<IAuthSessionStore>(services);
                Replace<IOneTimeTokenStore>(services);
                Replace<ISettingsRepository>(services);
                Replace<IVaultRepository>(services);
                Replace<IImageRepository>(services);
                Replace<IRateLimiter>(services);
                Replace<IUserDataRepository>(services);
                Replace<IErrorEventRepository>(services);
                Replace<IPromptRepository>(services);
                Replace<IWritingRepository>(services);
                Replace<IDrillRepository>(services);
                Replace<IAiUsageRepository>(services);
                Replace<IFreeAiDailyCounter>(services);
                Replace<ISubscriptionRepository>(services);
                Replace<IAdminRepository>(services);

                // Nothing to seed into: there is no database behind the fakes.
                var seeder = services.Single(d => d.ImplementationType == typeof(CuratedPromptSeeder));
                services.Remove(seeder);
            }

            if (!UseRealEmailSender)
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Emails);
            }

            ConfigureServices?.Invoke(services);
        });
    }

    private void Replace<T>(IServiceCollection services)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(_ => (T)(object)Data);
    }

    private sealed class FakeStore(Exception? failure) : IDocumentDatabase
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
