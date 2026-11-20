using System.Text.Json;
using System.Text.Json.Nodes;
using Escribot.Api.Api;
using Escribot.Api.Common;
using Escribot.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Escribot.Api.Tests;

public class IeltsTests
{
    [Theory]
    [InlineData(6.0, 6.0)]
    [InlineData(6.24, 6.0)]
    [InlineData(6.25, 6.5)]
    [InlineData(6.5, 6.5)]
    [InlineData(6.74, 6.5)]
    [InlineData(6.75, 7.0)]
    [InlineData(-1, 0)]
    [InlineData(9.6, 9)]
    public void Bands_round_to_the_nearest_half_with_quarters_going_up(double value, double expected)
    {
        Assert.Equal(expected, Ielts.RoundBand(value));
    }

    [Theory]
    [InlineData(6.0, 7.0, 6.5)] // 6.67
    [InlineData(7.0, 6.0, 6.5)] // 6.33
    [InlineData(5.5, 6.5, 6.0)] // 6.17
    [InlineData(6.5, 7.5, 7.0)] // 7.17
    [InlineData(8.0, 8.0, 8.0)]
    [InlineData(6.0, 6.5, 6.5)] // 6.33
    public void Task_2_counts_twice_in_the_combined_band(double task1, double task2, double expected)
    {
        Assert.Equal(expected, Ielts.WeightedExamBand(task1, task2));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    [InlineData("one", 1)]
    [InlineData("  The chart   shows\nthree\ttrends. ", 5)]
    [InlineData("well-known state-of-the-art", 2)]
    [InlineData("non breaking space", 3)]
    public void Words_are_counted_between_any_white_space(string? text, int expected)
    {
        Assert.Equal(expected, Ielts.CountWords(text));
    }

    [Fact]
    public void Task_minimums_and_levels_match_the_exam()
    {
        Assert.Equal(150, Ielts.MinimumWords("task1"));
        Assert.Equal(250, Ielts.MinimumWords("task2"));
        Assert.Equal(20, Ielts.RecommendedMinutes("task1"));
        Assert.Equal(40, Ielts.RecommendedMinutes("task2"));
        Assert.Equal("beginner", Ielts.LevelFromBand(5.5));
        Assert.Equal("intermediate", Ielts.LevelFromBand(6.5));
        Assert.Equal("advanced", Ielts.LevelFromBand(7));
        Assert.Equal("intermediate", Ielts.LevelFromBand(null));
        Assert.True(Ielts.IsDrillLevel("advanced"));
        Assert.False(Ielts.IsDrillLevel("expert"));
    }
}

public class ChartSpecNormalizerTests
{
    private static JsonObject? Normalize(string json) => ChartSpecNormalizer.Normalize(JsonNode.Parse(json));

    [Fact]
    public void A_well_formed_chart_keeps_its_content_in_the_expected_shape()
    {
        var result = Normalize("""
            {
              "kind": "line", "title": " Rail use ", "subtitle": "By region", "xAxisKey": "year", "yAxisUnit": "%",
              "series": [{ "key": "north", "label": "North", "color": "#123456" }, { "key": "south", "label": "South" }],
              "data": [{ "year": 2000, "north": 10, "south": "1,250" }, { "year": 2010, "north": "45%", "south": 20 }],
              "sourceNote": "Synthetic"
            }
            """)!;

        Assert.Equal(
            """{"kind":"line","title":"Rail use","subtitle":"By region","xAxisKey":"year","yAxisUnit":"%","series":[{"key":"north","label":"North","color":"#123456"},{"key":"south","label":"South","color":"#c55e1b"}],"data":[{"year":2000,"north":10,"south":1250},{"year":2010,"north":45,"south":20}],"sourceNote":"Synthetic"}""",
            result.ToJsonString());
    }

    [Theory]
    [InlineData("LineChart", "line")]
    [InlineData("line_chart", "line")]
    [InlineData("column", "bar")]
    [InlineData("BarChart", "bar")]
    [InlineData("composed", "mixed")]
    public void Chart_kinds_are_recognised_under_their_common_names(string given, string expected)
    {
        var result = Normalize($$"""{ "type": "{{given}}", "data": [{ "x": "a", "v": 1 }, { "x": "b", "v": 2 }] }""")!;

        Assert.Equal(expected, result["kind"]!.GetValue<string>());
        Assert.Equal("Data Chart", result["title"]!.GetValue<string>());
    }

    [Fact]
    public void Labels_and_datasets_are_turned_into_rows()
    {
        var result = Normalize("""
            {
              "chartType": "bar", "unit": "tonnes",
              "labels": ["2019", "2020", "2021"],
              "datasets": [{ "label": "Paper", "data": [1, 2, 3] }, { "name": "Glass", "data": [4, null, 6] }]
            }
            """)!;

        Assert.Equal("label", result["xAxisKey"]!.GetValue<string>());
        Assert.Equal("tonnes", result["yAxisUnit"]!.GetValue<string>());
        Assert.Equal(
            """[{"label":"2019","s1":1,"s2":4},{"label":"2020","s1":2},{"label":"2021","s1":3,"s2":6}]""",
            result["data"]!.ToJsonString());
        Assert.Equal(new[] { "Paper", "Glass" }, result["series"]!.AsArray().Select(s => s!["label"]!.GetValue<string>()));
    }

    [Fact]
    public void Without_a_series_list_every_numeric_column_becomes_a_series()
    {
        var result = Normalize("""{ "kind": "line", "data": [{ "period": "Q1", "unit_sales": 5, "note": "n/a" }, { "period": "Q2", "unit_sales": 7, "note": "n/a" }] }""")!;

        Assert.Equal("period", result["xAxisKey"]!.GetValue<string>());
        var series = Assert.Single(result["series"]!.AsArray());
        Assert.Equal("unit_sales", series!["key"]!.GetValue<string>());
        Assert.Equal("Unit Sales", series["label"]!.GetValue<string>());
    }

    [Fact]
    public void Series_are_matched_to_columns_ignoring_case_and_by_label_and_empty_ones_are_dropped()
    {
        var result = Normalize("""
            {
              "kind": "mixed",
              "series": [
                { "dataKey": "NORTH", "name": "North", "type": "bar", "yAxisId": "right" },
                { "key": "missing", "label": "south", "type": "area" },
                { "key": "empty", "label": "Empty" },
                "West"
              ],
              "data": [{ "year": "2000", "north": 1, "south": 2, "empty": "n/a", "west": 3 }, { "year": "2010", "north": 4, "south": 5, "empty": null, "west": 6 }]
            }
            """)!;

        Assert.Equal(
            """[{"key":"north","label":"North","type":"bar","yAxisId":"right","color":"#16656e"},{"key":"south","label":"south","color":"#c55e1b"},{"key":"west","label":"West","color":"#475569"}]""",
            result["series"]!.ToJsonString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"line\"")]
    [InlineData("""{ "kind": "scatter", "data": [{ "x": 1, "y": 2 }, { "x": 2, "y": 3 }] }""")]
    [InlineData("""{ "kind": "line", "data": [{ "x": 1, "y": 2 }] }""")]
    [InlineData("""{ "kind": "line", "data": [{ "x": 1 }, { "x": 2 }] }""")]
    [InlineData("""{ "kind": "line", "data": [{ "x": "a", "y": "b" }, { "x": "c", "y": "d" }] }""")]
    public void Input_that_cannot_be_a_chart_is_rejected(string json)
    {
        Assert.Null(Normalize(json));
    }

    [Theory]
    [InlineData("pie")]
    [InlineData("table")]
    [InlineData("process")]
    [InlineData("map")]
    [InlineData("composite")]
    public void Other_kinds_of_visual_pass_through_unchanged(string kind)
    {
        var json = $$"""{"kind":"{{kind}}","title":"Synthetic","anything":[1,2,3]}""";

        Assert.Equal(json, Normalize(json)!.ToJsonString());
    }
}

public class ApiKeyProtectorTests
{
    private static ApiKeyProtector Protector(string? secret = null) =>
        new(Options.Create(new AppOptions { EncryptionSecret = secret ?? TestSecrets.NewSecret() }));

    [Fact]
    public void A_key_round_trips_and_is_not_readable_in_its_stored_form()
    {
        var protector = Protector();
        var key = $"sk-{TestSecrets.NewSecret()}";

        var stored = protector.Encrypt(key);

        Assert.Equal(key, protector.Decrypt(stored));
        Assert.DoesNotContain(key, stored);
        Assert.Equal(3, stored.Split(':').Length);
        // A fresh nonce every time.
        Assert.NotEqual(stored, protector.Encrypt(key));
    }

    [Fact]
    public void A_value_altered_or_encrypted_with_another_secret_does_not_decrypt()
    {
        var protector = Protector();
        var stored = protector.Encrypt("a-provider-key");
        var parts = stored.Split(':');
        var altered = Convert.FromBase64String(parts[2]);
        altered[0] ^= 1;

        Assert.Null(Protector().Decrypt(stored));
        Assert.Null(protector.Decrypt($"{parts[0]}:{parts[1]}:{Convert.ToBase64String(altered)}"));
        Assert.Null(protector.Decrypt("not:a:stored-value"));
        Assert.Null(protector.Decrypt("only-one-part"));
        Assert.Null(protector.Decrypt(null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("12345", null)]
    [InlineData("abcdefgh", "abc••••gh")]
    [InlineData("  sk-or-v1-abcdefghijklmnop  ", "sk-or••••••mnop")]
    public void A_saved_key_is_shown_only_by_its_ends(string? key, string? expected)
    {
        Assert.Equal(expected, ApiKeyProtector.Mask(key));
    }
}

public class DataApiUnitTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void A_data_url_gives_the_type_and_bytes()
    {
        var (contentType, data) = ImageStore.ParseDataUrl($"data:IMAGE/PNG;base64,{Png[..40]}\n{Png[40..]}");

        Assert.Equal("image/png", contentType);
        Assert.Equal(Convert.FromBase64String(Png), data);
    }

    [Fact]
    public async Task A_file_whose_description_cannot_be_saved_is_removed_again()
    {
        var files = new FakeFileStore();
        var store = new ImageStore(new FailingImages(), files, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageStore>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => store.StoreAsync("user-1", "free", $"data:image/png;base64,{Png}"));

        Assert.Empty(files.Saved);
        Assert.StartsWith("users/user-1/images/", Assert.Single(files.Deleted));
    }

    private sealed class FailingImages : Escribot.Api.Data.IImageRepository
    {
        public Task<long> GetTotalBytesAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task SaveAsync(string userId, Escribot.Api.Data.Models.ImageMetadata image, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("The database did not answer.");

        public Task<Escribot.Api.Data.Models.ImageMetadata?> GetAsync(string userId, string imageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<Escribot.Api.Data.Models.ImageMetadata>> ListAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string userId, string imageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("https://example.test/image.png")]
    [InlineData("data:image/png;base64,")]
    [InlineData("data:image/png;base64,!!!!")]
    [InlineData("data:image/png;base64,abc")]
    [InlineData("data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=")]
    [InlineData("data:text/html;base64,PGI+eDwvYj4=")]
    [InlineData("data:application/pdf;base64,JVBERi0=")]
    public void Anything_but_a_png_jpeg_webp_or_gif_data_url_is_refused(string dataUrl)
    {
        var exception = Assert.Throws<ImageUploadException>(() => ImageStore.ParseDataUrl(dataUrl));

        Assert.Equal(400, exception.StatusCode);
    }

    [Theory]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0x01 })]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01 })]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 })]
    public void Valid_magic_bytes_are_accepted_for_each_supported_image_format(string mimeType, byte[] bytes)
    {
        var (contentType, data) = ImageStore.ParseDataUrl($"data:{mimeType};base64,{Convert.ToBase64String(bytes)}");

        Assert.Equal(mimeType, contentType);
        Assert.Equal(bytes, data);
    }

    [Theory]
    [InlineData("image/png", new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x3E })] // <svg>
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })] // truncated PNG header
    [InlineData("image/jpeg", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })] // PNG spoofed as JPEG
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61 })] // GIF88a (invalid)
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45 })] // RIFF WAVE
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42 })] // truncated WEBP
    public void Spoofed_or_non_image_bytes_are_refused(string mimeType, byte[] bytes)
    {
        var exception = Assert.Throws<ImageUploadException>(() =>
            ImageStore.ParseDataUrl($"data:{mimeType};base64,{Convert.ToBase64String(bytes)}"));

        Assert.Equal(400, exception.StatusCode);
    }

    [Theory]
    [InlineData(null, false, false, "free")]
    [InlineData("free", false, false, "free")]
    [InlineData("pro", false, false, "pro")]
    [InlineData("free", false, true, "pro")]
    [InlineData("owner", false, false, "free")]
    [InlineData("free", true, false, "owner")]
    [InlineData("pro", true, true, "owner")]
    public void The_plan_is_owner_then_pro_then_free(string? storedPlan, bool isOwner, bool hasPaidPro, string expected)
    {
        Assert.Equal(expected, PlanResolver.Resolve(storedPlan, isOwner, hasPaidPro));
    }

    [Fact]
    public void Storage_allowances_are_25_mb_250_mb_and_unlimited()
    {
        Assert.Equal(25L * 1024 * 1024, Plans.Limits["free"].StorageBytes);
        Assert.Equal(250L * 1024 * 1024, Plans.Limits["pro"].StorageBytes);
        Assert.Null(Plans.Limits["owner"].StorageBytes);
    }

    [Theory]
    [InlineData("openrouter", "anthropic/some-model", "anthropic/some-model")]
    [InlineData("openrouter", "plain-model", "default")]
    [InlineData("openai", "gpt-custom", "gpt-custom")]
    [InlineData("openai", "anthropic/some-model", "default")]
    [InlineData("openai", null, "default")]
    [InlineData("openai", "", "default")]
    public void A_model_name_of_the_wrong_kind_for_the_provider_falls_back_to_the_default(string provider, string? model, string expected)
    {
        Assert.Equal(expected, AiProviders.ModelFor(provider, model, "default"));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_backup_file_is_read_row_by_row_and_unknown_fields_are_ignored()
    {
        var file = BackupFile.Parse(Json("""
            {
              "data": {
                "prompts": [{ "id": "p1", "userId": "someone-else", "taskType": "task1", "category": "bar", "topic": "t", "title": "T", "promptText": "Describe.",
                              "chartSpecJson": "{\"kind\":\"bar\"}", "source": "curated", "createdAt": "2026-03-01T09:00:00.000Z" }],
                "sessions": [{ "id": "s1", "mode": "coach", "startedAt": "2026-03-01T09:00:00Z" }],
                "vaultItems": [{ "id": "v1", "itemType": "error", "criterion": "LR", "originalText": "a", "upgradedText": "b", "explanation": "c", "exampleSentence": "d",
                                 "masteryLevel": 9, "createdAt": "2026-03-01T09:00:00.000Z" }]
              }
            }
            """), 100)!;

        var prompt = Assert.Single(file.Prompts);
        Assert.Null(prompt.UserId);
        Assert.Equal("custom", prompt.Source);
        Assert.Equal("bar", prompt.ChartSpec!.Value.GetProperty("kind").GetString());
        var session = Assert.Single(file.Sessions);
        Assert.Equal("in_progress", session.Status);
        Assert.Equal(0, session.TotalSecondsSpent);
        Assert.Equal(3, Assert.Single(file.VaultItems).MasteryLevel);
        Assert.Empty(file.Drafts);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "data": [] }""")]
    [InlineData("""{ "data": { "prompts": {} } }""")]
    [InlineData("""{ "data": { "prompts": ["text"] } }""")]
    [InlineData("""{ "data": { "sessions": [{ "id": 5, "mode": "coach", "startedAt": "2026-03-01T09:00:00Z" }] } }""")]
    [InlineData("""{ "data": { "sessions": [{ "id": "s", "mode": "coach" }] } }""")]
    [InlineData("""{ "data": { "sessions": [{ "id": "s", "mode": "coach", "startedAt": "not a date" }] } }""")]
    [InlineData("""{ "data": { "sessions": [{ "id": "s", "mode": "coach", "startedAt": "2026-03-01T09:00:00Z", "totalSecondsSpent": "many" }] } }""")]
    [InlineData("""{ "data": { "prompts": [{ "id": "p", "taskType": "task3", "category": "c", "topic": "t", "title": "T", "promptText": "x", "createdAt": "2026-03-01T09:00:00Z" }] } }""")]
    [InlineData("""{ "data": { "prompts": [{ "id": "p", "taskType": "task1", "category": "c", "topic": "t", "title": "T", "promptText": "x", "chartSpecJson": "{not json", "createdAt": "2026-03-01T09:00:00Z" }] } }""")]
    public void A_malformed_backup_file_is_refused_as_a_whole(string json)
    {
        Assert.Null(BackupFile.Parse(Json(json), 100));
    }

    [Fact]
    public void A_backup_file_with_too_many_rows_is_refused()
    {
        var sessions = string.Join(",", Enumerable.Range(0, 6).Select(i => $$"""{ "id": "s{{i}}", "mode": "coach", "startedAt": "2026-03-01T09:00:00Z" }"""));

        Assert.NotNull(BackupFile.Parse(Json($$"""{ "data": { "sessions": [{{sessions}}] } }"""), 6));
        Assert.Null(BackupFile.Parse(Json($$"""{ "data": { "sessions": [{{sessions}}] } }"""), 5));
    }
}
