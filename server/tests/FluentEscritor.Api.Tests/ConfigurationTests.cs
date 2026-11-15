using FluentEscritor.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FluentEscritor.Api.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void App_refuses_to_start_without_the_encryption_secret(string environment)
    {
        using var factory = new ApiFactory
        {
            EnvironmentName = environment,
            EncryptionSecret = null,
            PublicUrl = "https://app.example.test",
        };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("App:EncryptionSecret is required", exception.Message);
    }

    [Fact]
    public void App_refuses_to_start_in_production_without_the_public_url()
    {
        using var factory = new ApiFactory { EnvironmentName = "Production" };

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("App:PublicUrl is required in production", exception.Message);
    }

    [Fact]
    public void App_refuses_to_start_without_the_storage_connections()
    {
        using var factory = new ApiFactory { UseFakeStorage = false };

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings", exception.Message);
    }

    [Fact]
    public void Settings_are_read_from_configuration()
    {
        var secret = TestSecrets.NewSecret();
        using var factory = new ApiFactory { EncryptionSecret = secret, PublicUrl = "https://app.example.test" };

        var options = factory.Services.GetRequiredService<IOptions<AppOptions>>().Value;

        Assert.Equal(secret, options.EncryptionSecret);
        Assert.Equal(new Uri("https://app.example.test"), options.PublicUrl);
    }

    [Fact]
    public void A_short_encryption_secret_is_rejected_without_echoing_it()
    {
        const string shortSecret = "too-short-to-be-a-key";

        var result = Validate("Development", new AppOptions { EncryptionSecret = shortSecret });

        Assert.True(result.Failed);
        Assert.Contains("at least 32 characters", result.FailureMessage);
        Assert.DoesNotContain(shortSecret, result.FailureMessage);
    }

    [Fact]
    public void Production_requires_an_https_public_url()
    {
        var options = new AppOptions { EncryptionSecret = TestSecrets.NewSecret(), PublicUrl = new Uri("http://app.example.test") };

        Assert.True(Validate("Production", options).Failed);
        Assert.True(Validate("Development", options).Succeeded);
    }

    [Fact]
    public void Development_needs_only_the_encryption_secret()
    {
        var result = Validate("Development", new AppOptions { EncryptionSecret = TestSecrets.NewSecret() });

        Assert.True(result.Succeeded);
    }

    private static ValidateOptionsResult Validate(string environment, AppOptions options) =>
        new AppOptionsValidator(new TestEnvironment(environment)).Validate(name: null, options);

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "FluentEscritor.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
