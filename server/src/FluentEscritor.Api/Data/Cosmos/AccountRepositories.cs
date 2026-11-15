using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Data.Cosmos;

internal sealed class CosmosUserAccountRepository(UserDocuments documents, LookupStore lookups, TimeProvider time) : IUserAccountRepository
{
    /// <summary>
    /// How long an unconfirmed email reservation is left to the sign-up that made it.
    /// Uniqueness does not depend on this value: a slower sign-up fails to confirm.
    /// </summary>
    internal static readonly TimeSpan ReservationGracePeriod = TimeSpan.FromMinutes(2);

    public async Task<CreateAccountResult> CreateAsync(UserAccount account, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(account.Id, nameof(account));
        var email = LookupStore.NormalizeEmail(account.Email);
        if (email.Length == 0)
        {
            throw new ArgumentException("An account needs an email address.", nameof(account));
        }

        // The lookup is written first: it is what makes the email unique. A lookup and an
        // account live in different partitions, so the pair cannot be one transaction.
        // Instead the lookup starts as a reservation and is confirmed once the account is
        // written; only the request whose confirmation succeeds has created the account.
        if (!await lookups.TryReserveAsync(LookupStore.Email, email, account.Id, cancellationToken)
            && !await TryTakeOverAbandonedReservationAsync(email, account.Id, cancellationToken))
        {
            return CreateAccountResult.EmailTaken;
        }

        var now = DocumentJson.Now(time);
        var stored = account with { Email = account.Email.Trim(), NormalizedEmail = email, CreatedAt = now, UpdatedAt = now };
        try
        {
            if (!await documents.CreateAsync(DocumentTypes.Account, account.Id, stored, cancellationToken))
            {
                throw new InvalidOperationException("An account with this id already exists.");
            }
        }
        catch
        {
            await lookups.DeleteReservationAsync(LookupStore.Email, email, account.Id, CancellationToken.None);
            throw;
        }

        if (!await lookups.ConfirmAsync(LookupStore.Email, email, account.Id, cancellationToken))
        {
            // This request took so long that its reservation was judged abandoned and
            // another sign-up now holds the address: withdraw the account just written.
            await documents.DeleteAsync(DocumentTypes.Account, account.Id, null, CancellationToken.None);
            return CreateAccountResult.EmailTaken;
        }

        return CreateAccountResult.Created;
    }

    /// <summary>
    /// The email already has a lookup. Returns true only if that lookup was an abandoned
    /// reservation (an earlier sign-up stopped before writing its account) and it now
    /// belongs to <paramref name="userId"/>.
    /// </summary>
    private async Task<bool> TryTakeOverAbandonedReservationAsync(string email, string userId, CancellationToken cancellationToken)
    {
        var existing = await lookups.FindAsync(LookupStore.Email, email, cancellationToken);
        if (existing is null || !existing.Pending)
        {
            return false;
        }

        var owner = await GetAsync(existing.UserId, cancellationToken);
        if (owner?.NormalizedEmail == email)
        {
            // The account was written but its sign-up stopped before confirming: finish it.
            await lookups.ConfirmAsync(LookupStore.Email, email, existing.UserId, cancellationToken);
            return false;
        }

        // A sign-up that is still running has no account yet either, so a missing account
        // alone proves nothing. Leave a recent reservation to its owner.
        if (DocumentJson.Now(time) - existing.CreatedAt < ReservationGracePeriod)
        {
            return false;
        }

        return await lookups.TryTakeOverAsync(LookupStore.Email, email, existing.UserId, userId, cancellationToken);
    }

    public Task<UserAccount?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.Account, userId, null, cancellationToken);

    public async Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var entry = await lookups.FindAsync(LookupStore.Email, LookupStore.NormalizeEmail(email), cancellationToken);
        return entry is null ? null : await GetAsync(entry.UserId, cancellationToken);
    }

    public Task<UserAccount?> UpdateAsync(string userId, Func<UserAccount, UserAccount> change, CancellationToken cancellationToken = default) =>
        documents.UpdateAsync(DocumentTypes.Account, userId, null, current =>
        {
            var updated = change(current);
            if (ReferenceEquals(updated, current))
            {
                return current;
            }

            if (LookupStore.NormalizeEmail(updated.Email) != current.NormalizedEmail)
            {
                throw new InvalidOperationException("The email address cannot be changed through UpdateAsync.");
            }

            return updated with { Id = current.Id, NormalizedEmail = current.NormalizedEmail, CreatedAt = current.CreatedAt, UpdatedAt = DocumentJson.Now(time) };
        }, cancellationToken);
}

internal sealed class CosmosSettingsRepository(UserDocuments documents, TimeProvider time) : ISettingsRepository
{
    public async Task<UserSettings> GetOrCreateAsync(string userId, CancellationToken cancellationToken = default)
    {
        var existing = await documents.GetAsync(DocumentTypes.Settings, userId, null, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var defaults = new UserSettings { UserId = userId, UpdatedAt = DocumentJson.Now(time) };
        if (await documents.CreateAsync(DocumentTypes.Settings, userId, defaults, cancellationToken))
        {
            return defaults;
        }

        // Another request created them at the same moment.
        return (await documents.GetAsync(DocumentTypes.Settings, userId, null, cancellationToken))!;
    }

    public async Task<UserSettings> UpdateAsync(string userId, Func<UserSettings, UserSettings> change, CancellationToken cancellationToken = default)
    {
        await GetOrCreateAsync(userId, cancellationToken);
        var updated = await documents.UpdateAsync(DocumentTypes.Settings, userId, null, current =>
        {
            var changed = change(current);
            return ReferenceEquals(changed, current) ? current : changed with { UserId = userId, UpdatedAt = DocumentJson.Now(time) };
        }, cancellationToken);

        return updated ?? throw new InvalidOperationException("The settings were deleted while they were being updated.");
    }
}

internal sealed class CosmosSubscriptionRepository(UserDocuments documents, LookupStore lookups, TimeProvider time) : ISubscriptionRepository
{
    public Task<Subscription?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.Subscription, userId, null, cancellationToken);

    public async Task<Subscription?> FindByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken cancellationToken = default)
    {
        var entry = await lookups.FindAsync(LookupStore.StripeCustomer, stripeCustomerId, cancellationToken);
        return entry is null ? null : await GetAsync(entry.UserId, cancellationToken);
    }

    public async Task<Subscription> CreateAsync(string userId, string stripeCustomerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeCustomerId);

        // The subscription document is what gives a user exactly one customer.
        var subscription = new Subscription { UserId = userId, StripeCustomerId = stripeCustomerId, UpdatedAt = DocumentJson.Now(time) };
        var created = await documents.CreateAsync(DocumentTypes.Subscription, userId, subscription, cancellationToken);
        if (!created)
        {
            subscription = await GetAsync(userId, cancellationToken)
                ?? throw new InvalidOperationException("The subscription was deleted while it was being created.");

            // An earlier attempt may have stopped between the two writes, so the existing
            // subscription is only returned once its lookup is known to be there.
            var entry = await lookups.FindAsync(LookupStore.StripeCustomer, subscription.StripeCustomerId, cancellationToken);
            if (entry?.UserId == userId)
            {
                return subscription;
            }
        }

        if (!await lookups.TryCreateAsync(LookupStore.StripeCustomer, subscription.StripeCustomerId, userId, null, cancellationToken))
        {
            var owner = await lookups.FindAsync(LookupStore.StripeCustomer, subscription.StripeCustomerId, cancellationToken);
            if (owner?.UserId != userId)
            {
                if (created)
                {
                    await documents.DeleteAsync(DocumentTypes.Subscription, userId, null, CancellationToken.None);
                }

                throw new InvalidOperationException("This Stripe customer already belongs to another user.");
            }
        }

        return subscription;
    }

    public Task<Subscription?> UpdateAsync(string userId, Func<Subscription, Subscription> change, CancellationToken cancellationToken = default) =>
        documents.UpdateAsync(DocumentTypes.Subscription, userId, null, current =>
        {
            var changed = change(current);
            return ReferenceEquals(changed, current)
                ? current
                : changed with { UserId = userId, StripeCustomerId = current.StripeCustomerId, UpdatedAt = DocumentJson.Now(time) };
        }, cancellationToken);
}
