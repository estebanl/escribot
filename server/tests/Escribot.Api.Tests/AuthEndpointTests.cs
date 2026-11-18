using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Escribot.Api.Auth;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Escribot.Api.Tests;

/// <summary>
/// Sign-in through the real endpoints, with the repositories kept in memory.
/// All accounts are synthetic and every password is generated per test.
/// </summary>
public partial class AuthEndpointTests
{
    private const string Email = "ana@example.test";

    private readonly string _password = $"pw-{TestSecrets.NewSecret()}";

    // ---- Sign-up ----

    [Fact]
    public async Task Sign_up_creates_the_account_signs_in_and_gives_it_its_starting_data()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await SignUpAsync(browser, Email, _password, "Ana");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = (await Json(response)).GetProperty("user");
        Assert.Equal(Email, user.GetProperty("email").GetString());
        Assert.Equal("Ana", user.GetProperty("name").GetString());
        Assert.False(user.GetProperty("emailVerified").GetBoolean());
        Assert.True(user.GetProperty("hasPassword").GetBoolean());

        var account = Assert.Single(factory.Data.Accounts.Values);
        Assert.NotEqual(_password, account.PasswordHash);
        Assert.DoesNotContain(_password, account.PasswordHash);
        Assert.True(factory.Data.Settings.ContainsKey(account.Id));
        Assert.Equal(4, factory.Data.Vault[account.Id].Count);
        Assert.Equal(4, factory.Data.Vault[account.Id].Select(v => v.Id).Distinct().Count());

        using var me = await browser.GetAsync("/api/auth/me");
        Assert.Equal(account.Id, (await Json(me)).GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Sign_up_never_returns_or_stores_the_password()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await SignUpAsync(browser, Email, _password);

        Assert.DoesNotContain(_password, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("passwordHash", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.Emails.Sent, e => e.Text.Contains(_password));
    }

    [Theory]
    [InlineData("not-an-email", "long-enough-password", "INVALID_EMAIL")]
    [InlineData("", "long-enough-password", "INVALID_EMAIL")]
    [InlineData("two words@example.test", "long-enough-password", "INVALID_EMAIL")]
    [InlineData("ana@example.test", "short", "INVALID_PASSWORD")]
    [InlineData("ana@example.test", null, "INVALID_PASSWORD")]
    public async Task Sign_up_rejects_an_invalid_email_or_password(string email, string? password, string code)
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await SignUpAsync(browser, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await Json(response)).GetProperty("code").GetString());
        Assert.Empty(factory.Data.Accounts);
    }

    [Fact]
    public async Task An_email_can_be_registered_only_once_whatever_its_letter_case()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        using var other = factory.CreateBrowser();
        using var response = await SignUpAsync(other, "ANA@Example.Test", $"pw-{TestSecrets.NewSecret()}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("USER_ALREADY_EXISTS", (await Json(response)).GetProperty("code").GetString());
        Assert.Single(factory.Data.Accounts);
        using var me = await other.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ---- Sign-in and sign-out ----

    [Fact]
    public async Task Sign_in_needs_the_right_password_and_does_not_say_whether_the_account_exists()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();
        using var other = factory.CreateBrowser();

        using var wrongPassword = await SignInAsync(other, Email, "not-the-password");
        using var unknownAccount = await SignInAsync(other, "nobody@example.test", _password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAccount.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownAccount.Content.ReadAsStringAsync());

        using var signedIn = await SignInAsync(other, " ANA@example.test ", _password);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        using var me = await other.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_on_the_server_not_only_in_the_browser()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        using var signUp = await SignUpAsync(browser, Email, _password);
        var cookie = SessionCookie(signUp);

        using var signOut = await browser.PostAsJsonAsync("/api/auth/sign-out", new { });

        Assert.Equal(HttpStatusCode.OK, signOut.StatusCode);
        Assert.Empty(factory.Data.Sessions);
        // A copy of the old cookie no longer works either.
        using var replay = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        using var me = await replay.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ---- Sessions ----

    [Fact]
    public async Task The_session_cookie_is_http_only_secure_and_holds_only_a_token_whose_hash_is_stored()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await SignUpAsync(browser, Email, _password);

        var cookie = SessionCookie(response);
        Assert.StartsWith("__Host-escribot-session=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);

        var session = Assert.Single(factory.Data.Sessions.Values);
        Assert.Equal(factory.Clock.Now + TimeSpan.FromDays(30), session.ExpiresAt);
    }

    [Fact]
    public async Task A_session_lasts_30_days_and_is_extended_once_a_day_while_in_use()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();
        var start = factory.Clock.Now;

        // Within the first day nothing is written.
        factory.Clock.Now = start + TimeSpan.FromHours(23);
        using (var sameDay = await browser.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, sameDay.StatusCode);
            Assert.False(sameDay.Headers.Contains("Set-Cookie"));
            Assert.Equal(start + TimeSpan.FromDays(30), factory.Data.Sessions.Values.Single().ExpiresAt);
        }

        // After a day the session and its cookie get a full lifetime again.
        factory.Clock.Now = start + TimeSpan.FromDays(2);
        using (var nextDay = await browser.GetAsync("/api/auth/me"))
        {
            Assert.Equal(HttpStatusCode.OK, nextDay.StatusCode);
            Assert.StartsWith("__Host-escribot-session=", SessionCookie(nextDay));
            Assert.Equal(start + TimeSpan.FromDays(32), factory.Data.Sessions.Values.Single().ExpiresAt);
        }

        // Unused for 30 days, it is over.
        factory.Clock.Now = start + TimeSpan.FromDays(32) + TimeSpan.FromSeconds(1);
        using var expired = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Every_request_checks_the_session_against_the_store()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        factory.Data.Sessions.Clear();

        using var me = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ---- Email verification ----

    [Fact]
    public async Task With_email_configured_a_new_account_must_verify_its_address_before_signing_in()
    {
        await using var factory = new ApiFactory { Settings = ApiFactory.EmailConfigured() };
        using var browser = factory.CreateBrowser();

        using var signUp = await SignUpAsync(browser, Email, _password);

        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        Assert.True((await Json(signUp)).GetProperty("verificationRequired").GetBoolean());
        Assert.False(signUp.Headers.Contains("Set-Cookie"));
        Assert.Empty(factory.Data.Sessions);

        using var tooEarly = await SignInAsync(browser, Email, _password);
        Assert.Equal(HttpStatusCode.Forbidden, tooEarly.StatusCode);
        Assert.Equal("EMAIL_NOT_VERIFIED", (await Json(tooEarly)).GetProperty("code").GetString());

        var email = Assert.Single(factory.Emails.Sent);
        Assert.Equal(Email, email.To);
        Assert.Equal("Verify your Escribot email", email.Subject);
        var link = Link(email.Text);
        Assert.StartsWith("https://localhost/api/auth/verify-email?token=", link);

        // Opening the link verifies the address, signs in and goes on to the app.
        using var verified = await browser.GetAsync(link);
        Assert.Equal(HttpStatusCode.Redirect, verified.StatusCode);
        Assert.Equal("/", verified.Headers.Location!.OriginalString);
        Assert.True(factory.Data.Accounts.Values.Single().EmailVerified);
        using var me = await browser.GetAsync("/api/auth/me");
        Assert.True((await Json(me)).GetProperty("user").GetProperty("emailVerified").GetBoolean());

        // Opening it again (a mail scanner may have been first) is not an error, but it signs nobody in.
        using var other = factory.CreateBrowser();
        using var again = await other.GetAsync(link);
        Assert.Equal("/", again.Headers.Location!.OriginalString);
        Assert.False(again.Headers.Contains("Set-Cookie"));
        using var notSignedIn = await other.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, notSignedIn.StatusCode);

        using var madeUp = await other.GetAsync("/api/auth/verify-email?token=not-a-token");
        Assert.Equal("/?error=invalid_token", madeUp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_verification_link_expires_after_an_hour()
    {
        await using var factory = new ApiFactory { Settings = ApiFactory.EmailConfigured() };
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        factory.Clock.Now += TimeSpan.FromMinutes(61);
        using var response = await browser.GetAsync(Link(factory.Emails.Sent.Single().Text));

        Assert.Equal("/?error=invalid_token", response.Headers.Location!.OriginalString);
        Assert.False(factory.Data.Accounts.Values.Single().EmailVerified);
    }

    [Fact]
    public async Task With_email_configured_sign_up_answers_the_same_for_a_taken_address()
    {
        await using var factory = new ApiFactory { Settings = ApiFactory.EmailConfigured() };
        using var browser = factory.CreateBrowser();
        using var first = await SignUpAsync(browser, Email, _password);
        await browser.GetAsync(Link(factory.Emails.Sent.Single().Text));

        using var other = factory.CreateBrowser();
        using var second = await SignUpAsync(other, Email, $"pw-{TestSecrets.NewSecret()}");

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Single(factory.Data.Accounts);
        // The verified owner gets no email, and their password still works.
        Assert.Single(factory.Emails.Sent);
        using var signIn = await SignInAsync(other, Email, _password);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    }

    [Fact]
    public async Task The_verification_email_can_be_requested_again_without_revealing_accounts()
    {
        await using var factory = new ApiFactory { Settings = ApiFactory.EmailConfigured() };
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        using var known = await browser.PostAsJsonAsync("/api/auth/send-verification-email", new { email = Email, callbackURL = "/vault" });
        using var unknown = await browser.PostAsJsonAsync("/api/auth/send-verification-email", new { email = "nobody@example.test" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(2, factory.Emails.Sent.Count);
        using var verified = await browser.GetAsync(Link(factory.Emails.Sent[1].Text));
        Assert.Equal("/vault", verified.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_link_never_sends_the_browser_to_another_site()
    {
        await using var factory = new ApiFactory { Settings = ApiFactory.EmailConfigured() };
        using var browser = factory.CreateBrowser();
        using var signUp = await browser.PostAsJsonAsync("/api/auth/sign-up/email", new { email = Email, password = _password, callbackURL = "https://evil.example.test/" });

        var link = Link(factory.Emails.Sent.Single().Text);
        Assert.DoesNotContain("evil", link);

        using var tampered = await browser.GetAsync(link.Replace("callbackURL=%2F", "callbackURL=%2F%2Fevil.example.test"));
        Assert.Equal("/", tampered.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_failed_delivery_does_not_fail_sign_up()
    {
        await using var factory = new ApiFactory { Settings = ApiFactory.EmailConfigured() };
        factory.Emails.Fail = true;
        using var browser = factory.CreateBrowser();

        using var response = await SignUpAsync(browser, Email, _password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Data.Accounts);
    }

    // ---- Password reset ----

    [Fact]
    public async Task Password_reset_sets_a_new_password_and_signs_out_everywhere()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();
        factory.Emails.Sent.Clear();
        var newPassword = $"new-{TestSecrets.NewSecret()}";

        using var request = await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = Email });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var email = Assert.Single(factory.Emails.Sent);
        Assert.Equal("Reset your Escribot password", email.Subject);
        var link = new Uri(Link(email.Text));
        Assert.Equal("/reset-password", link.AbsolutePath);
        var token = link.Query["?token=".Length..];

        using var other = factory.CreateBrowser();
        using var tooShort = await other.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // The rejected attempt did not use the link up.
        using var reset = await other.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        using var oldSession = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var oldPassword = await SignInAsync(other, Email, _password);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        using var signIn = await SignInAsync(other, Email, newPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // The link works once.
        using var again = await other.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = $"x-{TestSecrets.NewSecret()}" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("INVALID_TOKEN", (await Json(again)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_reset_request_answers_the_same_for_an_unknown_address_and_sends_nothing()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();
        factory.Emails.Sent.Clear();

        using var known = await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = Email });
        using var unknown = await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = "nobody@example.test" });

        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Single(factory.Emails.Sent);
    }

    [Fact]
    public async Task A_reset_link_expires_after_an_hour_and_older_links_die_with_a_reset()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();
        factory.Emails.Sent.Clear();

        (await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = Email })).Dispose();
        var expiredToken = ResetToken(factory.Emails.Sent[0].Text);
        factory.Clock.Now += TimeSpan.FromMinutes(61);
        using var expired = await browser.PostAsJsonAsync("/api/auth/reset-password", new { token = expiredToken, newPassword = $"a-{TestSecrets.NewSecret()}" });
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);

        // The sign-in limits are per hour; start a new one for the next two requests.
        factory.Clock.Now += TimeSpan.FromHours(1);
        (await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = Email })).Dispose();
        (await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = Email })).Dispose();
        using var used = await browser.PostAsJsonAsync("/api/auth/reset-password", new { token = ResetToken(factory.Emails.Sent[1].Text), newPassword = $"b-{TestSecrets.NewSecret()}" });
        Assert.Equal(HttpStatusCode.OK, used.StatusCode);
        using var other = await browser.PostAsJsonAsync("/api/auth/reset-password", new { token = ResetToken(factory.Emails.Sent[2].Text), newPassword = $"c-{TestSecrets.NewSecret()}" });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Fact]
    public async Task In_production_without_email_password_reset_is_disabled_and_no_link_is_written_anywhere()
    {
        await using var factory = new ApiFactory { EnvironmentName = "Production", PublicUrl = "https://app.example.test", UseRealEmailSender = true };
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        using var request = await browser.PostAsJsonAsync("/api/auth/request-password-reset", new { email = Email });
        using var reset = await browser.PostAsJsonAsync("/api/auth/reset-password", new { token = "anything", newPassword = _password });
        using var config = await browser.GetAsync("/api/auth/config");

        Assert.Equal(HttpStatusCode.BadRequest, request.StatusCode);
        Assert.Equal("RESET_PASSWORD_DISABLED", (await Json(request)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.False((await Json(config)).GetProperty("passwordResetAvailable").GetBoolean());
        // Sign-up made no verification link either: there is nowhere safe to deliver it.
        Assert.Empty(factory.Data.Tokens);
    }

    // ---- Deleting the account ----

    [Fact]
    public async Task Deleting_an_account_needs_the_password_and_removes_files_documents_and_sessions()
    {
        await using var factory = new ApiFactory();
        using var ana = factory.CreateBrowser();
        using var ben = factory.CreateBrowser();
        (await SignUpAsync(ana, Email, _password)).Dispose();
        (await SignUpAsync(ben, "ben@example.test", _password)).Dispose();
        var anaId = factory.Data.Accounts.Values.Single(a => a.Email == Email).Id;
        var benId = factory.Data.Accounts.Values.Single(a => a.Email != Email).Id;
        await factory.Data.SaveAsync(anaId, TestData.Image("one", 10));
        await factory.Data.SaveAsync(anaId, TestData.Image("two", 20));
        await factory.Data.SaveAsync(benId, TestData.Image("bens", 30));

        using var wrong = await ana.PostAsJsonAsync("/api/auth/delete-user", new { password = "not-the-password" });
        using var missing = await ana.PostAsJsonAsync("/api/auth/delete-user", new { });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True(factory.Data.Accounts.ContainsKey(anaId));
        Assert.Empty(factory.Files.Deleted);

        using var deleted = await ana.PostAsJsonAsync("/api/auth/delete-user", new { password = _password });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        Assert.Equal(["images/one", "images/two"], factory.Files.Deleted);
        Assert.False(factory.Data.Accounts.ContainsKey(anaId));
        Assert.False(factory.Data.Settings.ContainsKey(anaId));
        Assert.False(factory.Data.Vault.ContainsKey(anaId));
        Assert.DoesNotContain(factory.Data.Sessions.Values, s => s.UserId == anaId);
        using var gone = await ana.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        using var signIn = await SignInAsync(ana, Email, _password);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);

        // The other account is untouched.
        using var benStillThere = await ben.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, benStillThere.StatusCode);
        Assert.Equal(4, factory.Data.Vault[benId].Count);
        Assert.Single(factory.Data.Images[benId]);
    }

    [Fact]
    public async Task A_deletion_that_fails_part_way_can_be_repeated_by_the_user()
    {
        FailsOnce? userData = null;
        await using var factory = new ApiFactory
        {
            ConfigureServices = services =>
            {
                var inner = services.BuildServiceProvider().GetRequiredService<IUserDataRepository>();
                services.RemoveAll<IUserDataRepository>();
                services.AddSingleton<IUserDataRepository>(userData = new FailsOnce(inner));
            },
        };
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        using var failed = await browser.PostAsJsonAsync("/api/auth/delete-user", new { password = _password });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Single(factory.Data.Accounts);

        // Still signed in, and the same request now completes.
        using var me = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var retried = await browser.PostAsJsonAsync("/api/auth/delete-user", new { password = _password });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Empty(factory.Data.Accounts);
        Assert.Equal(2, userData!.Calls);
    }

    private sealed class FailsOnce(IUserDataRepository inner) : IUserDataRepository
    {
        public int Calls { get; private set; }

        public Task<UserDataExport> ExportAsync(string userId, CancellationToken cancellationToken = default) => inner.ExportAsync(userId, cancellationToken);

        public Task ImportAsync(string userId, UserDataImport import, CancellationToken cancellationToken = default) => inner.ImportAsync(userId, import, cancellationToken);

        public Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default) =>
            ++Calls == 1 ? throw new HttpRequestException("The database did not answer.") : inner.DeleteAllAsync(userId, cancellationToken);
    }

    [Fact]
    public async Task Deleting_an_account_requires_a_session()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await browser.PostAsJsonAsync("/api/auth/delete-user", new { password = _password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Allowlist, owner and admins ----

    [Fact]
    public async Task With_an_allowlist_only_listed_addresses_and_the_owner_can_sign_up()
    {
        await using var factory = new ApiFactory
        {
            Settings = new() { ["ALLOWED_SIGNUP_EMAILS"] = " Invited@Example.Test , second@example.test", ["OWNER_EMAIL"] = "owner@example.test" },
        };
        using var browser = factory.CreateBrowser();

        using var stranger = await SignUpAsync(browser, "stranger@example.test", _password);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal("Sign-ups are currently invite-only.", (await Json(stranger)).GetProperty("error").GetString());
        Assert.Empty(factory.Data.Accounts);

        using var invited = await SignUpAsync(factory.CreateBrowser(), "invited@example.test", _password);
        using var owner = await SignUpAsync(factory.CreateBrowser(), "Owner@example.test", _password);
        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task The_owner_and_admin_addresses_count_only_once_they_are_verified()
    {
        await using var factory = new ApiFactory
        {
            Settings = new() { ["OWNER_EMAIL"] = "Owner@example.test", ["ADMIN_EMAILS"] = "admin@example.test, other@example.test" },
        };
        using var owner = factory.CreateBrowser();
        using var admin = factory.CreateBrowser();
        using var user = factory.CreateBrowser();
        (await SignUpAsync(owner, "owner@example.test", _password)).Dispose();
        (await SignUpAsync(admin, "admin@example.test", _password)).Dispose();
        (await SignUpAsync(user, Email, _password)).Dispose();

        // Anyone can type the owner's address into the sign-up form.
        Assert.Equal((false, false), await Roles(owner));
        Assert.Equal((false, false), await Roles(admin));

        // Dev mode printed a verification link for each; open them.
        foreach (var (browser, index) in new[] { (owner, 0), (admin, 1), (user, 2) })
        {
            (await browser.GetAsync(Link(factory.Emails.Sent[index].Text))).Dispose();
        }

        Assert.Equal((true, true), await Roles(owner));
        Assert.Equal((false, true), await Roles(admin));
        Assert.Equal((false, false), await Roles(user));

        static async Task<(bool IsOwner, bool IsAdmin)> Roles(HttpClient browser)
        {
            using var me = await browser.GetAsync("/api/auth/me");
            var user = (await Json(me)).GetProperty("user");
            return (user.GetProperty("isOwner").GetBoolean(), user.GetProperty("isAdmin").GetBoolean());
        }
    }

    // ---- Rate limits ----

    [Theory]
    [InlineData("/api/auth/sign-in/email", 5, 60)]
    [InlineData("/api/auth/sign-up/email", 10, 3600)]
    [InlineData("/api/auth/request-password-reset", 3, 3600)]
    [InlineData("/api/auth/reset-password", 5, 600)]
    public async Task Sign_in_endpoints_are_limited_per_address(string path, int limit, int windowSeconds)
    {
        await using var factory = new ApiFactory();
        // Start at the beginning of a window so the whole test falls inside one.
        factory.Clock.Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 - (1_800_000_000 % windowSeconds));
        using var browser = factory.CreateBrowser();
        object body = new { email = "nobody@example.test", password = "wrong-password", token = "none", newPassword = "long-enough-password" };

        for (var i = 0; i < limit; i++)
        {
            using var allowed = await browser.PostAsJsonAsync(path, body);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var limited = await browser.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("RATE_LIMITED", (await Json(limited)).GetProperty("code").GetString());
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, windowSeconds);

        // Another address is not affected, and the limit lifts when the window ends.
        Assert.All(factory.Data.RateCounts.Keys, key => Assert.Contains(":unknown:", key));
        factory.Clock.Now += TimeSpan.FromSeconds(windowSeconds);
        using var later = await browser.PostAsJsonAsync(path, body);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, later.StatusCode);
    }

    [Fact]
    public async Task A_limited_sign_in_is_refused_even_with_the_right_password()
    {
        await using var factory = new ApiFactory();
        factory.Clock.Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 - (1_800_000_000 % 3600));
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();
        using var attacker = factory.CreateBrowser();
        for (var i = 0; i < 5; i++)
        {
            (await SignInAsync(attacker, Email, $"guess-{i}-long-enough")).Dispose();
        }

        using var response = await SignInAsync(attacker, Email, _password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // ---- Every /api route ----

    [Theory]
    [InlineData("GET", "/api/settings")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("POST", "/api/sessions")]
    [InlineData("GET", "/api/account/export")]
    [InlineData("POST", "/api/ai/evaluate")]
    [InlineData("POST", "/api/billing/checkout")]
    [InlineData("GET", "/api/a-route-added-later")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("GET", "/API/Settings")]
    public async Task Signed_out_api_calls_get_401_with_the_same_json_as_before(string method, string path)
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"Unauthorized — please sign in."}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/stripe/webhook")]
    [InlineData("/api/errors")]
    [InlineData("/api/auth/config")]
    public async Task Sign_in_the_stripe_webhook_and_error_reports_need_no_session(string path)
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await browser.PostAsJsonAsync(path, new { });

        // The first two have no endpoint on this server yet; what matters is that they are not turned away.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_path_that_only_starts_like_a_public_one_still_needs_a_session()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();

        using var response = await browser.GetAsync("/api/authors");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_request_passes_the_guard()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        using var response = await browser.GetAsync("/api/a-route-added-later");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_page_on_another_site_cannot_make_changes_with_a_visitors_session()
    {
        await using var factory = new ApiFactory { PublicUrl = "https://app.example.test" };
        using var browser = factory.CreateBrowser();
        (await SignUpAsync(browser, Email, _password)).Dispose();

        using var crossSite = await SendWithOrigin("https://evil.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Single(factory.Data.Sessions);

        using var ownHost = await SendWithOrigin("https://localhost");
        using var publicUrl = await SendWithOrigin("https://app.example.test");
        Assert.Equal(HttpStatusCode.OK, ownHost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publicUrl.StatusCode);

        Task<HttpResponseMessage> SendWithOrigin(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("Origin", origin);
            return browser.SendAsync(request);
        }
    }

    // ---- Helpers ----

    internal static Task<HttpResponseMessage> SignUpAsync(HttpClient browser, string email, string? password, string? name = null) =>
        browser.PostAsJsonAsync("/api/auth/sign-up/email", new { name, email, password });

    internal static Task<HttpResponseMessage> SignInAsync(HttpClient browser, string email, string password) =>
        browser.PostAsJsonAsync("/api/auth/sign-in/email", new { email, password });

    internal static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    internal static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.Contains("escribot-session="));

    internal static string Link(string emailText) => LinkPattern().Match(emailText).Value;

    private static string ResetToken(string emailText) => new Uri(Link(emailText)).Query["?token=".Length..];

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex LinkPattern();
}

public class ApiErrorHandlingTests
{
    [Fact]
    public async Task An_unexpected_failure_is_recorded_and_answered_without_details()
    {
        const string internalDetail = "account-name.documents.example.test refused the connection";
        await using var factory = new ApiFactory
        {
            ConfigureServices = services =>
            {
                services.RemoveAll<IVaultRepository>();
                services.AddSingleton<IVaultRepository>(new FailingVault(internalDetail));
            },
        };
        using var browser = factory.CreateBrowser();
        (await AuthEndpointTests.SignUpAsync(browser, "ana@example.test", $"pw-{TestSecrets.NewSecret()}")).Dispose();

        using var response = await browser.GetAsync("/api/vault?secret=query-value");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"Failed to load Vault."}""", await response.Content.ReadAsStringAsync());
        var recorded = Assert.Single(factory.Data.Errors, e => e.Path == "GET /api/vault");
        Assert.Equal("server", recorded.Source);
        Assert.Equal(internalDetail, recorded.Message);
        Assert.Equal(factory.Data.Accounts.Values.Single().Id, recorded.UserId);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_400_and_not_an_error_report()
    {
        await using var factory = new ApiFactory();
        using var browser = factory.CreateBrowser();
        (await AuthEndpointTests.SignUpAsync(browser, "ana@example.test", $"pw-{TestSecrets.NewSecret()}")).Dispose();

        using var response = await browser.PostAsync("/api/auth/sign-in/email", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"The request could not be read."}""", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Data.Errors);
    }

    private sealed class FailingVault(string message) : IVaultRepository
    {
        // Provisioning a new account saves the starter items; only reading fails.
        public Task SaveManyAsync(string userId, IReadOnlyList<VaultItem> items, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<VaultItem>> ListAsync(string userId, CancellationToken cancellationToken = default) => throw new HttpRequestException(message);

        public Task<VaultItem?> GetAsync(string userId, string itemId, CancellationToken cancellationToken = default) => throw new HttpRequestException(message);

        public Task SaveAsync(string userId, VaultItem item, CancellationToken cancellationToken = default) => throw new HttpRequestException(message);

        public Task<bool> DeleteAsync(string userId, string itemId, CancellationToken cancellationToken = default) => throw new HttpRequestException(message);
    }
}
