using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Tests;

/// <summary>
/// One user's repository calls can never return, change or delete another user's
/// documents, even when they know the exact ids.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AppHostCollection.Name)]
public class TenantIsolationTests(AppHostFixture fixture) : IAsyncLifetime
{
    private readonly string _owner = AppHostFixture.NewUserId();
    private readonly string _intruder = AppHostFixture.NewUserId();

    // The same ids are used for every document, to prove the owner is what separates them.
    private const string PromptId = "prompt-1";
    private const string SessionId = "session-1";
    private const string DraftId = "draft-1";
    private const string EvaluationId = "evaluation-1";
    private const string VaultId = "vault-1";
    private const string DrillId = "drill-1";
    private const string UsageId = "usage-1";
    private const string ImageId = "image-1";
    private const string Feature = "rubric_score";

    private string _customerId = null!;

    public async Task InitializeAsync()
    {
        _customerId = AppHostFixture.NewId("cus");

        Assert.Equal(CreateAccountResult.Created, await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(_owner)));
        Assert.Equal(CreateAccountResult.Created, await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(_intruder)));

        await fixture.Get<ISettingsRepository>().UpdateAsync(_owner, s => s with { NativeLanguage = "pt" });
        await fixture.Get<ISubscriptionRepository>().CreateAsync(_owner, _customerId);
        await fixture.Get<IPromptRepository>().SaveAsync(_owner, TestData.Prompt(PromptId));

        var writing = fixture.Get<IWritingRepository>();
        await writing.SaveSessionAsync(_owner, TestData.Session(SessionId));
        await writing.SaveDraftAsync(_owner, TestData.Draft(DraftId, SessionId));
        await writing.SaveEvaluationAsync(_owner, TestData.Evaluation(EvaluationId, SessionId, DraftId));

        await fixture.Get<IVaultRepository>().SaveAsync(_owner, TestData.VaultItem(VaultId));
        await fixture.Get<IDrillRepository>().SaveAsync(_owner, TestData.Drill(DrillId));
        await fixture.Get<IAiUsageRepository>().RecordAsync(_owner, TestData.Usage(UsageId, Feature));
        await fixture.Get<IImageRepository>().SaveAsync(_owner, TestData.Image(ImageId, 1000));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_owner_sees_their_own_documents()
    {
        Assert.NotNull(await fixture.Get<IPromptRepository>().GetVisibleAsync(_owner, PromptId));
        Assert.NotNull(await fixture.Get<IWritingRepository>().GetOwnedSessionAsync(_owner, SessionId));
        Assert.NotNull(await fixture.Get<IWritingRepository>().GetDraftAsync(_owner, DraftId));
        Assert.NotNull(await fixture.Get<IVaultRepository>().GetAsync(_owner, VaultId));
        Assert.NotNull(await fixture.Get<IDrillRepository>().GetAsync(_owner, DrillId));
        Assert.NotNull(await fixture.Get<IImageRepository>().GetAsync(_owner, ImageId));
        Assert.Equal(1, await fixture.Get<IAiUsageRepository>().CountAsync(_owner, TestData.Start.AddDays(-1), [Feature]));
    }

    [Fact]
    public async Task Another_user_cannot_read_documents_by_id()
    {
        Assert.Null(await fixture.Get<IPromptRepository>().GetVisibleAsync(_intruder, PromptId));
        Assert.Null(await fixture.Get<IWritingRepository>().GetOwnedSessionAsync(_intruder, SessionId));
        Assert.Null(await fixture.Get<IWritingRepository>().GetSessionBundleAsync(_intruder, SessionId));
        Assert.Null(await fixture.Get<IWritingRepository>().GetDraftAsync(_intruder, DraftId));
        Assert.Null(await fixture.Get<IVaultRepository>().GetAsync(_intruder, VaultId));
        Assert.Null(await fixture.Get<IDrillRepository>().GetAsync(_intruder, DrillId));
        Assert.Null(await fixture.Get<IImageRepository>().GetAsync(_intruder, ImageId));
        Assert.Null(await fixture.Get<ISubscriptionRepository>().GetAsync(_intruder));
    }

    [Fact]
    public async Task Another_user_sees_nothing_in_lists_counts_or_exports()
    {
        var writing = fixture.Get<IWritingRepository>();

        Assert.DoesNotContain(await fixture.Get<IPromptRepository>().ListVisibleAsync(_intruder), p => p.UserId is not null);
        Assert.Empty(await writing.ListSessionsAsync(_intruder));
        Assert.Empty(await writing.ListDraftsAsync(_intruder));
        Assert.Empty(await writing.ListEvaluationsAsync(_intruder));
        Assert.Empty(await fixture.Get<IVaultRepository>().ListAsync(_intruder));
        Assert.Empty(await fixture.Get<IDrillRepository>().ListAsync(_intruder));
        Assert.Empty(await fixture.Get<IImageRepository>().ListAsync(_intruder));
        Assert.Empty(await fixture.Get<IAiUsageRepository>().ListSinceAsync(_intruder, TestData.Start.AddDays(-1)));
        Assert.Equal(0, await fixture.Get<IAiUsageRepository>().CountAsync(_intruder, TestData.Start.AddDays(-1), [Feature]));
        Assert.Equal(0, await fixture.Get<IImageRepository>().GetTotalBytesAsync(_intruder));

        var export = await fixture.Get<IUserDataRepository>().ExportAsync(_intruder);
        Assert.Equal(_intruder, export.Account!.Id);
        Assert.Empty(export.Prompts);
        Assert.Empty(export.Sessions);
        Assert.Empty(export.Drafts);
        Assert.Empty(export.Evaluations);
        Assert.Empty(export.VaultItems);
        Assert.Empty(export.MicroDrills);
        Assert.Empty(export.AiUsage);
        Assert.Empty(export.Images);
        Assert.Null(export.Subscription);
    }

    [Fact]
    public async Task Another_user_cannot_delete_or_update_documents()
    {
        Assert.False(await fixture.Get<IPromptRepository>().DeleteAsync(_intruder, PromptId));
        Assert.False(await fixture.Get<IWritingRepository>().DeleteSessionAsync(_intruder, SessionId));
        Assert.False(await fixture.Get<IVaultRepository>().DeleteAsync(_intruder, VaultId));
        Assert.False(await fixture.Get<IDrillRepository>().DeleteAsync(_intruder, DrillId));
        Assert.False(await fixture.Get<IImageRepository>().DeleteAsync(_intruder, ImageId));
        Assert.Null(await fixture.Get<ISubscriptionRepository>().UpdateAsync(_intruder, s => s with { Status = "active" }));

        await The_owner_sees_their_own_documents();
        Assert.Null((await fixture.Get<ISubscriptionRepository>().GetAsync(_owner))!.Status);
    }

    [Fact]
    public async Task Saving_with_the_same_id_never_touches_the_other_users_document()
    {
        // The intruder saves a record with the owner's document id, and even claims the
        // owner's user id on the record. The user id argument decides where it goes.
        var forged = TestData.VaultItem(VaultId) with { UserId = _owner, UpgradedText = "overwritten" };
        await fixture.Get<IVaultRepository>().SaveAsync(_intruder, forged);
        await fixture.Get<IWritingRepository>().SaveSessionAsync(_intruder, TestData.Session(SessionId) with { UserId = _owner, Mode = "simulator_t1" });

        var owned = await fixture.Get<IVaultRepository>().GetAsync(_owner, VaultId);
        Assert.Equal("pressing concern", owned!.UpgradedText);
        Assert.Equal(_owner, owned.UserId);
        Assert.Equal("coach", (await fixture.Get<IWritingRepository>().GetOwnedSessionAsync(_owner, SessionId))!.Mode);

        var theirs = await fixture.Get<IVaultRepository>().GetAsync(_intruder, VaultId);
        Assert.Equal("overwritten", theirs!.UpgradedText);
        Assert.Equal(_intruder, theirs.UserId);
    }

    [Fact]
    public async Task Settings_are_separate_per_user()
    {
        var settings = fixture.Get<ISettingsRepository>();

        Assert.Equal("pt", (await settings.GetOrCreateAsync(_owner)).NativeLanguage);
        Assert.Equal("es", (await settings.GetOrCreateAsync(_intruder)).NativeLanguage);
    }

    [Fact]
    public async Task Deleting_one_user_leaves_the_other_intact()
    {
        await fixture.Get<IUserDataRepository>().DeleteAllAsync(_intruder);

        await The_owner_sees_their_own_documents();
        Assert.NotNull(await fixture.Get<IUserAccountRepository>().GetAsync(_owner));
        Assert.NotNull(await fixture.Get<IUserAccountRepository>().FindByEmailAsync($"{_owner}@example.test"));
        Assert.Equal(_owner, (await fixture.Get<ISubscriptionRepository>().FindByStripeCustomerIdAsync(_customerId))!.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    [InlineData("vaultItem:vault-1")]
    [InlineData("has space")]
    public async Task Malformed_ids_are_not_found_and_cannot_be_saved(string id)
    {
        Assert.Null(await fixture.Get<IVaultRepository>().GetAsync(_owner, id));
        Assert.False(await fixture.Get<IVaultRepository>().DeleteAsync(_owner, id));
        Assert.Null(await fixture.Get<IVaultRepository>().GetAsync(id, VaultId));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Get<IVaultRepository>().SaveAsync(_owner, TestData.VaultItem(id)));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Get<IVaultRepository>().SaveAsync(id, TestData.VaultItem(VaultId)));
    }
}
