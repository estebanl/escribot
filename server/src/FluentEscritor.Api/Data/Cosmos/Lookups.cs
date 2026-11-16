using System.Text.Json.Nodes;
using FluentEscritor.Api.Data.Models;
using Microsoft.Azure.Cosmos;

namespace FluentEscritor.Api.Data.Cosmos;

/// <summary>
/// Finds a user by a key other than their id. Each lookup is one small document whose id,
/// which is also its partition key, is <c>kind:sha256(key)</c>, so a lookup is a single
/// point read and the key itself (a session token, for example) is never stored.
/// Creating a lookup fails if it exists, which is what makes emails unique.
/// <para>
/// A lookup can be created as a reservation (<c>pending</c>) for a document that is
/// written next, in another partition. Confirming a reservation and taking an abandoned
/// one over are both conditional writes to the same document, so only one of them wins.
/// </para>
/// </summary>
internal sealed class LookupStore(CosmosStore store, TimeProvider time)
{
    public const string Email = "email";
    public const string SessionToken = "session";
    public const string StripeCustomer = "stripe";
    public const string EmailVerificationToken = "verify-email";
    public const string PasswordResetToken = "reset-password";

    private Container Container => store.Lookups;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Id(string kind, string key) => $"{kind}:{DocumentId.Hash(key)}";

    /// <summary>Returns false if the key is already taken.</summary>
    public Task<bool> TryCreateAsync(string kind, string key, string userId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var id = Id(kind, key);
        return CosmosStore.CreateAsync(Container, id, Document(id, kind, userId, DocumentJson.Now(time), expiresAt), cancellationToken);
    }

    /// <summary>Creates a pending lookup. Returns false if the key is already taken.</summary>
    public Task<bool> TryReserveAsync(string kind, string key, string userId, CancellationToken cancellationToken)
    {
        var id = Id(kind, key);
        return CosmosStore.CreateAsync(Container, id, Document(id, kind, userId, DocumentJson.Now(time), null, pending: true), cancellationToken);
    }

    /// <summary>
    /// Makes the user's reservation permanent. Returns false if the lookup is gone or now
    /// belongs to someone else, in which case the caller does not hold the key.
    /// </summary>
    public async Task<bool> ConfirmAsync(string kind, string key, string userId, CancellationToken cancellationToken)
    {
        var id = Id(kind, key);
        var result = await CosmosStore.UpdateAsync(Container, id, id, current =>
        {
            if (Owner(current) != userId || !IsPending(current))
            {
                return null;
            }

            current.Remove(PendingProperty);
            return current;
        }, cancellationToken);

        return result is not null && Owner(result) == userId;
    }

    /// <summary>
    /// Moves a reservation that <paramref name="fromUserId"/> never confirmed to
    /// <paramref name="userId"/>, as a new reservation. Returns false if it was confirmed,
    /// removed or taken by someone else in the meantime.
    /// </summary>
    public async Task<bool> TryTakeOverAsync(string kind, string key, string fromUserId, string userId, CancellationToken cancellationToken)
    {
        var id = Id(kind, key);
        var taken = false;
        var result = await CosmosStore.UpdateAsync(Container, id, id, current =>
        {
            taken = Owner(current) == fromUserId && IsPending(current);
            return taken ? Document(id, kind, userId, DocumentJson.Now(time), null, pending: true) : null;
        }, cancellationToken);

        return result is not null && taken;
    }

    /// <summary>Removes the user's reservation if it is still theirs and still pending.</summary>
    public Task<bool> DeleteReservationAsync(string kind, string key, string userId, CancellationToken cancellationToken) =>
        CosmosStore.DeleteIfAsync(Container, Id(kind, key), Id(kind, key), current => Owner(current) == userId && IsPending(current), cancellationToken);

    public async Task<LookupEntry?> FindAsync(string kind, string key, CancellationToken cancellationToken)
    {
        var document = await CosmosStore.ReadAsync(Container, Id(kind, key), Id(kind, key), cancellationToken);
        if (document is null)
        {
            return null;
        }

        var entry = new LookupEntry(
            document["userId"]!.GetValue<string>(),
            document["createdAt"]!.GetValue<DateTimeOffset>(),
            document["expiresAt"]?.GetValue<DateTimeOffset>(),
            IsPending(document));

        // Cosmos DB removes expired documents in the background, so check as well.
        return entry.ExpiresAt is { } expiresAt && expiresAt <= DocumentJson.Now(time) ? null : entry;
    }

    /// <summary>Points an existing lookup at a user and expiry. Returns false if it does not exist.</summary>
    public async Task<bool> ReplaceAsync(string kind, string key, string userId, DateTimeOffset createdAt, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var id = Id(kind, key);
        var updated = await CosmosStore.UpdateAsync(Container, id, id, _ => Document(id, kind, userId, createdAt, expiresAt), cancellationToken);
        return updated is not null;
    }

    public Task<bool> DeleteAsync(string kind, string key, CancellationToken cancellationToken) =>
        CosmosStore.DeleteAsync(Container, Id(kind, key), Id(kind, key), cancellationToken);

    /// <summary>
    /// Deletes the user's lookups, of one kind or of every kind. Reads across partitions.
    /// The email lookup is deleted last: until then the user can still be found to sign
    /// in, and nobody else can register the address.
    /// </summary>
    public async Task DeleteAllForUserAsync(string userId, string? kind, CancellationToken cancellationToken)
    {
        var query = kind is null
            ? UserDocuments.Query("SELECT VALUE c.id FROM c WHERE c.userId = @userId", ("@userId", userId))
            : UserDocuments.Query("SELECT VALUE c.id FROM c WHERE c.userId = @userId AND c.kind = @kind", ("@userId", userId), ("@kind", kind));

        var ids = await CosmosStore.QueryAsync<string>(Container, null, query, cancellationToken);
        foreach (var id in ids.OrderBy(id => id.StartsWith($"{Email}:", StringComparison.Ordinal)))
        {
            await CosmosStore.DeleteAsync(Container, id, id, cancellationToken);
        }
    }

    private const string PendingProperty = "pending";

    private static string Owner(JsonObject document) => document["userId"]!.GetValue<string>();

    private static bool IsPending(JsonObject document) => document[PendingProperty]?.GetValue<bool>() == true;

    private JsonObject Document(string id, string kind, string userId, DateTimeOffset createdAt, DateTimeOffset? expiresAt, bool pending = false)
    {
        var document = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["userId"] = userId,
            ["createdAt"] = DocumentJson.Timestamp(createdAt),
        };

        if (pending)
        {
            document[PendingProperty] = true;
        }

        if (expiresAt is { } expires)
        {
            document["expiresAt"] = DocumentJson.Timestamp(expires);
            document["ttl"] = Math.Max(1, (int)Math.Ceiling((expires - DocumentJson.Now(time)).TotalSeconds));
        }

        return document;
    }
}

/// <param name="Pending">A reservation whose owner has not confirmed it yet.</param>
internal sealed record LookupEntry(string UserId, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Pending = false);

internal sealed class CosmosAuthSessionStore(LookupStore lookups) : IAuthSessionStore
{
    public async Task CreateAsync(string token, string userId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(userId, nameof(userId));
        if (!await lookups.TryCreateAsync(LookupStore.SessionToken, token, userId, expiresAt, cancellationToken))
        {
            throw new InvalidOperationException("A session with this token already exists.");
        }
    }

    public async Task<AuthSession?> FindAsync(string token, CancellationToken cancellationToken = default)
    {
        var entry = await lookups.FindAsync(LookupStore.SessionToken, token, cancellationToken);
        return entry is null ? null : new AuthSession(entry.UserId, entry.CreatedAt, entry.ExpiresAt!.Value);
    }

    public async Task<bool> ExtendAsync(string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var entry = await lookups.FindAsync(LookupStore.SessionToken, token, cancellationToken);
        return entry is not null
            && await lookups.ReplaceAsync(LookupStore.SessionToken, token, entry.UserId, entry.CreatedAt, expiresAt, cancellationToken);
    }

    public Task RemoveAsync(string token, CancellationToken cancellationToken = default) =>
        lookups.DeleteAsync(LookupStore.SessionToken, token, cancellationToken);

    public Task RemoveAllForUserAsync(string userId, CancellationToken cancellationToken = default) =>
        lookups.DeleteAllForUserAsync(userId, LookupStore.SessionToken, cancellationToken);
}

internal sealed class CosmosOneTimeTokenStore(LookupStore lookups) : IOneTimeTokenStore
{
    public async Task CreateAsync(OneTimeTokenPurpose purpose, string token, string userId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(userId, nameof(userId));
        if (!await lookups.TryCreateAsync(Kind(purpose), token, userId, expiresAt, cancellationToken))
        {
            throw new InvalidOperationException("This token already exists.");
        }
    }

    public async Task<string?> FindUserIdAsync(OneTimeTokenPurpose purpose, string token, CancellationToken cancellationToken = default) =>
        (await lookups.FindAsync(Kind(purpose), token, cancellationToken))?.UserId;

    public async Task<string?> RedeemAsync(OneTimeTokenPurpose purpose, string token, CancellationToken cancellationToken = default)
    {
        var entry = await lookups.FindAsync(Kind(purpose), token, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        // Deleting is what uses the token up: only one caller's delete finds the document.
        return await lookups.DeleteAsync(Kind(purpose), token, cancellationToken) ? entry.UserId : null;
    }

    public Task RemoveAllForUserAsync(OneTimeTokenPurpose purpose, string userId, CancellationToken cancellationToken = default) =>
        lookups.DeleteAllForUserAsync(userId, Kind(purpose), cancellationToken);

    private static string Kind(OneTimeTokenPurpose purpose) => purpose switch
    {
        OneTimeTokenPurpose.EmailVerification => LookupStore.EmailVerificationToken,
        OneTimeTokenPurpose.PasswordReset => LookupStore.PasswordResetToken,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };
}
