using System.Net;
using System.Text;
using System.Text.Json;
using FluentEscritor.Api.Data.Models;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// "Continue with Google" through the real sign-in handler. Google itself is replaced
/// by a stand-in that answers the two server-to-server calls.
/// </summary>
public class GoogleSignInTests
{
    private const string Email = "ana@example.test";

    [Fact]
    public async Task Google_sign_in_is_off_unless_both_settings_are_present()
    {
        await using var factory = new ApiFactory { Settings = new() { ["GOOGLE_CLIENT_ID"] = "only-the-id" } };
        using var browser = factory.CreateBrowser();

        using var start = await browser.GetAsync("/api/auth/sign-in/google");
        using var config = await browser.GetAsync("/api/auth/config");

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.False((await AuthEndpointTests.Json(config)).GetProperty("googleEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_first_google_sign_in_creates_a_verified_account_and_signs_in()
    {
        var google = new FakeGoogle { Email = Email, Name = "Ana" };
        await using var factory = Factory(google);
        using var browser = factory.CreateBrowser();

        using var callback = await SignInWithGoogleAsync(browser, "/vault");

        Assert.Equal("/vault", callback.Headers.Location!.OriginalString);
        var account = Assert.Single(factory.Data.Accounts.Values);
        Assert.True(account.EmailVerified);
        Assert.Null(account.PasswordHash);
        Assert.Equal(new[] { ("google", google.Subject) }, account.ExternalLogins.Select(l => (l.Provider, l.ProviderKey)));
        Assert.True(factory.Data.Settings.ContainsKey(account.Id));

        using var me = await browser.GetAsync("/api/auth/me");
        var user = (await AuthEndpointTests.Json(me)).GetProperty("user");
        Assert.Equal(Email, user.GetProperty("email").GetString());
        Assert.False(user.GetProperty("hasPassword").GetBoolean());

        // Signing in again finds the same account.
        using var again = await SignInWithGoogleAsync(factory.CreateBrowser(), "/");
        Assert.Single(factory.Data.Accounts);
        Assert.Single(factory.Data.Accounts.Values.Single().ExternalLogins);
    }

    [Fact]
    public async Task Google_sign_in_takes_over_an_unverified_account_and_drops_its_unproven_password()
    {
        // Someone registers the address first, hoping its real owner later signs in
        // with Google and lands in an account whose password they know.
        var password = $"pw-{TestSecrets.NewSecret()}";
        await using var factory = Factory(new FakeGoogle { Email = Email });
        using var squatter = factory.CreateBrowser();
        (await AuthEndpointTests.SignUpAsync(squatter, Email, password)).Dispose();

        using var owner = factory.CreateBrowser();
        (await SignInWithGoogleAsync(owner, "/")).Dispose();

        var account = Assert.Single(factory.Data.Accounts.Values);
        Assert.True(account.EmailVerified);
        Assert.Null(account.PasswordHash);
        using var squatterSession = await squatter.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, squatterSession.StatusCode);
        using var squatterSignIn = await AuthEndpointTests.SignInAsync(squatter, Email, password);
        Assert.Equal(HttpStatusCode.Unauthorized, squatterSignIn.StatusCode);
        using var ownerSession = await owner.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, ownerSession.StatusCode);
    }

    [Fact]
    public async Task Google_sign_in_keeps_the_password_of_an_account_that_was_already_verified()
    {
        var password = $"pw-{TestSecrets.NewSecret()}";
        await using var factory = Factory(new FakeGoogle { Email = Email });
        using var browser = factory.CreateBrowser();
        (await AuthEndpointTests.SignUpAsync(browser, Email, password)).Dispose();
        (await browser.GetAsync(AuthEndpointTests.Link(factory.Emails.Sent.Single().Text))).Dispose();

        (await SignInWithGoogleAsync(factory.CreateBrowser(), "/")).Dispose();

        Assert.NotNull(factory.Data.Accounts.Values.Single().PasswordHash);
        using var stillSignedIn = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
    }

    [Fact]
    public async Task An_address_google_has_not_verified_is_not_accepted()
    {
        await using var factory = Factory(new FakeGoogle { Email = Email, EmailVerified = false });
        using var browser = factory.CreateBrowser();

        using var callback = await SignInWithGoogleAsync(browser, "/");

        Assert.Equal("/login?error=google_email_not_verified", callback.Headers.Location!.OriginalString);
        Assert.Empty(factory.Data.Accounts);
        Assert.Empty(factory.Data.Sessions);
    }

    [Fact]
    public async Task The_allowlist_applies_to_google_sign_ups_too()
    {
        await using var factory = Factory(new FakeGoogle { Email = Email }, new() { ["ALLOWED_SIGNUP_EMAILS"] = "invited@example.test" });
        using var browser = factory.CreateBrowser();

        using var callback = await SignInWithGoogleAsync(browser, "/");

        Assert.Equal("/login?error=signup_not_allowed", callback.Headers.Location!.OriginalString);
        Assert.Empty(factory.Data.Accounts);
    }

    [Fact]
    public async Task A_callback_that_does_not_belong_to_a_sign_in_started_in_this_browser_is_refused()
    {
        await using var factory = Factory(new FakeGoogle { Email = Email });
        using var victim = factory.CreateBrowser();
        using var attacker = factory.CreateBrowser();
        using var start = await attacker.GetAsync("/api/auth/sign-in/google");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        using var forged = await victim.GetAsync($"/api/auth/callback/google?code=stolen&state={Uri.EscapeDataString(state)}");
        using var madeUp = await victim.GetAsync("/api/auth/callback/google?code=stolen&state=not-a-real-state");

        Assert.Equal("/login?error=google_sign_in_failed", forged.Headers.Location!.OriginalString);
        Assert.Equal("/login?error=google_sign_in_failed", madeUp.Headers.Location!.OriginalString);
        Assert.Empty(factory.Data.Sessions);
    }

    [Fact]
    public async Task The_sign_in_started_on_one_instance_can_finish_on_another()
    {
        // Two instances share only their settings, as two replicas do.
        var secret = TestSecrets.NewSecret();
        var google = new FakeGoogle { Email = Email };
        await using var first = Factory(google, secret: secret);
        await using var second = Factory(google, secret: secret);
        using var onFirst = first.CreateBrowser();
        using var start = await onFirst.GetAsync("/api/auth/sign-in/google");
        var state = QueryHelpers.ParseQuery(start.Headers.Location!.Query)["state"].ToString();

        using var onSecond = second.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/callback/google?code=a-code&state={Uri.EscapeDataString(state)}");
        request.Headers.Add("Cookie", string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        using var callback = await onSecond.SendAsync(request);

        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        Assert.Single(second.Data.Accounts);
    }

    private static ApiFactory Factory(FakeGoogle google, Dictionary<string, string?>? settings = null, string? secret = null) => new()
    {
        EncryptionSecret = secret ?? TestSecrets.NewSecret(),
        Settings = new(settings ?? [])
        {
            ["GOOGLE_CLIENT_ID"] = "test-client-id",
            ["GOOGLE_CLIENT_SECRET"] = TestSecrets.NewSecret(),
        },
        ConfigureServices = services => services.Configure<GoogleOptions>(GoogleDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = google),
    };

    /// <summary>Starts the sign-in, pretends the person approved it at Google, and returns the callback's response.</summary>
    private static async Task<HttpResponseMessage> SignInWithGoogleAsync(HttpClient browser, string callbackUrl)
    {
        using var start = await browser.GetAsync($"/api/auth/sign-in/google?callbackURL={Uri.EscapeDataString(callbackUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var toGoogle = start.Headers.Location!;
        Assert.Equal("accounts.google.com", toGoogle.Host);
        var query = QueryHelpers.ParseQuery(toGoogle.Query);
        Assert.Equal("https://localhost/api/auth/callback/google", query["redirect_uri"].ToString());

        return await browser.GetAsync($"/api/auth/callback/google?code=a-code&state={Uri.EscapeDataString(query["state"].ToString())}");
    }

    private sealed class FakeGoogle : HttpMessageHandler
    {
        public string Subject { get; } = Guid.NewGuid().ToString("N");

        public required string Email { get; init; }

        public bool EmailVerified { get; init; } = true;

        public string? Name { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body = request.Method == HttpMethod.Post
                ? new { access_token = "an-access-token", token_type = "Bearer", expires_in = 3600 }
                : new { sub = Subject, id = Subject, email = Email, email_verified = EmailVerified, name = Name };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }
}
