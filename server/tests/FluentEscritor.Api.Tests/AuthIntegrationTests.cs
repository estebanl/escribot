using Aspire.Hosting.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Cosmos;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Tests;

/// <summary>Sign-in against the API the AppHost starts, with the Cosmos DB emulator behind it.</summary>
[Trait("Category", "Integration")]
[Collection(AppHostCollection.Name)]
public class AuthIntegrationTests(AppHostFixture fixture)
{
    private readonly string _userId = AppHostFixture.NewUserId();

    [Fact]
    public async Task An_account_can_sign_up_sign_out_sign_in_and_delete_itself()
    {
        // The auth steps of scripts/test-isolation.mjs, on the same paths.
        var email = $"auth-{Guid.NewGuid():N}@example.test";
        var password = $"pw-{TestSecrets.NewSecret()}";
        using var handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false };
        using var browser = new HttpClient(handler) { BaseAddress = fixture.App.GetEndpoint("api", "http") };
        using var anonymous = fixture.App.CreateHttpClient("api", "http");

        using var signUp = await browser.PostAsJsonAsync("/api/auth/sign-up/email", new { name = "Isolation A", email, password });
        Assert.Equal(HttpStatusCode.OK, signUp.StatusCode);
        Assert.Contains(signUp.Headers.GetValues("Set-Cookie"), c => c.StartsWith("fluentescritor-session=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var userId = (await signUp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("id").GetString()!;

        // The account is stored with a hash, and has its starting data.
        var account = await fixture.Get<IUserAccountRepository>().FindByEmailAsync(email);
        Assert.Equal(userId, account!.Id);
        Assert.NotNull(account.PasswordHash);
        Assert.DoesNotContain(password, account.PasswordHash);
        Assert.NotNull(account.SecurityStamp);
        Assert.Equal(4, (await fixture.Get<IVaultRepository>().ListAsync(userId)).Count);

        using var duplicate = await anonymous.PostAsJsonAsync("/api/auth/sign-up/email", new { email = email.ToUpperInvariant(), password });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        using var me = await browser.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var signOut = await browser.PostAsJsonAsync("/api/auth/sign-out", new { });
        Assert.Equal(HttpStatusCode.OK, signOut.StatusCode);
        using var signedOut = await browser.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        Assert.Equal("""{"error":"Unauthorized — please sign in."}""", await signedOut.Content.ReadAsStringAsync());

        using var wrong = await browser.PostAsJsonAsync("/api/auth/sign-in/email", new { email, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var signIn = await browser.PostAsJsonAsync("/api/auth/sign-in/email", new { email, password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        await fixture.Get<IImageRepository>().SaveAsync(userId, TestData.Image("auth-image", 12));
        using var delete = await browser.PostAsJsonAsync("/api/auth/delete-user", new { password });
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        using var afterDelete = await browser.GetAsync("/api/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);
        Assert.Null(await fixture.Get<IUserAccountRepository>().GetAsync(userId));
        Assert.Null(await fixture.Get<IUserAccountRepository>().FindByEmailAsync(email));
        Assert.Empty(await fixture.Get<IVaultRepository>().ListAsync(userId));
        Assert.Empty(await fixture.Get<UserDocuments>().QueryAsync<string>(userId, "SELECT VALUE c.id FROM c", default));
    }

    [Fact]
    public async Task Deleting_a_user_removes_the_account_its_lookups_and_sessions_and_can_be_run_again()
    {
        var email = $"{_userId}@example.test";
        var token = TestSecrets.NewSecret();
        await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(_userId, email));
        await fixture.Get<IAuthSessionStore>().CreateAsync(token, _userId, DateTimeOffset.UtcNow.AddDays(1));
        await fixture.Get<IVaultRepository>().SaveAsync(_userId, TestData.VaultItem("kept"));

        await fixture.Get<IUserDataRepository>().DeleteAllAsync(_userId);

        Assert.Null(await fixture.Get<IUserAccountRepository>().GetAsync(_userId));
        Assert.Null(await fixture.Get<IUserAccountRepository>().FindByEmailAsync(email));
        Assert.Null(await fixture.Get<IAuthSessionStore>().FindAsync(token));
        Assert.Empty(await fixture.Get<UserDocuments>().QueryAsync<string>(_userId, "SELECT VALUE c.id FROM c", default));
        // Running it again on what is now nothing is harmless.
        await fixture.Get<IUserDataRepository>().DeleteAllAsync(_userId);
    }

    [Fact]
    public async Task An_emailed_token_is_found_by_its_purpose_and_redeemed_once()
    {
        var tokens = fixture.Get<IOneTimeTokenStore>();
        var token = TestSecrets.NewSecret();
        await tokens.CreateAsync(OneTimeTokenPurpose.PasswordReset, token, _userId, DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal(_userId, await tokens.FindUserIdAsync(OneTimeTokenPurpose.PasswordReset, token));
        // A reset link is not a verification link.
        Assert.Null(await tokens.FindUserIdAsync(OneTimeTokenPurpose.EmailVerification, token));
        Assert.Null(await tokens.RedeemAsync(OneTimeTokenPurpose.EmailVerification, token));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => tokens.RedeemAsync(OneTimeTokenPurpose.PasswordReset, token)));

        Assert.Single(results, r => r == _userId);
        Assert.Equal(5, results.Count(r => r is null));
        Assert.Null(await tokens.FindUserIdAsync(OneTimeTokenPurpose.PasswordReset, token));
    }

    [Fact]
    public async Task An_emailed_token_expires_and_is_not_stored_in_the_clear()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var tokens = new CosmosOneTimeTokenStore(new LookupStore(fixture.Get<CosmosStore>(), clock));
        var token = TestSecrets.NewSecret();
        await tokens.CreateAsync(OneTimeTokenPurpose.EmailVerification, token, _userId, clock.Now.AddHours(1));

        var stored = await CosmosStore.QueryAsync<JsonElement>(
            fixture.Get<CosmosStore>().Lookups,
            null,
            UserDocuments.Query("SELECT * FROM c WHERE c.userId = @userId", ("@userId", _userId)),
            default);
        Assert.Single(stored);
        Assert.DoesNotContain(token, stored[0].GetRawText());
        Assert.True(stored[0].GetProperty("ttl").GetInt32() is > 3500 and <= 3601);

        clock.Now += TimeSpan.FromMinutes(61);
        Assert.Null(await tokens.FindUserIdAsync(OneTimeTokenPurpose.EmailVerification, token));
        Assert.Null(await tokens.RedeemAsync(OneTimeTokenPurpose.EmailVerification, token));
    }

    [Fact]
    public async Task Tokens_of_one_purpose_can_be_removed_and_all_go_with_the_user()
    {
        var tokens = fixture.Get<IOneTimeTokenStore>();
        var (reset, verify, other) = (TestSecrets.NewSecret(), TestSecrets.NewSecret(), TestSecrets.NewSecret());
        var otherUser = AppHostFixture.NewUserId();
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        await tokens.CreateAsync(OneTimeTokenPurpose.PasswordReset, reset, _userId, expires);
        await tokens.CreateAsync(OneTimeTokenPurpose.EmailVerification, verify, _userId, expires);
        await tokens.CreateAsync(OneTimeTokenPurpose.PasswordReset, other, otherUser, expires);

        await tokens.RemoveAllForUserAsync(OneTimeTokenPurpose.PasswordReset, _userId);
        Assert.Null(await tokens.FindUserIdAsync(OneTimeTokenPurpose.PasswordReset, reset));
        Assert.Equal(_userId, await tokens.FindUserIdAsync(OneTimeTokenPurpose.EmailVerification, verify));

        await fixture.Get<IUserDataRepository>().DeleteAllAsync(_userId);
        Assert.Null(await tokens.FindUserIdAsync(OneTimeTokenPurpose.EmailVerification, verify));
        Assert.Equal(otherUser, await tokens.FindUserIdAsync(OneTimeTokenPurpose.PasswordReset, other));
    }
}
