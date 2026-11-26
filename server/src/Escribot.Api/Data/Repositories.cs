using Escribot.Api.Data.Models;

namespace Escribot.Api.Data;

// The repository layer. The rest of the app depends on these interfaces and the model
// records only; the Cosmos DB SDK stays inside Data/Cosmos.
//
// Every method that touches a user's data takes that user's id and reads or writes only
// that user's partition. A document id that belongs to someone else is simply not found.
// When saving, the user id argument decides the owner; any UserId on the record is ignored.

public interface IUserAccountRepository
{
    /// <summary>Creates the account and reserves its email address, which must be unused.</summary>
    Task<CreateAccountResult> CreateAsync(UserAccount account, CancellationToken cancellationToken = default);

    Task<UserAccount?> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="change"/> to the current account and saves it, retrying if
    /// someone else saved first. The email address cannot be changed here.
    /// Returns null if the account does not exist.
    /// </summary>
    Task<UserAccount?> UpdateAsync(string userId, Func<UserAccount, UserAccount> change, CancellationToken cancellationToken = default);
}

/// <summary>Browser sessions, found by token. Only a hash of each token is stored.</summary>
public interface IAuthSessionStore
{
    Task CreateAsync(string token, string userId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Returns null for an unknown or expired token.</summary>
    Task<AuthSession?> FindAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Returns false for an unknown or expired token.</summary>
    Task<bool> ExtendAsync(string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task RemoveAsync(string token, CancellationToken cancellationToken = default);

    Task RemoveAllForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Single-use tokens sent by email, each found by the token itself and tied to one user.
/// Only a hash of each token is stored, and an expired token is never returned.
/// </summary>
public interface IOneTimeTokenStore
{
    Task CreateAsync(OneTimeTokenPurpose purpose, string token, string userId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>The user the token was issued to, without using the token up.</summary>
    Task<string?> FindUserIdAsync(OneTimeTokenPurpose purpose, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uses the token up and returns the user it was issued to. Of several callers with
    /// the same token, only one gets the user; the others get null.
    /// </summary>
    Task<string?> RedeemAsync(OneTimeTokenPurpose purpose, string token, CancellationToken cancellationToken = default);

    Task RemoveAllForUserAsync(OneTimeTokenPurpose purpose, string userId, CancellationToken cancellationToken = default);
}

public interface ISettingsRepository
{
    /// <summary>Returns the user's settings, creating them with defaults on first use.</summary>
    Task<UserSettings> GetOrCreateAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Applies <paramref name="change"/> and saves, retrying if someone else saved first.</summary>
    Task<UserSettings> UpdateAsync(string userId, Func<UserSettings, UserSettings> change, CancellationToken cancellationToken = default);
}

public interface ISubscriptionRepository
{
    Task<Subscription?> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<Subscription?> FindByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the user's Stripe customer. If the user already has one, that existing
    /// subscription is returned unchanged. Either way the subscription can be found by
    /// its Stripe customer id once this returns, so it is safe to call again after a failure.
    /// </summary>
    Task<Subscription> CreateAsync(string userId, string stripeCustomerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="change"/> and saves, retrying if someone else saved first.
    /// Return the record unchanged from <paramref name="change"/> to skip the write, for
    /// example for a stale Stripe event. Returns null if the user has no subscription.
    /// </summary>
    Task<Subscription?> UpdateAsync(string userId, Func<Subscription, Subscription> change, CancellationToken cancellationToken = default);
}

public interface IPromptRepository
{
    /// <summary>The prompts a user may see: the shared curated bank plus their own.</summary>
    Task<IReadOnlyList<Prompt>> ListVisibleAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>A curated prompt or one of the user's own; null for anyone else's.</summary>
    Task<Prompt?> GetVisibleAsync(string userId, string promptId, CancellationToken cancellationToken = default);

    /// <summary>Saves one of the user's own prompts. Curated prompts cannot be written here.</summary>
    Task SaveAsync(string userId, Prompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Deletes one of the user's own prompts. Returns false if there was none.</summary>
    Task<bool> DeleteAsync(string userId, string promptId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="change"/> to one of the user's own prompts and writes the result
    /// only if nobody wrote the prompt in between; otherwise applies it again to the newer
    /// version. <paramref name="change"/> returns null to leave the prompt as it is. Returns the
    /// version the change was applied to and the result, or null if there is no such prompt:
    /// a prompt deleted meanwhile is never written back.
    /// </summary>
    Task<(Prompt Before, Prompt After)?> UpdateAsync(string userId, string promptId, Func<Prompt, Prompt?> change, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one of the user's own prompts if <paramref name="condition"/> holds for the
    /// version deleted, and returns that version; null if there was none or it did not hold.
    /// </summary>
    Task<Prompt?> DeleteIfAsync(string userId, string promptId, Func<Prompt, bool> condition, CancellationToken cancellationToken = default);
}

/// <summary>The shared curated prompt bank. Written only by the startup seeding.</summary>
public interface ICuratedPromptStore
{
    /// <summary>Prompt id to the hash of the seed entry it was written from.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSeedHashesAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(Prompt prompt, string seedHash, CancellationToken cancellationToken = default);

    Task DeleteAsync(string promptId, CancellationToken cancellationToken = default);
}

/// <summary>Writing sessions with their drafts and evaluations.</summary>
public interface IWritingRepository
{
    /// <summary>The session if it belongs to the user, otherwise null.</summary>
    Task<WritingSession?> GetOwnedSessionAsync(string userId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>The session with its drafts and evaluations, in one query.</summary>
    Task<SessionBundle?> GetSessionBundleAsync(string userId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<WritingSession>> ListSessionsAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveSessionAsync(string userId, WritingSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the session with its drafts and evaluations. Up to 99 of them go with the
    /// session in one transaction. With more, the session itself is deleted last, so after
    /// a failure the session still exists and calling this again completes the deletion.
    /// </summary>
    Task<bool> DeleteSessionAsync(string userId, string sessionId, CancellationToken cancellationToken = default);

    Task<Draft?> GetDraftAsync(string userId, string draftId, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Draft>> ListDraftsAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveDraftAsync(string userId, Draft draft, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<Evaluation>> ListEvaluationsAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveEvaluationAsync(string userId, Evaluation evaluation, CancellationToken cancellationToken = default);
}

public interface IVaultRepository
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<VaultItem>> ListAsync(string userId, CancellationToken cancellationToken = default);

    Task<VaultItem?> GetAsync(string userId, string itemId, CancellationToken cancellationToken = default);

    Task SaveAsync(string userId, VaultItem item, CancellationToken cancellationToken = default);

    /// <summary>Saves the items together: all are written or none.</summary>
    Task SaveManyAsync(string userId, IReadOnlyList<VaultItem> items, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string userId, string itemId, CancellationToken cancellationToken = default);
}

public interface IDrillRepository
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<MicroDrill>> ListAsync(string userId, CancellationToken cancellationToken = default);

    Task<MicroDrill?> GetAsync(string userId, string drillId, CancellationToken cancellationToken = default);

    Task SaveAsync(string userId, MicroDrill drill, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string userId, string drillId, CancellationToken cancellationToken = default);
}

public interface IAiUsageRepository
{
    Task RecordAsync(string userId, AiUsageRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the user's calls since <paramref name="since"/> to any of <paramref name="features"/>,
    /// optionally only those made with one key source. This is the monthly quota check.
    /// </summary>
    Task<int> CountAsync(string userId, DateTimeOffset since, IReadOnlyCollection<string> features, string? keySource = null, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<AiUsageRecord>> ListSinceAsync(string userId, DateTimeOffset since, CancellationToken cancellationToken = default);
}

public interface IImageRepository
{
    Task<ImageMetadata?> GetAsync(string userId, string imageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImageMetadata>> ListAsync(string userId, CancellationToken cancellationToken = default);

    Task SaveAsync(string userId, ImageMetadata image, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string userId, string imageId, CancellationToken cancellationToken = default);

    /// <summary>Total size of the user's images, for the per-plan storage allowance.</summary>
    Task<long> GetTotalBytesAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Fixed-window rate limits that hold across instances.</summary>
public interface IRateLimiter
{
    /// <summary>
    /// Counts one request against <paramref name="key"/> in the current window and says
    /// whether it is within <paramref name="limit"/>. Each call is one atomic increment.
    /// </summary>
    Task<RateLimitResult> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default);
}

/// <summary>Platform-wide count of Free-plan AI requests in the current UTC day.</summary>
public interface IFreeAiDailyCounter
{
    Task<long> GetTodayAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds one atomically and returns the new total for today.</summary>
    Task<long> IncrementTodayAsync(CancellationToken cancellationToken = default);
}

public interface IErrorEventRepository
{
    /// <summary>
    /// Stores the event after shortening it and removing anything that looks like a
    /// credential. Callers must not put essay text in an error. Id and time are assigned here.
    /// </summary>
    Task RecordAsync(ErrorEvent errorEvent, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ErrorEvent>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);

    Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

/// <summary>Operations over everything one user owns.</summary>
public interface IUserDataRepository
{
    Task<UserDataExport> ExportAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves everything in <paramref name="import"/> as the user's own, in the order
    /// prompts, sessions, drafts, evaluations, vault items, drills. Up to 100 documents
    /// of moderate size are saved as one transaction; a larger import is saved in several,
    /// so a failure part way leaves the earlier ones saved.
    /// </summary>
    Task ImportAsync(string userId, UserDataImport import, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every document the user owns, their lookups (email, sessions, Stripe
    /// customer, emailed tokens), and detaches their id from error events. The user's
    /// documents go first, then the lookups with the email one last, then the account
    /// document. Until the documents are gone the user can therefore still sign in, so a
    /// deletion that failed part way can be run again by the user. Safe to run again.
    /// Blobs are not touched: list the user's images and delete those blobs first.
    /// </summary>
    Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Platform-wide figures for the admin page. These read across all users, so only admin
/// endpoints may call them.
/// </summary>
public interface IAdminRepository
{
    Task<AccountStats> GetAccountStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Newest accounts first, optionally only those whose email contains <paramref name="emailContains"/>.</summary>
    Task<IReadOnlyList<AdminAccountRow>> ListAccountsAsync(string? emailContains, int limit, CancellationToken cancellationToken = default);

    /// <summary>Stripe subscription status to the number of users in it.</summary>
    Task<IReadOnlyDictionary<string, int>> GetSubscriptionStatusCountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiUsageTotal>> GetAiUsageTotalsAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    Task<int> CountAiCallsAsync(DateTimeOffset since, IReadOnlyCollection<string> features, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserAiUsage>> GetAiUsageByUserAsync(IReadOnlyCollection<string> userIds, DateTimeOffset since, IReadOnlyCollection<string> countedFeatures, CancellationToken cancellationToken = default);
}
