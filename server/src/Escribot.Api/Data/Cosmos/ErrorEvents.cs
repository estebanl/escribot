using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Data.Cosmos;

/// <summary>
/// Error events are partitioned by UTC day and removed by the container's 30-day
/// time-to-live, so nothing has to clean them up.
/// </summary>
internal sealed class CosmosErrorEventRepository(CosmosStore store, TimeProvider time) : IErrorEventRepository
{
    public Task RecordAsync(ErrorEvent errorEvent, CancellationToken cancellationToken = default)
    {
        var now = DocumentJson.Now(time);
        var day = Day(now);
        var stored = ErrorSanitizer.Clean(errorEvent) with { Id = Guid.NewGuid().ToString("N"), CreatedAt = now };
        var document = DocumentJson.ToNode(stored);
        document["day"] = day;
        return CosmosStore.UpsertAsync(store.Errors, day, document, cancellationToken);
    }

    public async Task<IReadOnlyList<ErrorEvent>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var rows = await CosmosStore.QueryAsync<JsonObject>(
            store.Errors,
            null,
            UserDocuments.Query("SELECT * FROM c ORDER BY c.createdAt DESC OFFSET 0 LIMIT @limit", ("@limit", limit)),
            cancellationToken);
        return rows.Select(r => r.Deserialize<ErrorEvent>(DocumentJson.Options)!).ToList();
    }

    public async Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var counts = await CosmosStore.QueryAsync<int>(
            store.Errors,
            null,
            UserDocuments.Query("SELECT VALUE COUNT(1) FROM c WHERE c.createdAt >= @since", ("@since", DocumentJson.Timestamp(since))),
            cancellationToken);
        return counts.Sum();
    }

    /// <summary>Keeps the user's error reports for debugging, but no longer tied to the person.</summary>
    public async Task DetachUserAsync(string userId, CancellationToken cancellationToken)
    {
        var rows = await CosmosStore.QueryAsync<JsonObject>(
            store.Errors,
            null,
            UserDocuments.Query("SELECT c.id, c.day FROM c WHERE c.userId = @userId", ("@userId", userId)),
            cancellationToken);

        foreach (var row in rows)
        {
            await CosmosStore.UpdateAsync(store.Errors, row["day"]!.GetValue<string>(), row["id"]!.GetValue<string>(), document =>
            {
                document.Remove("userId");
                return document;
            }, cancellationToken);
        }
    }

    private static string Day(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Shortens an error and removes anything shaped like a credential before it is stored.
/// It cannot recognise essay text; callers keep that out of errors in the first place.
/// </summary>
internal static partial class ErrorSanitizer
{
    public const int MaxMessage = 1000;
    public const int MaxStack = 4000;
    public const int MaxPath = 500;
    public const int MaxDigest = 100;
    public const string Redacted = "[redacted]";

    public static ErrorEvent Clean(ErrorEvent e) => e with
    {
        Message = Clean(e.Message, MaxMessage) ?? string.Empty,
        Stack = Clean(e.Stack, MaxStack),
        // A path may carry a token in its query string, so only the path itself is kept.
        Path = Clean(e.Path?.Split('?', '#')[0], MaxPath),
        Digest = Clean(e.Digest, MaxDigest),
    };

    public static string? Clean(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        text = NamedSecret().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Redacted}");
        text = QueryKey().Replace(text, m => $"{m.Groups[1].Value}{Redacted}");
        text = BearerToken().Replace(text, $"Bearer {Redacted}");
        text = JsonWebToken().Replace(text, Redacted);
        text = PrefixedKey().Replace(text, Redacted);
        text = GoogleApiKey().Replace(text, Redacted);
        text = LongOpaqueValue().Replace(text, Redacted);
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    // api_key=..., "password": "...", AccountKey=..., Authorization: ..., Cookie: ..., key=..., "key": "..."
    // (a bare "key" counts only when no letter or digit precedes it, so "monkey=" is left alone;
    // ?key= and &key= are left to QueryKey).
    [GeneratedRegex("""((?:api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|secret|password|passwd|authorization|accountkey|sharedaccesssignature|sig|cookie|(?<![A-Za-z0-9?&])key)["']?)(\s*[=:]\s*["']?)(?:Bearer\s+)?[^\s"'&;,]+""", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();

    // Query-string key=... parameters (?key=..., &key=..., and their percent-encoded forms
    // %3Fkey%3D... and %26key%3D...). The value stops at the characters that end a query value
    // in prose, so text after it (", status 400", ")", "#fragment") is kept.
    [GeneratedRegex("""((?:[?&]|%3F|%26)key(?:=|%3D))[^\s"'&;,)#]+""", RegexOptions.IgnoreCase)]
    private static partial Regex QueryKey();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex JsonWebToken();

    // Provider and Stripe keys: sk-..., sk_live_..., pk_test_..., rk_..., whsec_...
    [GeneratedRegex(@"\b(?:sk|pk|rk|whsec)[-_][A-Za-z0-9_-]{12,}")]
    private static partial Regex PrefixedKey();

    // Google API keys (39 characters: AIza followed by 35 URL-safe characters). There are no
    // boundary anchors: a key can end in "-" (no \b after it) or follow a word character
    // (GEMINI_KEY_AIza...), and the 39 characters are too few for LongOpaqueValue to catch.
    // The open-ended count takes any longer run whole, so no tail of it is left behind.
    [GeneratedRegex(@"AIza[0-9A-Za-z_-]{35,}")]
    private static partial Regex GoogleApiKey();

    // Any other unbroken run of 40 or more key-like characters.
    [GeneratedRegex(@"[A-Za-z0-9+/_=-]{40,}")]
    private static partial Regex LongOpaqueValue();
}
