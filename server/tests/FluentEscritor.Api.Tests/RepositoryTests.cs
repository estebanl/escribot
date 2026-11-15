using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Cosmos;
using FluentEscritor.Api.Data.Models;
using Microsoft.Azure.Cosmos;

namespace FluentEscritor.Api.Tests;

[Trait("Category", "Integration")]
[Collection(AppHostCollection.Name)]
public class RepositoryTests(AppHostFixture fixture)
{
    private readonly string _userId = AppHostFixture.NewUserId();

    // ---- Accounts and lookups ----

    [Fact]
    public async Task Account_is_found_by_id_and_by_email_in_any_letter_case()
    {
        var accounts = fixture.Get<IUserAccountRepository>();
        await accounts.CreateAsync(TestData.Account(_userId, $"Mixed.{_userId}@Example.Test"));

        var byId = await accounts.GetAsync(_userId);
        var byEmail = await accounts.FindByEmailAsync($"  MIXED.{_userId}@example.test ");

        Assert.Equal(_userId, byId!.Id);
        Assert.Equal($"Mixed.{_userId}@Example.Test", byId.Email);
        Assert.Equal(_userId, byEmail!.Id);
        Assert.NotEqual(default, byId.CreatedAt);
        Assert.Null(await accounts.FindByEmailAsync($"nobody-{_userId}@example.test"));
    }

    [Fact]
    public async Task Only_one_account_can_hold_an_email_even_when_created_at_the_same_time()
    {
        var accounts = fixture.Get<IUserAccountRepository>();
        var email = $"shared-{_userId}@example.test";

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => accounts.CreateAsync(TestData.Account($"{_userId}-{i}", i % 2 == 0 ? email : email.ToUpperInvariant()))));

        Assert.Single(results, r => r == CreateAccountResult.Created);
        Assert.Equal(7, results.Count(r => r == CreateAccountResult.EmailTaken));
    }

    [Fact]
    public async Task An_email_lookup_left_by_a_failed_sign_up_does_not_block_the_address()
    {
        var email = $"orphan-{_userId}@example.test";
        await fixture.Get<LookupStore>().TryCreateAsync(LookupStore.Email, email, "user-that-was-never-written", null, default);

        var result = await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(_userId, email));

        Assert.Equal(CreateAccountResult.Created, result);
        Assert.Equal(_userId, (await fixture.Get<IUserAccountRepository>().FindByEmailAsync(email))!.Id);
    }

    [Fact]
    public async Task Account_update_keeps_identity_fields_and_refuses_an_email_change()
    {
        var accounts = fixture.Get<IUserAccountRepository>();
        await accounts.CreateAsync(TestData.Account(_userId));

        var updated = await accounts.UpdateAsync(_userId, a => a with { EmailVerified = true, Name = "Renamed", Id = "someone-else" });

        Assert.True(updated!.EmailVerified);
        Assert.Equal(_userId, (await accounts.GetAsync(_userId))!.Id);
        Assert.Equal("Renamed", (await accounts.GetAsync(_userId))!.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.UpdateAsync(_userId, a => a with { Email = "other@example.test" }));
        Assert.Null(await accounts.UpdateAsync(AppHostFixture.NewUserId(), a => a with { Name = "x" }));
    }

    [Fact]
    public async Task Auth_session_is_found_by_token_until_it_expires_or_is_removed()
    {
        var sessions = fixture.Get<IAuthSessionStore>();
        var token = TestSecrets.NewSecret();
        var expired = TestSecrets.NewSecret();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        await sessions.CreateAsync(token, _userId, expiresAt);
        await sessions.CreateAsync(expired, _userId, DateTimeOffset.UtcNow.AddSeconds(-5));

        var found = await sessions.FindAsync(token);
        Assert.Equal(_userId, found!.UserId);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), found.ExpiresAt.ToUnixTimeSeconds());
        Assert.Null(await sessions.FindAsync(expired));
        Assert.Null(await sessions.FindAsync(TestSecrets.NewSecret()));
        Assert.False(await sessions.ExtendAsync(expired, expiresAt));

        Assert.True(await sessions.ExtendAsync(token, expiresAt.AddDays(1)));
        Assert.Equal(expiresAt.AddDays(1).ToUnixTimeSeconds(), (await sessions.FindAsync(token))!.ExpiresAt.ToUnixTimeSeconds());

        await sessions.RemoveAsync(token);
        Assert.Null(await sessions.FindAsync(token));
    }

    [Fact]
    public async Task Auth_session_token_is_not_stored_and_all_of_a_users_sessions_can_be_removed()
    {
        var sessions = fixture.Get<IAuthSessionStore>();
        var tokens = Enumerable.Range(0, 3).Select(_ => TestSecrets.NewSecret()).ToList();
        foreach (var token in tokens)
        {
            await sessions.CreateAsync(token, _userId, DateTimeOffset.UtcNow.AddDays(1));
        }

        var stored = await CosmosStore.QueryAsync<System.Text.Json.Nodes.JsonObject>(
            fixture.Get<CosmosStore>().Lookups,
            null,
            new QueryDefinition("SELECT * FROM c WHERE c.userId = @u").WithParameter("@u", _userId),
            default);
        Assert.Equal(3, stored.Count);
        Assert.All(stored, d => Assert.All(tokens, t => Assert.DoesNotContain(t, d.ToJsonString())));
        Assert.All(stored, d => Assert.InRange(d["ttl"]!.GetValue<int>(), 86_000, 86_402));

        await sessions.RemoveAllForUserAsync(_userId);

        Assert.All(await Task.WhenAll(tokens.Select(t => sessions.FindAsync(t))), Assert.Null);
    }

    // ---- Settings and subscription ----

    [Fact]
    public async Task Settings_are_created_once_with_defaults()
    {
        var settings = fixture.Get<ISettingsRepository>();

        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => settings.GetOrCreateAsync(_userId)));

        Assert.All(all, s =>
        {
            Assert.Equal(_userId, s.UserId);
            Assert.Equal(6.5, s.CurrentBand);
            Assert.Equal(7.5, s.TargetBand);
            Assert.Equal("es", s.NativeLanguage);
            Assert.True(s.L1CoachingEnabled);
            Assert.Equal("free", s.Plan);
            Assert.Null(s.OnboardedAt);
            Assert.Empty(s.EncryptedApiKeys);
        });
        Assert.Single(all.Select(s => s.UpdatedAt).Distinct());
    }

    [Fact]
    public async Task Concurrent_settings_updates_do_not_lose_each_others_changes()
    {
        var settings = fixture.Get<ISettingsRepository>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => settings.UpdateAsync(_userId, s => s with
        {
            EncryptedApiKeys = new Dictionary<string, string>(s.EncryptedApiKeys) { [$"provider{i}"] = $"ciphertext{i}" },
        })));

        Assert.Equal(8, (await settings.GetOrCreateAsync(_userId)).EncryptedApiKeys.Count);
    }

    [Fact]
    public async Task Subscription_is_found_by_stripe_customer_and_a_user_gets_one_customer()
    {
        var subscriptions = fixture.Get<ISubscriptionRepository>();
        var customerId = AppHostFixture.NewId("cus");

        var created = await subscriptions.CreateAsync(_userId, customerId);
        var again = await subscriptions.CreateAsync(_userId, AppHostFixture.NewId("cus"));

        Assert.Equal(customerId, created.StripeCustomerId);
        Assert.Equal(customerId, again.StripeCustomerId);
        Assert.Equal(_userId, (await subscriptions.FindByStripeCustomerIdAsync(customerId))!.UserId);
        Assert.Null(await subscriptions.FindByStripeCustomerIdAsync(AppHostFixture.NewId("cus")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => subscriptions.CreateAsync(AppHostFixture.NewUserId(), customerId));
    }

    [Fact]
    public async Task Subscription_update_applies_new_events_and_skips_stale_ones()
    {
        var subscriptions = fixture.Get<ISubscriptionRepository>();
        var customerId = AppHostFixture.NewId("cus");
        await subscriptions.CreateAsync(_userId, customerId);

        Subscription Apply(Subscription s, long eventAt, string status) =>
            eventAt <= s.LastEventAt ? s : s with { Status = status, LastEventAt = eventAt, StripeCustomerId = "cus_forged" };

        await subscriptions.UpdateAsync(_userId, s => Apply(s, 200, "active"));
        var afterStale = await subscriptions.UpdateAsync(_userId, s => Apply(s, 100, "canceled"));

        Assert.Equal("active", afterStale!.Status);
        var stored = await subscriptions.GetAsync(_userId);
        Assert.Equal("active", stored!.Status);
        Assert.Equal(200, stored.LastEventAt);
        Assert.Equal(customerId, stored.StripeCustomerId);
    }

    // ---- Prompts ----

    [Fact]
    public async Task Visible_prompts_are_the_curated_bank_plus_the_users_own()
    {
        var prompts = fixture.Get<IPromptRepository>();
        var seeds = CuratedPromptSeed.LoadEmbedded();
        await fixture.Get<CuratedPromptSynchronizer>().SynchronizeAsync(seeds);
        var ownId = AppHostFixture.NewId("custom");
        await prompts.SaveAsync(_userId, TestData.Prompt(ownId) with { Source = "custom" });

        var visible = await prompts.ListVisibleAsync(_userId);

        Assert.All(seeds, s => Assert.Contains(visible, p => p.Id == s.Prompt.Id && p.UserId is null && p.Source == "curated"));
        var own = Assert.Single(visible, p => p.UserId is not null);
        Assert.Equal(ownId, own.Id);
        Assert.Equal(_userId, own.UserId);
        Assert.Equal("bar", own.ChartSpec!.Value.GetProperty("kind").GetString());

        var curated = await prompts.GetVisibleAsync(_userId, seeds[0].Prompt.Id);
        Assert.Equal(seeds[0].Prompt.Title, curated!.Title);
        Assert.Equal(
            seeds[0].Prompt.ChartSpec!.Value.GetProperty("data").GetArrayLength(),
            curated.ChartSpec!.Value.GetProperty("data").GetArrayLength());

        Assert.True(await prompts.DeleteAsync(_userId, ownId));
        Assert.False(await prompts.DeleteAsync(_userId, seeds[0].Prompt.Id));
        Assert.NotNull(await prompts.GetVisibleAsync(_userId, seeds[0].Prompt.Id));
    }

    [Fact]
    public async Task Users_cannot_write_curated_prompts()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Get<IPromptRepository>().SaveAsync(_userId, TestData.Prompt("p1") with { Source = "curated" }));
    }

    [Fact]
    public async Task Seeding_is_idempotent_and_removes_prompts_that_are_no_longer_shipped()
    {
        var store = fixture.Get<ICuratedPromptStore>();
        var synchronizer = fixture.Get<CuratedPromptSynchronizer>();
        var seeds = CuratedPromptSeed.LoadEmbedded();
        var retiredId = AppHostFixture.NewId("retired");

        await synchronizer.SynchronizeAsync(seeds);
        Assert.Equal(0, await synchronizer.SynchronizeAsync(seeds));

        // A prompt from an earlier release, and one whose text changed since it was stored.
        await store.UpsertAsync(TestData.Prompt(retiredId), "old-hash");
        await store.UpsertAsync(seeds[0].Prompt with { Title = "Outdated title" }, "old-hash");

        Assert.Equal(2, await synchronizer.SynchronizeAsync(seeds));
        Assert.Equal(0, await synchronizer.SynchronizeAsync(seeds));

        var hashes = await store.GetSeedHashesAsync();
        Assert.Equal(seeds.Select(s => s.Prompt.Id).Order(), hashes.Keys.Order());
        Assert.All(seeds, s => Assert.Equal(s.Hash, hashes[s.Prompt.Id]));
        Assert.Equal(seeds[0].Prompt.Title, (await fixture.Get<IPromptRepository>().GetVisibleAsync(_userId, seeds[0].Prompt.Id))!.Title);
        Assert.Null(await fixture.Get<IPromptRepository>().GetVisibleAsync(_userId, retiredId));
    }

    // ---- Writing sessions ----

    [Fact]
    public async Task Session_bundle_returns_the_session_with_its_drafts_and_evaluations_only()
    {
        var writing = fixture.Get<IWritingRepository>();
        await writing.SaveSessionAsync(_userId, TestData.Session("s1", minutes: 1));
        await writing.SaveSessionAsync(_userId, TestData.Session("s2", minutes: 2));
        await writing.SaveDraftAsync(_userId, TestData.Draft("d2", "s1", minutes: 20));
        await writing.SaveDraftAsync(_userId, TestData.Draft("d1", "s1", minutes: 10));
        await writing.SaveDraftAsync(_userId, TestData.Draft("d3", "s2", minutes: 30));
        await writing.SaveEvaluationAsync(_userId, TestData.Evaluation("e1", "s1", "d1", minutes: 11));
        await writing.SaveEvaluationAsync(_userId, TestData.Evaluation("e3", "s2", "d3", minutes: 31));

        var bundle = await writing.GetSessionBundleAsync(_userId, "s1");

        Assert.Equal("s1", bundle!.Session.Id);
        Assert.Equal(_userId, bundle.Session.UserId);
        Assert.Equal(["d1", "d2"], bundle.Drafts.Select(d => d.Id));
        Assert.Equal(["e1"], bundle.Evaluations.Select(e => e.Id));
        Assert.Equal("synthetic", bundle.Drafts[0].Outline!.Value.GetProperty("thesis").GetString());
        Assert.Equal(TestData.Start.AddMinutes(10), bundle.Drafts[0].CreatedAt);
        Assert.Equal(["s2", "s1"], (await writing.ListSessionsAsync(_userId)).Select(s => s.Id));
        Assert.Equal(["d3", "d2", "d1"], (await writing.ListDraftsAsync(_userId)).Select(d => d.Id));
        Assert.Null(await writing.GetSessionBundleAsync(_userId, "missing"));
    }

    [Fact]
    public async Task Deleting_a_session_removes_its_drafts_and_evaluations_and_nothing_else()
    {
        var writing = fixture.Get<IWritingRepository>();
        await writing.SaveSessionAsync(_userId, TestData.Session("s1"));
        await writing.SaveSessionAsync(_userId, TestData.Session("s2"));
        await writing.SaveDraftAsync(_userId, TestData.Draft("d1", "s1"));
        await writing.SaveDraftAsync(_userId, TestData.Draft("d2", "s2"));
        await writing.SaveEvaluationAsync(_userId, TestData.Evaluation("e1", "s1", "d1"));
        // A document of another type that happens to share the session's id.
        await fixture.Get<IVaultRepository>().SaveAsync(_userId, TestData.VaultItem("s1"));

        Assert.True(await writing.DeleteSessionAsync(_userId, "s1"));
        Assert.False(await writing.DeleteSessionAsync(_userId, "s1"));

        Assert.Null(await writing.GetOwnedSessionAsync(_userId, "s1"));
        Assert.Null(await writing.GetDraftAsync(_userId, "d1"));
        Assert.Empty(await writing.ListEvaluationsAsync(_userId));
        Assert.NotNull(await writing.GetDraftAsync(_userId, "d2"));
        Assert.NotNull(await fixture.Get<IVaultRepository>().GetAsync(_userId, "s1"));
    }

    // ---- Vault, drills, usage, images ----

    [Fact]
    public async Task Vault_items_save_together_list_newest_first_and_update_in_place()
    {
        var vault = fixture.Get<IVaultRepository>();
        await vault.SaveManyAsync(_userId, [TestData.VaultItem("v1", 1), TestData.VaultItem("v2", 2), TestData.VaultItem("v3", 3)]);

        await vault.SaveAsync(_userId, (await vault.GetAsync(_userId, "v2"))! with { MasteryLevel = 2, ReviewCount = 1 });
        Assert.True(await vault.DeleteAsync(_userId, "v1"));

        var items = await vault.ListAsync(_userId);
        Assert.Equal(["v3", "v2"], items.Select(v => v.Id));
        Assert.Equal(2, items[1].MasteryLevel);
    }

    [Fact]
    public async Task A_rejected_batch_saves_nothing()
    {
        var vault = fixture.Get<IVaultRepository>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            vault.SaveManyAsync(_userId, [TestData.VaultItem("ok"), TestData.VaultItem("not/ok")]));

        Assert.Empty(await vault.ListAsync(_userId));
    }

    [Fact]
    public async Task Drills_round_trip_with_their_json_payloads()
    {
        var drills = fixture.Get<IDrillRepository>();
        await drills.SaveAsync(_userId, TestData.Drill("d1"));
        await drills.SaveAsync(_userId, (await drills.GetAsync(_userId, "d1"))! with
        {
            UserResponse = "synthetic answer",
            Score = TestData.Json("""{"band":7}"""),
            CompletedAt = TestData.Start.AddMinutes(5),
        });

        var drill = Assert.Single(await drills.ListAsync(_userId));
        Assert.Equal("synthetic", drill.PromptPayload!.Value.GetProperty("sentence").GetString());
        Assert.Equal(7, drill.Score!.Value.GetProperty("band").GetInt32());
        Assert.Equal(TestData.Start.AddMinutes(5), drill.CompletedAt);
    }

    [Fact]
    public async Task Ai_usage_is_counted_by_feature_key_source_and_time()
    {
        var usage = fixture.Get<IAiUsageRepository>();
        await usage.RecordAsync(_userId, TestData.Usage("u1", "rubric_score", minutes: 0));
        await usage.RecordAsync(_userId, TestData.Usage("u2", "rubric_score", minutes: 60));
        await usage.RecordAsync(_userId, TestData.Usage("u3", "toolkit", minutes: 60));
        await usage.RecordAsync(_userId, TestData.Usage("u4", "rubric_score", keySource: "user", minutes: 60));

        Assert.Equal(3, await usage.CountAsync(_userId, TestData.Start, ["rubric_score"]));
        Assert.Equal(2, await usage.CountAsync(_userId, TestData.Start, ["rubric_score"], "platform"));
        Assert.Equal(1, await usage.CountAsync(_userId, TestData.Start.AddMinutes(30), ["rubric_score"], "platform"));
        Assert.Equal(3, await usage.CountAsync(_userId, TestData.Start, ["rubric_score", "toolkit"], "platform"));
        Assert.Equal(0, await usage.CountAsync(_userId, TestData.Start, ["outline_check"]));
        Assert.Equal(["u2", "u3", "u4"], (await usage.ListSinceAsync(_userId, TestData.Start.AddMinutes(30))).Select(u => u.Id).Order());
    }

    [Fact]
    public async Task Image_bytes_are_totalled_per_user()
    {
        var images = fixture.Get<IImageRepository>();
        Assert.Equal(0, await images.GetTotalBytesAsync(_userId));

        await images.SaveAsync(_userId, TestData.Image("i1", 1_500_000));
        await images.SaveAsync(_userId, TestData.Image("i2", 700_000));

        Assert.Equal(2_200_000, await images.GetTotalBytesAsync(_userId));
        Assert.True(await images.DeleteAsync(_userId, "i1"));
        Assert.Equal(700_000, await images.GetTotalBytesAsync(_userId));
        Assert.Equal("images/i2", (await images.GetAsync(_userId, "i2"))!.BlobName);
    }

    // ---- Export and deletion ----

    [Fact]
    public async Task Export_returns_everything_the_user_owns_without_credentials()
    {
        await SeedFullUserAsync(_userId);

        var export = await fixture.Get<IUserDataRepository>().ExportAsync(_userId);

        Assert.Equal(_userId, export.Account!.Id);
        Assert.Null(export.Account.PasswordHash);
        Assert.Null(export.Account.SecurityStamp);
        Assert.Empty(export.Settings!.EncryptedApiKeys);
        Assert.Equal(["anthropic", "openai"], export.SavedApiKeyProviders);
        Assert.NotNull(export.Subscription);
        Assert.Single(export.Prompts);
        Assert.Single(export.Sessions);
        Assert.Single(export.Drafts);
        Assert.Single(export.Evaluations);
        Assert.Equal(2, export.VaultItems.Count);
        Assert.Single(export.MicroDrills);
        Assert.Single(export.AiUsage);
        Assert.Single(export.Images);
        Assert.DoesNotContain("ciphertext", System.Text.Json.JsonSerializer.Serialize(export));
        Assert.DoesNotContain("synthetic-password-hash", System.Text.Json.JsonSerializer.Serialize(export));
    }

    [Fact]
    public async Task Delete_removes_every_document_and_lookup_and_frees_the_email()
    {
        var (email, customerId, token) = await SeedFullUserAsync(_userId);
        // More documents than fit in one batch.
        foreach (var chunk in Enumerable.Range(0, 130).Select(i => TestData.VaultItem($"bulk-{i}")).Chunk(100))
        {
            await fixture.Get<IVaultRepository>().SaveManyAsync(_userId, chunk);
        }

        var marker = $"marker {Guid.NewGuid():N}"[..20];
        await fixture.Get<IErrorEventRepository>().RecordAsync(new ErrorEvent { Source = "client", Message = marker, UserId = _userId });

        var data = fixture.Get<IUserDataRepository>();
        await data.DeleteAllAsync(_userId);
        await data.DeleteAllAsync(_userId);

        var remaining = await CosmosStore.QueryAsync<int>(
            fixture.Get<CosmosStore>().Users,
            null,
            new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.userId = @u").WithParameter("@u", _userId),
            default);
        Assert.Equal(0, remaining.Sum());

        Assert.Null(await fixture.Get<IUserAccountRepository>().GetAsync(_userId));
        Assert.Null(await fixture.Get<IUserAccountRepository>().FindByEmailAsync(email));
        Assert.Null(await fixture.Get<IAuthSessionStore>().FindAsync(token));
        Assert.Null(await fixture.Get<ISubscriptionRepository>().FindByStripeCustomerIdAsync(customerId));
        Assert.Null((await fixture.Get<IUserDataRepository>().ExportAsync(_userId)).Account);

        // The error report is kept, but no longer names the person.
        var report = Assert.Single(await fixture.Get<IErrorEventRepository>().ListRecentAsync(500), e => e.Message == marker);
        Assert.Null(report.UserId);

        Assert.Equal(CreateAccountResult.Created, await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(AppHostFixture.NewUserId(), email)));
    }

    private async Task<(string Email, string CustomerId, string Token)> SeedFullUserAsync(string userId)
    {
        var email = $"{userId}@example.test";
        var customerId = AppHostFixture.NewId("cus");
        var token = TestSecrets.NewSecret();

        await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(userId, email));
        await fixture.Get<IAuthSessionStore>().CreateAsync(token, userId, DateTimeOffset.UtcNow.AddDays(1));
        await fixture.Get<ISettingsRepository>().UpdateAsync(userId, s => s with
        {
            EncryptedApiKeys = new Dictionary<string, string> { ["openai"] = "ciphertext-1", ["anthropic"] = "ciphertext-2" },
        });
        await fixture.Get<ISubscriptionRepository>().CreateAsync(userId, customerId);
        await fixture.Get<IPromptRepository>().SaveAsync(userId, TestData.Prompt("p1"));

        var writing = fixture.Get<IWritingRepository>();
        await writing.SaveSessionAsync(userId, TestData.Session("s1"));
        await writing.SaveDraftAsync(userId, TestData.Draft("d1", "s1"));
        await writing.SaveEvaluationAsync(userId, TestData.Evaluation("e1", "s1", "d1"));

        await fixture.Get<IVaultRepository>().SaveManyAsync(userId, [TestData.VaultItem("v1"), TestData.VaultItem("v2")]);
        await fixture.Get<IDrillRepository>().SaveAsync(userId, TestData.Drill("dr1"));
        await fixture.Get<IAiUsageRepository>().RecordAsync(userId, TestData.Usage("u1", "rubric_score"));
        await fixture.Get<IImageRepository>().SaveAsync(userId, TestData.Image("i1", 10));

        return (email, customerId, token);
    }
}
