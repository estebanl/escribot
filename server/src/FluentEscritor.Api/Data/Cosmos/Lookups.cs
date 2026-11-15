using System.Text.Json.Nodes;
using FluentEscritor.Api.Data.Models;
using Microsoft.Azure.Cosmos;

namespace FluentEscritor.Api.Data.Cosmos;

/// <summary>
/// Finds a user by a key other than their id. Each lookup is one small document whose id,
/// which is also its partition key, is <c>kind:sha256(key)</c>, so a lookup is a single
/// point read and the key itself (a session token, for example) is never stored.
/// Creating a lookup fails if it exists, which is what makes emails unique.
/// </summary>
internal sealed class LookupStore(CosmosStore store, TimeProvider time)
{
    public const string Email = "email";
    public const string SessionToken = "session";
    public const string StripeCustomer = "stripe";

    private Container Container => store.Lookups;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Id(string kind, string key) => $"{kind}:{DocumentId.Hash(key)}";

    /// <summary>Returns false if the key is already taken.</summary>
    public Task<bool> TryCreateAsync(string kind, string key, string userId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var id = Id(kind, key);
        return CosmosStore.CreateAsync(Container, id, Document(id, kind, userId, DocumentJson.Now(time), expiresAt), cancellationToken);
    }

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
            document["expiresAt"]?.GetValue<DateTimeOffset>());

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

    /// <summary>Deletes the user's lookups, of one kind or of every kind. Reads across partitions.</summary>
    public async Task DeleteAllForUserAsync(string userId, string? kind, CancellationToken cancellationToken)
    {
        var query = kind is null
            ? UserDocuments.Query("SELECT VALUE c.id FROM c WHERE c.userId = @userId", ("@userId", userId))
            : UserDocuments.Query("SELECT VALUE c.id FROM c WHERE c.userId = @userId AND c.kind = @kind", ("@userId", userId), ("@kind", kind));

        foreach (var id in await CosmosStore.QueryAsync<string>(Container, null, query, cancellationToken))
        {
            await CosmosStore.DeleteAsync(Container, id, id, cancellationToken);
        }
    }

    private JsonObject Document(string id, string kind, string userId, DateTimeOffset createdAt, DateTimeOffset? expiresAt)
    {
        var document = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["userId"] = userId,
            ["createdAt"] = DocumentJson.Timestamp(createdAt),
        };

        if (expiresAt is { } expires)
        {
            document["expiresAt"] = DocumentJson.Timestamp(expires);
            document["ttl"] = Math.Max(1, (int)Math.Ceiling((expires - DocumentJson.Now(time)).TotalSeconds));
        }

        return document;
    }
}

internal sealed record LookupEntry(string UserId, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

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
