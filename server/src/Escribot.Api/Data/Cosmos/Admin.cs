using System.Text.Json.Nodes;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Data.Cosmos;

/// <summary>
/// The only queries that read the users container across partitions. They cost more
/// than a single-partition read, which is acceptable for an admin page opened rarely.
/// </summary>
internal sealed class CosmosAdminRepository(CosmosStore store, TimeProvider time) : IAdminRepository
{
    public async Task<AccountStats> GetAccountStatsAsync(CancellationToken cancellationToken = default)
    {
        var now = DocumentJson.Now(time);
        var rows = await QueryAsync(
            "SELECT c.emailVerified, c.createdAt FROM c WHERE c.type = @type",
            cancellationToken,
            ("@type", DocumentTypes.Account.Name));

        var week = DocumentJson.Timestamp(now.AddDays(-7));
        var month = DocumentJson.Timestamp(now.AddDays(-30));
        return new AccountStats(
            rows.Count,
            rows.Count(r => r["emailVerified"]?.GetValue<bool>() == true),
            rows.Count(r => string.CompareOrdinal(r["createdAt"]!.GetValue<string>(), week) >= 0),
            rows.Count(r => string.CompareOrdinal(r["createdAt"]!.GetValue<string>(), month) >= 0));
    }

    public async Task<IReadOnlyList<AdminAccountRow>> ListAccountsAsync(string? emailContains, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var search = emailContains?.Trim().ToLowerInvariant();
        var accounts = string.IsNullOrEmpty(search)
            ? await QueryAsync(
                "SELECT * FROM c WHERE c.type = @type ORDER BY c.createdAt DESC OFFSET 0 LIMIT @limit",
                cancellationToken,
                ("@type", DocumentTypes.Account.Name),
                ("@limit", limit))
            : await QueryAsync(
                "SELECT * FROM c WHERE c.type = @type AND CONTAINS(c.normalizedEmail, @search) ORDER BY c.createdAt DESC OFFSET 0 LIMIT @limit",
                cancellationToken,
                ("@type", DocumentTypes.Account.Name),
                ("@search", search),
                ("@limit", limit));

        if (accounts.Count == 0)
        {
            return [];
        }

        var userIds = accounts.Select(a => a["userId"]!.GetValue<string>()).ToArray();
        var extras = await QueryAsync(
            "SELECT c.userId, c.type, c.plan, c.status FROM c WHERE ARRAY_CONTAINS(@types, c.type) AND ARRAY_CONTAINS(@userIds, c.userId)",
            cancellationToken,
            ("@types", new[] { DocumentTypes.Settings.Name, DocumentTypes.Subscription.Name }),
            ("@userIds", userIds));

        string? Extra(string userId, string type, string property) => extras
            .FirstOrDefault(e => e["userId"]!.GetValue<string>() == userId && e["type"]!.GetValue<string>() == type)?[property]?.GetValue<string>();

        return accounts
            .Select(a => UserDocuments.FromDocument(DocumentTypes.Account, a))
            .Select(a => new AdminAccountRow(
                a.Id,
                a.Name,
                a.Email,
                a.EmailVerified,
                a.CreatedAt,
                Extra(a.Id, DocumentTypes.Settings.Name, "plan"),
                Extra(a.Id, DocumentTypes.Subscription.Name, "status")))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, int>> GetSubscriptionStatusCountsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            "SELECT c.status FROM c WHERE c.type = @type AND IS_DEFINED(c.status)",
            cancellationToken,
            ("@type", DocumentTypes.Subscription.Name));
        return rows.GroupBy(r => r["status"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<IReadOnlyList<AiUsageTotal>> GetAiUsageTotalsAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            "SELECT c.plan, c.keySource, COUNT(1) AS calls, SUM(c.costUsd) AS cost FROM c WHERE c.type = @type AND c.createdAt >= @since GROUP BY c.plan, c.keySource",
            cancellationToken,
            ("@type", DocumentTypes.AiUsage.Name),
            ("@since", DocumentJson.Timestamp(since)));
        return rows
            .Select(r => new AiUsageTotal(
                r["plan"]?.GetValue<string>(),
                r["keySource"]!.GetValue<string>(),
                r["cost"]?.GetValue<double>() ?? 0,
                r["calls"]!.GetValue<int>()))
            .ToList();
    }

    public async Task<int> CountAiCallsAsync(DateTimeOffset since, IReadOnlyCollection<string> features, CancellationToken cancellationToken = default)
    {
        var counts = await CosmosStore.QueryAsync<int>(
            store.Users,
            null,
            UserDocuments.Query(
                "SELECT VALUE COUNT(1) FROM c WHERE c.type = @type AND c.createdAt >= @since AND ARRAY_CONTAINS(@features, c.feature)",
                ("@type", DocumentTypes.AiUsage.Name),
                ("@since", DocumentJson.Timestamp(since)),
                ("@features", features.ToArray())),
            cancellationToken);
        return counts.Sum();
    }

    public async Task<IReadOnlyList<UserAiUsage>> GetAiUsageByUserAsync(IReadOnlyCollection<string> userIds, DateTimeOffset since, IReadOnlyCollection<string> countedFeatures, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var rows = await QueryAsync(
            "SELECT c.userId, c.feature, c.keySource, c.costUsd FROM c WHERE c.type = @type AND c.createdAt >= @since AND ARRAY_CONTAINS(@userIds, c.userId)",
            cancellationToken,
            ("@type", DocumentTypes.AiUsage.Name),
            ("@since", DocumentJson.Timestamp(since)),
            ("@userIds", userIds.ToArray()));

        return rows
            .GroupBy(r => r["userId"]!.GetValue<string>())
            .Select(g => new UserAiUsage(
                g.Key,
                g.Count(r => countedFeatures.Contains(r["feature"]!.GetValue<string>())),
                g.Where(r => r["keySource"]?.GetValue<string>() == "platform").Sum(r => r["costUsd"]?.GetValue<double>() ?? 0)))
            .ToList();
    }

    private Task<List<JsonObject>> QueryAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) =>
        CosmosStore.QueryAsync<JsonObject>(store.Users, null, UserDocuments.Query(sql, parameters), cancellationToken);
}
