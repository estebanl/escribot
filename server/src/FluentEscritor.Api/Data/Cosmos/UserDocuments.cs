using System.Text.Json;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Data.Models;
using Microsoft.Azure.Cosmos;

namespace FluentEscritor.Api.Data.Cosmos;

/// <summary>
/// How a model record is stored in the users container.
/// One-per-user documents use the type name as their id; the others use <c>type:id</c>.
/// </summary>
internal sealed record DocumentType<T>(string Name, Func<T, string>? GetId)
{
    public bool IsSingleton => GetId is null;

    public string DocumentId(string? id) => IsSingleton ? Name : $"{Name}:{id}";
}

internal static class DocumentTypes
{
    public static readonly DocumentType<UserAccount> Account = new("account", null);
    public static readonly DocumentType<UserSettings> Settings = new("settings", null);
    public static readonly DocumentType<Subscription> Subscription = new("subscription", null);
    public static readonly DocumentType<Prompt> Prompt = new("prompt", p => p.Id);
    public static readonly DocumentType<WritingSession> Session = new("session", s => s.Id);
    public static readonly DocumentType<Draft> Draft = new("draft", d => d.Id);
    public static readonly DocumentType<Evaluation> Evaluation = new("evaluation", e => e.Id);
    public static readonly DocumentType<VaultItem> VaultItem = new("vaultItem", v => v.Id);
    public static readonly DocumentType<MicroDrill> Drill = new("drill", d => d.Id);
    public static readonly DocumentType<AiUsageRecord> AiUsage = new("aiUsage", u => u.Id);
    public static readonly DocumentType<ImageMetadata> Image = new("image", i => i.Id);
}

/// <summary>
/// Typed access to the users container. Every operation names the user and is confined
/// to that user's partition, which is what keeps one user's data from another.
/// </summary>
internal sealed class UserDocuments(CosmosStore store)
{
    private Container Container => store.Users;

    public async Task<T?> GetAsync<T>(DocumentType<T> type, string userId, string? id, CancellationToken cancellationToken)
        where T : class
    {
        if (!DocumentId.IsValid(userId) || (!type.IsSingleton && !DocumentId.IsValid(id)))
        {
            return null;
        }

        var document = await CosmosStore.ReadAsync(Container, userId, type.DocumentId(id), cancellationToken);
        return document is null ? null : FromDocument(type, document);
    }

    public Task SaveAsync<T>(DocumentType<T> type, string userId, T value, CancellationToken cancellationToken) =>
        CosmosStore.UpsertAsync(Container, DocumentId.Require(userId, nameof(userId)), ToDocument(type, userId, value), cancellationToken);

    /// <summary>Returns false if the document already exists.</summary>
    public Task<bool> CreateAsync<T>(DocumentType<T> type, string userId, T value, CancellationToken cancellationToken) =>
        CosmosStore.CreateAsync(Container, DocumentId.Require(userId, nameof(userId)), ToDocument(type, userId, value), cancellationToken);

    public async Task SaveManyAsync<T>(DocumentType<T> type, string userId, IReadOnlyList<T> values, CancellationToken cancellationToken)
    {
        DocumentId.Require(userId, nameof(userId));
        if (values.Count > CosmosStore.MaxBatchSize)
        {
            throw new ArgumentException($"At most {CosmosStore.MaxBatchSize} documents can be saved together.", nameof(values));
        }

        var documents = values.Select(v => ToDocument(type, userId, v)).ToList();
        var streams = new List<Stream>();
        try
        {
            await CosmosStore.BatchAsync(Container, userId, documents, (batch, document) =>
            {
                var stream = DocumentJson.ToStream(document);
                streams.Add(stream);
                batch.UpsertItemStream(stream);
            }, cancellationToken);
        }
        finally
        {
            streams.ForEach(s => s.Dispose());
        }
    }

    public async Task<bool> DeleteAsync<T>(DocumentType<T> type, string userId, string? id, CancellationToken cancellationToken)
    {
        if (!DocumentId.IsValid(userId) || (!type.IsSingleton && !DocumentId.IsValid(id)))
        {
            return false;
        }

        return await CosmosStore.DeleteAsync(Container, userId, type.DocumentId(id), cancellationToken);
    }

    /// <summary>See <see cref="CosmosStore.UpdateAsync"/>. Returning the same instance from <paramref name="change"/> skips the write.</summary>
    public async Task<T?> UpdateAsync<T>(DocumentType<T> type, string userId, string? id, Func<T, T> change, CancellationToken cancellationToken)
        where T : class
    {
        if (!DocumentId.IsValid(userId) || (!type.IsSingleton && !DocumentId.IsValid(id)))
        {
            return null;
        }

        T? result = null;
        var found = await CosmosStore.UpdateAsync(Container, userId, type.DocumentId(id), document =>
        {
            var current = FromDocument(type, document);
            result = change(current);
            return ReferenceEquals(result, current) || result.Equals(current) ? null : ToDocument(type, userId, result);
        }, cancellationToken);

        return found is null ? null : result;
    }

    /// <summary>
    /// The user's documents of one type. <paramref name="filter"/> is a condition on the
    /// alias <c>c</c>; values go in <paramref name="parameters"/>, never in the text.
    /// </summary>
    public async Task<IReadOnlyList<T>> ListAsync<T>(
        DocumentType<T> type,
        string userId,
        string? orderBy,
        CancellationToken cancellationToken,
        string? filter = null,
        params (string Name, object Value)[] parameters)
    {
        if (!DocumentId.IsValid(userId))
        {
            return [];
        }

        var sql = "SELECT * FROM c WHERE c.type = @type"
            + (filter is null ? string.Empty : $" AND ({filter})")
            + (orderBy is null ? string.Empty : $" ORDER BY {orderBy}");
        var documents = await QueryAsync<JsonObject>(userId, sql, cancellationToken, [("@type", type.Name), .. parameters]);
        return documents.Select(d => FromDocument(type, d)).ToList();
    }

    /// <summary>Any query confined to the user's partition.</summary>
    public async Task<List<TResult>> QueryAsync<TResult>(string userId, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        if (!DocumentId.IsValid(userId))
        {
            return [];
        }

        return await CosmosStore.QueryAsync<TResult>(Container, userId, Query(sql, parameters), cancellationToken);
    }

    public static QueryDefinition Query(string sql, params (string Name, object Value)[] parameters)
    {
        var query = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
        {
            query = query.WithParameter(name, value);
        }

        return query;
    }

    /// <summary>
    /// Saves documents of any types for one user. Each batch is one transaction and stays
    /// within what Cosmos DB accepts: 100 operations and, with a margin, 2 MB.
    /// </summary>
    public async Task SaveBatchesAsync(string userId, IReadOnlyList<JsonObject> documents, CancellationToken cancellationToken)
    {
        const int MaxBatchBytes = 1_500_000;
        DocumentId.Require(userId, nameof(userId));

        var batch = new List<byte[]>();
        var batchBytes = 0;
        foreach (var document in documents)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            if (batch.Count > 0 && (batch.Count == CosmosStore.MaxBatchSize || batchBytes + bytes.Length > MaxBatchBytes))
            {
                await FlushAsync();
            }

            batch.Add(bytes);
            batchBytes += bytes.Length;
        }

        if (batch.Count > 0)
        {
            await FlushAsync();
        }

        async Task FlushAsync()
        {
            var streams = new List<Stream>();
            try
            {
                await CosmosStore.BatchAsync(Container, userId, batch, (transaction, bytes) =>
                {
                    var stream = new MemoryStream(bytes);
                    streams.Add(stream);
                    transaction.UpsertItemStream(stream);
                }, cancellationToken);
            }
            finally
            {
                streams.ForEach(s => s.Dispose());
            }

            batch.Clear();
            batchBytes = 0;
        }
    }

    public static JsonObject ToDocument<T>(DocumentType<T> type, string userId, T value)
    {
        var document = DocumentJson.ToNode(value);
        var id = type.GetId?.Invoke(value);
        if (!type.IsSingleton)
        {
            DocumentId.Require(id, "id");
        }

        // The storage fields always win over whatever the record carried.
        document["id"] = type.DocumentId(id);
        document["userId"] = userId;
        document["type"] = type.Name;
        return document;
    }

    public static T FromDocument<T>(DocumentType<T> type, JsonObject document)
    {
        // Give the record back its own id instead of the stored "type:id".
        var userId = document["userId"]!.GetValue<string>();
        var storedId = document["id"]!.GetValue<string>();
        document["id"] = type.IsSingleton ? userId : storedId[(type.Name.Length + 1)..];
        return document.Deserialize<T>(DocumentJson.Options)!;
    }
}
