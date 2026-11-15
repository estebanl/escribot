using System.Text.Json.Nodes;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Data.Cosmos;

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

    public async Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        DocumentId.Require(userId, nameof(userId));

        // Lookups first: once they are gone nobody can sign in as, or be billed as, this
        // user, even if a later step fails and the deletion has to be run again.
        await lookups.DeleteAllForUserAsync(userId, null, cancellationToken);

        // Whatever is in the partition goes, including document types added later.
        var ids = await documents.QueryAsync<string>(userId, "SELECT VALUE c.id FROM c", cancellationToken);
        await CosmosStore.BatchAsync(store.Users, userId, ids, (batch, id) => batch.DeleteItem(id), cancellationToken);

        await errors.DetachUserAsync(userId, cancellationToken);
    }
}
