using System.Text.Json.Nodes;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Data.Cosmos;

internal sealed class CosmosUserDataRepository(CosmosStore store, UserDocuments documents, LookupStore lookups, CosmosErrorEventRepository errors) : IUserDataRepository
{
    public async Task<UserDataExport> ExportAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Everything a user owns is in one partition, so the export is a single query.
        var rows = await documents.QueryAsync<JsonObject>(userId, "SELECT * FROM c", cancellationToken);
        var byType = rows.ToLookup(r => r["type"]?.GetValue<string>());

        List<T> All<T>(DocumentType<T> type) => byType[type.Name].Select(r => UserDocuments.FromDocument(type, r)).ToList();

        var account = All(DocumentTypes.Account).SingleOrDefault();
        var settings = All(DocumentTypes.Settings).SingleOrDefault();

        return new UserDataExport
        {
            Account = account is null ? null : account with { PasswordHash = null, SecurityStamp = null },
            Settings = settings is null ? null : settings with { EncryptedApiKeys = new Dictionary<string, string>() },
            SavedApiKeyProviders = settings?.EncryptedApiKeys.Keys.Order().ToList() ?? [],
            Subscription = All(DocumentTypes.Subscription).SingleOrDefault(),
            Prompts = All(DocumentTypes.Prompt).OrderBy(p => p.CreatedAt).ToList(),
            Sessions = All(DocumentTypes.Session).OrderBy(s => s.StartedAt).ToList(),
            Drafts = All(DocumentTypes.Draft).OrderBy(d => d.CreatedAt).ToList(),
            Evaluations = All(DocumentTypes.Evaluation).OrderBy(e => e.CreatedAt).ToList(),
            VaultItems = All(DocumentTypes.VaultItem).OrderBy(v => v.CreatedAt).ToList(),
            MicroDrills = All(DocumentTypes.Drill).OrderBy(d => d.CreatedAt).ToList(),
            AiUsage = All(DocumentTypes.AiUsage).OrderBy(u => u.CreatedAt).ToList(),
            Images = All(DocumentTypes.Image).OrderBy(i => i.CreatedAt).ToList(),
        };
    }

    public Task ImportAsync(string userId, UserDataImport import, CancellationToken cancellationToken = default)
    {
        List<JsonObject> all =
        [
            // An imported prompt is always the user's own, whatever the file says.
            .. import.Prompts.Select(p => UserDocuments.ToDocument(DocumentTypes.Prompt, userId, p.Source == "curated" ? p with { Source = "custom", UserId = userId } : p with { UserId = userId })),
            .. import.Sessions.Select(s => UserDocuments.ToDocument(DocumentTypes.Session, userId, s)),
            .. import.Drafts.Select(d => UserDocuments.ToDocument(DocumentTypes.Draft, userId, d)),
            .. import.Evaluations.Select(e => UserDocuments.ToDocument(DocumentTypes.Evaluation, userId, e)),
            .. import.VaultItems.Select(v => UserDocuments.ToDocument(DocumentTypes.VaultItem, userId, v)),
            .. import.MicroDrills.Select(d => UserDocuments.ToDocument(DocumentTypes.Drill, userId, d)),
        ];

        return documents.SaveBatchesAsync(userId, all, cancellationToken);
    }

    public async Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(userId, nameof(userId));

        // The order keeps an interrupted deletion repeatable by the user themselves. Their
        // documents go first; while that is unfinished the account, its email lookup and
        // the session all still exist, so they can sign in and delete again. Then the
        // lookups, the email one last, and only then the account document.
        var accountId = DocumentTypes.Account.DocumentId(null);
        var ids = await documents.QueryAsync<string>(userId, "SELECT VALUE c.id FROM c", cancellationToken);

        // Whatever is in the partition goes, including document types added later.
        var others = ids.Where(id => id != accountId).ToList();
        await CosmosStore.BatchAsync(store.Users, userId, others, (batch, id) => batch.DeleteItem(id), cancellationToken);

        // Also before the account goes, for the same reason: if this fails the user can
        // still sign in and run the deletion again.
        await errors.DetachUserAsync(userId, cancellationToken);

        await lookups.DeleteAllForUserAsync(userId, null, cancellationToken);

        if (ids.Contains(accountId))
        {
            await CosmosStore.DeleteAsync(store.Users, userId, accountId, cancellationToken);
        }

    }
}
