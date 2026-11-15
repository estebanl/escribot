using System.Text.Json;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Data.Cosmos;

internal sealed class CosmosPromptRepository(CosmosStore store, UserDocuments documents, TimeProvider time) : IPromptRepository, ICuratedPromptStore
{
    /// <summary>The one partition of the shared container that holds the curated prompts.</summary>
    private const string CuratedPartition = "curated-prompts";

    private const string CuratedSource = "curated";

    public async Task<IReadOnlyList<Prompt>> ListVisibleAsync(string userId, CancellationToken cancellationToken = default)
    {
        var curated = ListCuratedAsync(cancellationToken);
        var own = documents.ListAsync(DocumentTypes.Prompt, userId, "c.createdAt DESC", cancellationToken);
        await Task.WhenAll(curated, own);
        return [.. await curated, .. await own];
    }

    public async Task<Prompt?> GetVisibleAsync(string userId, string promptId, CancellationToken cancellationToken = default)
    {
        if (!DocumentId.IsValid(promptId))
        {
            return null;
        }

        var curated = await CosmosStore.ReadAsync(store.Shared, CuratedPartition, promptId, cancellationToken);
        return curated is not null
            ? FromCurated(curated)
            : await documents.GetAsync(DocumentTypes.Prompt, userId, promptId, cancellationToken);
    }

    public Task SaveAsync(string userId, Prompt prompt, CancellationToken cancellationToken = default)
    {
        if (prompt.Source == CuratedSource)
        {
            throw new ArgumentException("Curated prompts are written only by the startup seeding.", nameof(prompt));
        }

        var stored = prompt.CreatedAt == default ? prompt with { CreatedAt = DocumentJson.Now(time) } : prompt;
        return documents.SaveAsync(DocumentTypes.Prompt, userId, stored, cancellationToken);
    }

    public Task<bool> DeleteAsync(string userId, string promptId, CancellationToken cancellationToken = default) =>
        documents.DeleteAsync(DocumentTypes.Prompt, userId, promptId, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetSeedHashesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await CosmosStore.QueryAsync<JsonObject>(
            store.Shared,
            CuratedPartition,
            UserDocuments.Query("SELECT c.id, c.seedHash FROM c"),
            cancellationToken);
        return rows.ToDictionary(r => r["id"]!.GetValue<string>(), r => r["seedHash"]?.GetValue<string>() ?? string.Empty);
    }

    public Task UpsertAsync(Prompt prompt, string seedHash, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(prompt.Id, nameof(prompt));
        var document = DocumentJson.ToNode(prompt with { UserId = null, Source = CuratedSource, CreatedAt = DocumentJson.Now(time) });
        document["pk"] = CuratedPartition;
        document["seedHash"] = seedHash;
        return CosmosStore.UpsertAsync(store.Shared, CuratedPartition, document, cancellationToken);
    }

    Task ICuratedPromptStore.DeleteAsync(string promptId, CancellationToken cancellationToken) =>
        CosmosStore.DeleteAsync(store.Shared, CuratedPartition, promptId, cancellationToken);

    private async Task<List<Prompt>> ListCuratedAsync(CancellationToken cancellationToken)
    {
        var rows = await CosmosStore.QueryAsync<JsonObject>(
            store.Shared,
            CuratedPartition,
            UserDocuments.Query("SELECT * FROM c ORDER BY c.id"),
            cancellationToken);
        return rows.Select(FromCurated).ToList();
    }

    private static Prompt FromCurated(JsonObject document) => document.Deserialize<Prompt>(DocumentJson.Options)!;
}

internal sealed class CosmosWritingRepository(CosmosStore store, UserDocuments documents) : IWritingRepository
{
    public Task<WritingSession?> GetOwnedSessionAsync(string userId, string sessionId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.Session, userId, sessionId, cancellationToken);

    public async Task<SessionBundle?> GetSessionBundleAsync(string userId, string sessionId, CancellationToken cancellationToken = default)
    {
        var rows = await QuerySessionDocumentsAsync(userId, sessionId, "*", cancellationToken);
        var session = rows.Where(r => IsType(r, DocumentTypes.Session.Name)).Select(r => UserDocuments.FromDocument(DocumentTypes.Session, r)).SingleOrDefault();
        if (session is null)
        {
            return null;
        }

        return new SessionBundle(
            session,
            rows.Where(r => IsType(r, DocumentTypes.Draft.Name)).Select(r => UserDocuments.FromDocument(DocumentTypes.Draft, r)).OrderBy(d => d.CreatedAt).ToList(),
            rows.Where(r => IsType(r, DocumentTypes.Evaluation.Name)).Select(r => UserDocuments.FromDocument(DocumentTypes.Evaluation, r)).OrderBy(e => e.CreatedAt).ToList());
    }

    public Task<IReadOnlyList<WritingSession>> ListSessionsAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.Session, userId, "c.startedAt DESC", cancellationToken);

    public Task SaveSessionAsync(string userId, WritingSession session, CancellationToken cancellationToken = default) =>
        documents.SaveAsync(DocumentTypes.Session, userId, session, cancellationToken);

    public async Task<bool> DeleteSessionAsync(string userId, string sessionId, CancellationToken cancellationToken = default)
    {
        var ids = (await QuerySessionDocumentsAsync(userId, sessionId, "c.id", cancellationToken))
            .Select(r => r["id"]!.GetValue<string>())
            .ToList();
        if (!ids.Contains(DocumentTypes.Session.DocumentId(sessionId)))
        {
            return false;
        }

        // One partition, so the session and everything under it go in a single transaction.
        await CosmosStore.BatchAsync(store.Users, userId, ids, (batch, id) => batch.DeleteItem(id), cancellationToken);
        return true;
    }

    public Task<Draft?> GetDraftAsync(string userId, string draftId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.Draft, userId, draftId, cancellationToken);

    public Task<IReadOnlyList<Draft>> ListDraftsAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.Draft, userId, "c.createdAt DESC", cancellationToken);

    public Task SaveDraftAsync(string userId, Draft draft, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(draft.SessionId, nameof(draft));
        return documents.SaveAsync(DocumentTypes.Draft, userId, draft, cancellationToken);
    }

    public Task<IReadOnlyList<Evaluation>> ListEvaluationsAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.Evaluation, userId, "c.createdAt DESC", cancellationToken);

    public Task SaveEvaluationAsync(string userId, Evaluation evaluation, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(evaluation.SessionId, nameof(evaluation));
        return documents.SaveAsync(DocumentTypes.Evaluation, userId, evaluation, cancellationToken);
    }

    /// <summary>The session document plus the drafts and evaluations that carry its id.</summary>
    private async Task<List<JsonObject>> QuerySessionDocumentsAsync(string userId, string sessionId, string select, CancellationToken cancellationToken)
    {
        if (!DocumentId.IsValid(sessionId))
        {
            return [];
        }

        var projection = select == "*" ? "*" : $"{select}, c.type";
        return await documents.QueryAsync<JsonObject>(
            userId,
            $"SELECT {projection} FROM c WHERE c.id = @sessionDocumentId OR (c.sessionId = @sessionId AND c.type IN (@draft, @evaluation))",
            cancellationToken,
            ("@sessionDocumentId", DocumentTypes.Session.DocumentId(sessionId)),
            ("@sessionId", sessionId),
            ("@draft", DocumentTypes.Draft.Name),
            ("@evaluation", DocumentTypes.Evaluation.Name));
    }

    private static bool IsType(JsonObject document, string type) => document["type"]?.GetValue<string>() == type;
}

internal sealed class CosmosVaultRepository(UserDocuments documents) : IVaultRepository
{
    public Task<IReadOnlyList<VaultItem>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.VaultItem, userId, "c.createdAt DESC", cancellationToken);

    public Task<VaultItem?> GetAsync(string userId, string itemId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.VaultItem, userId, itemId, cancellationToken);

    public Task SaveAsync(string userId, VaultItem item, CancellationToken cancellationToken = default) =>
        documents.SaveAsync(DocumentTypes.VaultItem, userId, item, cancellationToken);

    public Task SaveManyAsync(string userId, IReadOnlyList<VaultItem> items, CancellationToken cancellationToken = default) =>
        documents.SaveManyAsync(DocumentTypes.VaultItem, userId, items, cancellationToken);

    public Task<bool> DeleteAsync(string userId, string itemId, CancellationToken cancellationToken = default) =>
        documents.DeleteAsync(DocumentTypes.VaultItem, userId, itemId, cancellationToken);
}

internal sealed class CosmosDrillRepository(UserDocuments documents) : IDrillRepository
{
    public Task<IReadOnlyList<MicroDrill>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.Drill, userId, "c.createdAt DESC", cancellationToken);

    public Task<MicroDrill?> GetAsync(string userId, string drillId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.Drill, userId, drillId, cancellationToken);

    public Task SaveAsync(string userId, MicroDrill drill, CancellationToken cancellationToken = default) =>
        documents.SaveAsync(DocumentTypes.Drill, userId, drill, cancellationToken);

    public Task<bool> DeleteAsync(string userId, string drillId, CancellationToken cancellationToken = default) =>
        documents.DeleteAsync(DocumentTypes.Drill, userId, drillId, cancellationToken);
}

internal sealed class CosmosAiUsageRepository(UserDocuments documents, TimeProvider time) : IAiUsageRepository
{
    public Task RecordAsync(string userId, AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        var stored = record.CreatedAt == default ? record with { CreatedAt = DocumentJson.Now(time) } : record;
        return documents.SaveAsync(DocumentTypes.AiUsage, userId, stored, cancellationToken);
    }

    public async Task<int> CountAsync(string userId, DateTimeOffset since, IReadOnlyCollection<string> features, string? keySource = null, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT VALUE COUNT(1) FROM c WHERE c.type = @type AND c.createdAt >= @since AND ARRAY_CONTAINS(@features, c.feature)"
            + (keySource is null ? string.Empty : " AND c.keySource = @keySource");
        List<(string, object)> parameters =
        [
            ("@type", DocumentTypes.AiUsage.Name),
            ("@since", DocumentJson.Timestamp(since)),
            ("@features", features.ToArray()),
        ];
        if (keySource is not null)
        {
            parameters.Add(("@keySource", keySource));
        }

        var counts = await documents.QueryAsync<int>(userId, sql, cancellationToken, [.. parameters]);
        return counts.Sum();
    }

    public Task<IReadOnlyList<AiUsageRecord>> ListSinceAsync(string userId, DateTimeOffset since, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.AiUsage, userId, "c.createdAt DESC", cancellationToken, "c.createdAt >= @since", ("@since", DocumentJson.Timestamp(since)));
}

internal sealed class CosmosImageRepository(UserDocuments documents, TimeProvider time) : IImageRepository
{
    public Task<ImageMetadata?> GetAsync(string userId, string imageId, CancellationToken cancellationToken = default) =>
        documents.GetAsync(DocumentTypes.Image, userId, imageId, cancellationToken);

    public Task<IReadOnlyList<ImageMetadata>> ListAsync(string userId, CancellationToken cancellationToken = default) =>
        documents.ListAsync(DocumentTypes.Image, userId, "c.createdAt DESC", cancellationToken);

    public Task SaveAsync(string userId, ImageMetadata image, CancellationToken cancellationToken = default)
    {
        var stored = image.CreatedAt == default ? image with { CreatedAt = DocumentJson.Now(time) } : image;
        return documents.SaveAsync(DocumentTypes.Image, userId, stored, cancellationToken);
    }

    public Task<bool> DeleteAsync(string userId, string imageId, CancellationToken cancellationToken = default) =>
        documents.DeleteAsync(DocumentTypes.Image, userId, imageId, cancellationToken);

    public async Task<long> GetTotalBytesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var totals = await documents.QueryAsync<long>(
            userId,
            "SELECT VALUE SUM(c.bytes) FROM c WHERE c.type = @type",
            cancellationToken,
            ("@type", DocumentTypes.Image.Name));
        return totals.Sum();
    }
}
