using System.Net;
using System.Text.Json.Nodes;
using Escribot.DataModel;
using Microsoft.Azure.Cosmos;

namespace Escribot.Api.Data.Cosmos;

/// <summary>
/// The containers, and the few document operations the repositories are built from.
/// Documents travel as <see cref="JsonObject"/> so the stored shape is fully under this layer's control.
/// </summary>
internal sealed class CosmosStore(CosmosClient client)
{
    private const int MaxConcurrencyRetries = 10;

    /// <summary>Cosmos DB accepts at most this many operations in one transactional batch.</summary>
    public const int MaxBatchSize = 100;

    private static readonly ItemRequestOptions NoContent = new() { EnableContentResponseOnWrite = false };

    private readonly Database _database = client.GetDatabase(DataModelSchema.DatabaseName);

    public Container Users => _database.GetContainer(DataModelSchema.Users.Name);

    public Container Shared => _database.GetContainer(DataModelSchema.Shared.Name);

    public Container Lookups => _database.GetContainer(DataModelSchema.Lookups.Name);

    public Container Counters => _database.GetContainer(DataModelSchema.Counters.Name);

    public Container Errors => _database.GetContainer(DataModelSchema.Errors.Name);

    public static async Task<JsonObject?> ReadAsync(Container container, string partitionKey, string id, CancellationToken cancellationToken)
    {
        var (document, _) = await ReadWithETagAsync(container, partitionKey, id, cancellationToken);
        return document;
    }

    private static async Task<(JsonObject? Document, string? ETag)> ReadWithETagAsync(Container container, string partitionKey, string id, CancellationToken cancellationToken)
    {
        using var response = await container.ReadItemStreamAsync(id, new PartitionKey(partitionKey), cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }

        response.EnsureSuccessStatusCode();
        return (await DocumentJson.ReadAsync(response.Content, cancellationToken), response.Headers.ETag);
    }

    /// <summary>Returns false if a document with that id already exists.</summary>
    public static async Task<bool> CreateAsync(Container container, string partitionKey, JsonObject document, CancellationToken cancellationToken)
    {
        using var stream = DocumentJson.ToStream(document);
        using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(partitionKey), NoContent, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public static async Task UpsertAsync(Container container, string partitionKey, JsonObject document, CancellationToken cancellationToken)
    {
        using var stream = DocumentJson.ToStream(document);
        using var response = await container.UpsertItemStreamAsync(stream, new PartitionKey(partitionKey), NoContent, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Returns false if there was no such document.</summary>
    public static async Task<bool> DeleteAsync(Container container, string partitionKey, string id, CancellationToken cancellationToken)
    {
        using var response = await container.DeleteItemStreamAsync(id, new PartitionKey(partitionKey), NoContent, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Deletes the document only if <paramref name="condition"/> holds for the version that
    /// is deleted. Returns false if it does not exist or the condition does not hold.
    /// </summary>
    public static async Task<bool> DeleteIfAsync(Container container, string partitionKey, string id, Func<JsonObject, bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var (current, etag) = await ReadWithETagAsync(container, partitionKey, id, cancellationToken);
            if (current is null || !condition(current))
            {
                return false;
            }

            var options = new ItemRequestOptions { IfMatchEtag = etag, EnableContentResponseOnWrite = false };
            using var response = await container.DeleteItemStreamAsync(id, new PartitionKey(partitionKey), options, cancellationToken);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }

        throw new InvalidOperationException("The document kept changing while it was being deleted.");
    }

    /// <summary>
    /// Reads the document, applies <paramref name="change"/>, and writes it back only if
    /// nobody else wrote in between; otherwise starts again from the newer version.
    /// <paramref name="change"/> returns null to leave the document as it is.
    /// Returns the final document, or null if it does not exist.
    /// </summary>
    public static async Task<JsonObject?> UpdateAsync(Container container, string partitionKey, string id, Func<JsonObject, JsonObject?> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var (current, etag) = await ReadWithETagAsync(container, partitionKey, id, cancellationToken);
            if (current is null)
            {
                return null;
            }

            var updated = change(current);
            if (updated is null)
            {
                return current;
            }

            using var stream = DocumentJson.ToStream(updated);
            var options = new ItemRequestOptions { IfMatchEtag = etag, EnableContentResponseOnWrite = false };
            using var response = await container.ReplaceItemStreamAsync(stream, id, new PartitionKey(partitionKey), options, cancellationToken);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return updated;
        }

        throw new InvalidOperationException("The document kept changing while it was being updated.");
    }

    /// <summary>
    /// Adds one to <c>count</c> on the server, creating the document at one if it does
    /// not exist yet. Concurrent callers each get a different number.
    /// </summary>
    public static async Task<long> IncrementAsync(Container container, string id, int timeToLiveSeconds, CancellationToken cancellationToken)
    {
        var partitionKey = new PartitionKey(id);
        PatchOperation[] increment = [PatchOperation.Increment("/count", 1)];

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            using (var patched = await container.PatchItemStreamAsync(id, partitionKey, increment, cancellationToken: cancellationToken))
            {
                if (patched.StatusCode != HttpStatusCode.NotFound)
                {
                    patched.EnsureSuccessStatusCode();
                    var document = await DocumentJson.ReadAsync(patched.Content, cancellationToken);
                    return document["count"]!.GetValue<long>();
                }
            }

            var first = new JsonObject { ["id"] = id, ["count"] = 1, ["ttl"] = timeToLiveSeconds };
            if (await CreateAsync(container, id, first, cancellationToken))
            {
                return 1;
            }

            // Someone else created it in between: go round and increment theirs.
        }

        throw new InvalidOperationException("The counter could not be incremented.");
    }

    /// <summary>Runs a query, in one partition or, when <paramref name="partitionKey"/> is null, across all of them.</summary>
    public static async Task<List<T>> QueryAsync<T>(Container container, string? partitionKey, QueryDefinition query, CancellationToken cancellationToken)
    {
        var options = partitionKey is null ? null : new QueryRequestOptions { PartitionKey = new PartitionKey(partitionKey) };
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: options);
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// Applies the operations in one partition as a single transaction per batch of 100, in
    /// the order given. More than 100 operations are therefore not one transaction: if a
    /// later batch fails, the earlier ones stay applied.
    /// </summary>
    public static async Task BatchAsync<T>(Container container, string partitionKey, IReadOnlyList<T> items, Action<TransactionalBatch, T> add, CancellationToken cancellationToken)
    {
        foreach (var chunk in items.Chunk(MaxBatchSize))
        {
            var batch = container.CreateTransactionalBatch(new PartitionKey(partitionKey));
            foreach (var item in chunk)
            {
                add(batch, item);
            }

            using var response = await batch.ExecuteAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new CosmosException("A batch of document changes was rejected.", response.StatusCode, 0, response.ActivityId, response.RequestCharge);
            }
        }
    }
}
