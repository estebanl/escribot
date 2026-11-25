using Escribot.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Escribot.Api.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void App_refuses_to_start_without_the_encryption_secret(string environment)
    {
        var exception = StartupFailure<OptionsValidationException>(() => new ApiFactory
        {
            EnvironmentName = environment,
            EncryptionSecret = null,
            PublicUrl = "https://app.example.test",
        });

        Assert.Contains("App:EncryptionSecret is required", exception.Message);
    }

    [Theory]
    [InlineData("FREE_PLAN_EVALUATIONS_PER_MONTH", "-1")]
    [InlineData("FREE_PLAN_ASSISTS_PER_MONTH", "lots")]
    [InlineData("PRO_PLAN_EVALUATIONS_PER_MONTH", "2.5")]
    public void App_refuses_to_start_with_a_bad_plan_allowance(string name, string value)
    {
        var exception = StartupFailure<InvalidOperationException>(() => new ApiFactory { Settings = new() { [name] = value } });

        Assert.Contains($"{name} must be a whole number", exception.Message);
    }

    [Fact]
    public void App_refuses_to_start_in_production_without_the_public_url()
    {
        var exception = StartupFailure<OptionsValidationException>(() => new ApiFactory { EnvironmentName = "Production" });

        Assert.Contains("App:PublicUrl is required in production", exception.Message);
    }

    [Fact]
    public void App_refuses_to_start_without_the_storage_connections()
    {
        var exception = StartupFailure<InvalidOperationException>(() => new ApiFactory { UseFakeStorage = false });

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

    /// <summary>
    /// Starts the app, which is expected to fail, and returns the failure. The app and the
    /// test host run on different threads: when the app fails and disposes its services
    /// before the test host has read from them, the test host reports that disposal
    /// instead of the real cause. That says nothing about the app, so it is tried again.
    /// </summary>
    private static TException StartupFailure<TException>(Func<ApiFactory> createFactory)
        where TException : Exception
    {
        for (var attempt = 1; ; attempt++)
        {
            using var factory = createFactory();
            try
            {
                return Assert.Throws<TException>(() => factory.CreateClient());
            }
            catch (Xunit.Sdk.ThrowsException ex) when (ex.InnerException is ObjectDisposedException && attempt < 20)
            {
            }
        }
    }

    private static ValidateOptionsResult Validate(string environment, AppOptions options) =>
        new AppOptionsValidator(new TestEnvironment(environment)).Validate(name: null, options);

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Escribot.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
