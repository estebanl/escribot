using System.Reflection;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Cosmos;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Tests;

/// <summary>Parts of the data layer that need no database.</summary>
public class DataLayerUnitTests
{
    [Fact]
    public void Shipped_curated_prompts_are_complete_and_original()
    {
        var seeds = CuratedPromptSeed.LoadEmbedded();

        Assert.Equal(14, seeds.Count);
        Assert.Equal(seeds.Count, seeds.Select(s => s.Prompt.Id).Distinct().Count());
        Assert.Equal(seeds.Count, seeds.Select(s => s.Hash).Distinct().Count());
        Assert.All(seeds, s =>
        {
            Assert.True(DocumentId.IsValid(s.Prompt.Id));
            Assert.Contains(s.Prompt.TaskType, new[] { "task1", "task2" });
            Assert.False(string.IsNullOrWhiteSpace(s.Prompt.Title));
            Assert.False(string.IsNullOrWhiteSpace(s.Prompt.PromptText));
            Assert.Equal("curated", s.Prompt.Source);
            Assert.Null(s.Prompt.UserId);
            // Prompts transcribed from a published exam book were removed and must not return.
            Assert.DoesNotContain("ielts21", s.Prompt.Id, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(seeds.Where(s => s.Prompt.TaskType == "task1"), s => Assert.NotNull(s.Prompt.ChartSpec));
        Assert.Equal(seeds.Select(s => s.Hash), CuratedPromptSeed.LoadEmbedded().Select(s => s.Hash));
    }

    [Fact]
    public async Task Synchronizer_writes_only_what_differs()
    {
        var seeds = CuratedPromptSeed.LoadEmbedded();
        var store = new FakeCuratedStore();
        store.Hashes[seeds[0].Prompt.Id] = seeds[0].Hash;
        store.Hashes[seeds[1].Prompt.Id] = "changed";
        store.Hashes["retired"] = "x";
        var synchronizer = new CuratedPromptSynchronizer(store);

        Assert.Equal(seeds.Count, await synchronizer.SynchronizeAsync(seeds));
        Assert.DoesNotContain(seeds[0].Prompt.Id, store.Upserted);
        Assert.Contains(seeds[1].Prompt.Id, store.Upserted);
        Assert.Equal(["retired"], store.Deleted);
        Assert.Equal(0, await synchronizer.SynchronizeAsync(seeds));
    }

    // The sample credentials are made up when the test runs, so the repository holds
    // nothing shaped like a real key, not even a fake one.
    public static TheoryData<string, string> TextWithCredentials()
    {
        var value = TestSecrets.NewSecret().ToLowerInvariant();
        var shortValue = value[..20];
        var base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        string Part(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=');
        var webToken = string.Join('.', Part("""{"alg":"HS256"}"""), Part("""{"sub":"synthetic"}"""), shortValue);

        return new TheoryData<string, string>
        {
            { $"Authorization: Bearer {shortValue}", shortValue },
            { $"call failed with api_key={shortValue}", shortValue },
            { $$"""body {"password":"{{shortValue}}"} rejected""", shortValue },
            { $"provider key sk-or-v1-{shortValue} was refused", shortValue },
            { $"Stripe whsec_{shortValue} mismatch", shortValue },
            { $"AccountEndpoint=https://x.example.test/;AccountKey={base64};", base64 },
            { $"web token {webToken} seen", webToken },
            { $"Cookie: session={shortValue}", shortValue },
            { $"value {value} leaked", value },
        };
    }

    [Theory]
    [MemberData(nameof(TextWithCredentials))]
    public void Error_text_has_credentials_removed(string text, string secret)
    {
        var cleaned = ErrorSanitizer.Clean(text, 1000)!;

        Assert.DoesNotContain(secret, cleaned);
        Assert.Contains(ErrorSanitizer.Redacted, cleaned);
    }

    [Fact]
    public void Error_text_without_credentials_is_only_shortened()
    {
        const string Text = "TypeError: Cannot read properties of undefined (reading 'band') at EvaluationWorkbench (app.js:10:5)";

        Assert.Equal(Text, ErrorSanitizer.Clean(Text, 1000));
        Assert.Equal(Text[..20], ErrorSanitizer.Clean(Text, 20));
        Assert.Null(ErrorSanitizer.Clean(null, 20));
    }

    [Fact]
    public void Timestamps_are_written_in_one_sortable_utc_format()
    {
        var early = new DateTimeOffset(2026, 11, 15, 20, 4, 5, TimeSpan.FromHours(2));
        var late = early.AddMilliseconds(500);

        Assert.Equal("2026-11-15T18:04:05.000Z", DocumentJson.Timestamp(early));
        Assert.Equal("2026-11-15T18:04:05.500Z", DocumentJson.Timestamp(late));
        Assert.True(string.CompareOrdinal(DocumentJson.Timestamp(early), DocumentJson.Timestamp(late)) < 0);

        var json = DocumentJson.ToNode(new WritingSession { Id = "s", Mode = "coach", StartedAt = early });
        Assert.Equal("2026-11-15T18:04:05.000Z", json["startedAt"]!.GetValue<string>());
        Assert.False(json.ContainsKey("completedAt"));
    }

    [Fact]
    public void Lookup_keys_are_hashed_into_valid_ids()
    {
        var hash = DocumentId.Hash("Someone/Odd?#@example.test");

        Assert.Equal(64, hash.Length);
        Assert.True(DocumentId.IsValid(hash));
        Assert.Equal(hash, DocumentId.Hash("Someone/Odd?#@example.test"));
        Assert.NotEqual(hash, DocumentId.Hash("someone/odd?#@example.test"));
        Assert.False(DocumentId.IsValid(new string('a', 201)));
    }

    [Fact]
    public void The_cosmos_sdk_does_not_leak_out_of_the_data_layer()
    {
        var assembly = typeof(AppOptions).Assembly;
        static bool IsCosmos(Type t) => t.Namespace?.StartsWith("Microsoft.Azure.Cosmos", StringComparison.Ordinal) == true
            || (t.IsGenericType && t.GetGenericArguments().Any(IsCosmos))
            || (t.HasElementType && IsCosmos(t.GetElementType()!));

        // Everything that talks to Cosmos DB is internal to the API assembly...
        Assert.All(
            assembly.GetTypes().Where(t => t.Namespace == "FluentEscritor.Api.Data.Cosmos"),
            t => Assert.False(t.IsVisible, $"{t.Name} should not be public."));

        // ...and nothing public mentions an SDK type.
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var exposed = assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(Public).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Concat(t.GetProperties(Public).Select(p => p.PropertyType))
                .Concat(t.GetFields(Public).Select(f => f.FieldType))
                .Concat(t.GetInterfaces())
                .Append(t.BaseType ?? typeof(object)))
            .Where(IsCosmos);

        Assert.Empty(exposed);
        Assert.Contains(typeof(IWritingRepository), assembly.GetExportedTypes());
    }

    private sealed class FakeCuratedStore : ICuratedPromptStore
    {
        public Dictionary<string, string> Hashes { get; } = [];

        public List<string> Upserted { get; } = [];

        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> GetSeedHashesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Hashes));

        public Task UpsertAsync(Prompt prompt, string seedHash, CancellationToken cancellationToken = default)
        {
            Hashes[prompt.Id] = seedHash;
            Upserted.Add(prompt.Id);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string promptId, CancellationToken cancellationToken = default)
        {
            Hashes.Remove(promptId);
            Deleted.Add(promptId);
            return Task.CompletedTask;
        }
    }
}
