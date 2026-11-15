using System.Security.Cryptography;
using System.Text;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FluentEscritor.Api.Tests;

public class AuthUnitTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/vault?tab=1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("vault", false)]
    [InlineData("//evil.example.test", false)]
    [InlineData("/\\evil.example.test", false)]
    [InlineData("https://evil.example.test/", false)]
    [InlineData("/ok\r\nSet-Cookie: x=y", false)]
    public void Only_a_path_on_this_site_is_a_redirect_target(string? path, bool local)
    {
        Assert.Equal(local, LocalPath.IsLocal(path));
        Assert.Equal(local ? path : "/", LocalPath.OrRoot(path));
    }

    [Fact]
    public void Protected_state_round_trips_and_is_bound_to_its_secret_and_purpose()
    {
        var secret = TestSecrets.NewSecret();
        var plaintext = Encoding.UTF8.GetBytes("state of a sign-in");
        var protector = new SecretDataProtectionProvider(secret).CreateProtector("a", "b");

        var sealedValue = protector.Protect(plaintext);

        Assert.Equal(plaintext, new SecretDataProtectionProvider(secret).CreateProtector("a", "b").Unprotect(sealedValue));
        Assert.NotEqual(sealedValue, protector.Protect(plaintext));
        Assert.DoesNotContain("state of a sign-in", Encoding.UTF8.GetString(sealedValue));

        // Another secret, another purpose, a purpose chain that merely joins to the same text, or a changed byte.
        Assert.ThrowsAny<CryptographicException>(() => new SecretDataProtectionProvider(TestSecrets.NewSecret()).CreateProtector("a", "b").Unprotect(sealedValue));
        Assert.ThrowsAny<CryptographicException>(() => new SecretDataProtectionProvider(secret).CreateProtector("a", "c").Unprotect(sealedValue));
        Assert.ThrowsAny<CryptographicException>(() => new SecretDataProtectionProvider(secret).CreateProtector("a\n1:b").Unprotect(sealedValue));
        sealedValue[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedValue));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect([1, 2, 3]));
    }

    [Theory]
    [InlineData("no-reply@example.test", "no-reply@example.test")]
    [InlineData("FluentEscritor <no-reply@example.test>", "no-reply@example.test")]
    [InlineData("  \"Fluent Escritor\" <no-reply@example.test> ", "no-reply@example.test")]
    [InlineData("not an address", "")]
    [InlineData(null, "")]
    public void The_sender_is_reduced_to_a_bare_address(string? emailFrom, string expected)
    {
        Assert.Equal(expected, EmailSettings.BareAddress(emailFrom));
    }

    [Theory]
    [InlineData("Development", false, true, true, false)]
    [InlineData("Production", false, false, false, false)]
    [InlineData("Development", true, true, true, true)]
    [InlineData("Production", true, true, true, true)]
    public void What_email_allows_depends_on_configuration_and_environment(string environment, bool configured, bool reset, bool sendsLinks, bool verificationRequired)
    {
        var settings = new EmailSettings(
            new ConfigurationBuilder().AddInMemoryCollection(configured ? ApiFactory.EmailConfigured() : []).Build(),
            new TestEnvironment(environment));

        Assert.Equal(configured, settings.IsConfigured);
        Assert.Equal(reset, settings.PasswordResetAvailable);
        Assert.Equal(sendsLinks, settings.SendsVerificationLinks);
        Assert.Equal(verificationRequired, settings.VerificationRequired);
    }

    [Fact]
    public void Email_counts_as_configured_only_with_both_settings()
    {
        var onlyOne = ApiFactory.EmailConfigured();
        onlyOne.Remove("EMAIL_FROM");

        var settings = new EmailSettings(new ConfigurationBuilder().AddInMemoryCollection(onlyOne).Build(), new TestEnvironment("Production"));

        Assert.False(settings.IsConfigured);
    }

    [Theory]
    [InlineData("Development", false, typeof(ConsoleEmailSender))]
    [InlineData("Production", false, typeof(DisabledEmailSender))]
    [InlineData("Production", true, typeof(AzureCommunicationEmailSender))]
    public void The_sender_matches_configuration_and_environment(string environment, bool configured, Type expected)
    {
        using var factory = new ApiFactory
        {
            EnvironmentName = environment,
            PublicUrl = "https://app.example.test",
            UseRealEmailSender = true,
            Settings = configured ? ApiFactory.EmailConfigured() : [],
        };

        Assert.IsType(expected, factory.Services.GetRequiredService<IEmailSender>());
    }

    [Fact]
    public void A_malformed_email_connection_string_stops_the_app_without_echoing_it()
    {
        const string malformed = "this-is-not-a-connection-string";
        var settings = ApiFactory.EmailConfigured();
        settings["AZURE_COMMUNICATION_CONNECTION_STRING"] = malformed;

        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = new ApiFactory { UseRealEmailSender = true, Settings = settings };
            factory.CreateClient().Dispose();
        });

        Assert.Contains("AZURE_COMMUNICATION_CONNECTION_STRING is not a valid connection string", exception.ToString());
        Assert.DoesNotContain(malformed, exception.ToString());
    }

    [Fact]
    public void The_starter_vault_ships_with_the_app()
    {
        var items = NewUserProvisioner.LoadStarterVault();

        Assert.Equal(4, items.Count);
        Assert.All(items, item =>
        {
            Assert.Contains(item.ItemType, new[] { "error", "collocation" });
            Assert.NotEmpty(item.OriginalText);
            Assert.NotEmpty(item.UpgradedText);
        });
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
