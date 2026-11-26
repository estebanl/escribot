using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Escribot.Api.Ai;
using Escribot.Api.Api;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Escribot.Api.Tests;

/// <summary>
/// Editing and deleting uploaded prompts (<c>PATCH</c> and <c>DELETE /api/prompts/{id}</c>),
/// and transforming a saved prompt's image to SVG. All content is synthetic.
/// </summary>
public class PromptEditingTests
{
    // A 1x1 pixel PNG, and a second one with different bytes.
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
    private const string OtherPng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static ApiFactory CreateFactory(FakeChatClientFactory? chatFactory = null, Action<IServiceCollection>? configure = null)
    {
        ApiFactory? factory = null;
        factory = new ApiFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["OPENROUTER_API_KEY"] = chatFactory is null ? null : "or-test-key",
            },
            ConfigureServices = services =>
            {
                services.AddSingleton(new InMemoryDataAccessor(() => factory!.Data));
                if (chatFactory is not null)
                {
                    services.RemoveAll<IAiChatClientFactory>();
                    services.AddSingleton<IAiChatClientFactory>(chatFactory);
                }

                configure?.Invoke(services);
            },
        };
        return factory;
    }

    /// <summary>Hands the fake storage to services the test registers.</summary>
    private sealed class InMemoryDataAccessor(Func<InMemoryData> data)
    {
        public IPromptRepository Data => data();
    }

    /// <summary>
    /// The prompt repository, with a hook that runs once right after the next read, as if
    /// another tab wrote the prompt between an endpoint's read and its write.
    /// </summary>
    private sealed class PromptRaceRepository(IPromptRepository inner) : IPromptRepository
    {
        public IPromptRepository Inner => inner;

        public Func<Task>? AfterNextRead { get; set; }

        public async Task<Prompt?> GetVisibleAsync(string userId, string promptId, CancellationToken cancellationToken = default)
        {
            var prompt = await inner.GetVisibleAsync(userId, promptId, cancellationToken);
            if (AfterNextRead is { } hook)
            {
                AfterNextRead = null;
                await hook();
            }

            return prompt;
        }

        public Task<IReadOnlyList<Prompt>> ListVisibleAsync(string userId, CancellationToken cancellationToken = default) => inner.ListVisibleAsync(userId, cancellationToken);

        public Task SaveAsync(string userId, Prompt prompt, CancellationToken cancellationToken = default) => inner.SaveAsync(userId, prompt, cancellationToken);

        public Task<bool> DeleteAsync(string userId, string promptId, CancellationToken cancellationToken = default) => inner.DeleteAsync(userId, promptId, cancellationToken);

        public Task<(Prompt Before, Prompt After)?> UpdateAsync(string userId, string promptId, Func<Prompt, Prompt?> change, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(userId, promptId, change, cancellationToken);

        public Task<Prompt?> DeleteIfAsync(string userId, string promptId, Func<Prompt, bool> condition, CancellationToken cancellationToken = default) =>
            inner.DeleteIfAsync(userId, promptId, condition, cancellationToken);
    }

    private static T Service<T>(ApiFactory factory)
        where T : notnull => factory.Services.GetRequiredService<T>();

    private static async Task<(HttpClient Browser, string UserId)> SignUpAsync(ApiFactory factory)
    {
        var browser = factory.CreateBrowser();
        using var response = await AuthEndpointTests.SignUpAsync(browser, $"edit-{Guid.NewGuid():N}@example.test", $"pw-{TestSecrets.NewSecret()}", "Edit Tester");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (browser, (await AuthEndpointTests.Json(response)).GetProperty("user").GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> SendAsync(HttpClient browser, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await browser.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return await AuthEndpointTests.Json(response);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient browser, object body) =>
        (await SendAsync(browser, HttpMethod.Post, "/api/prompts", body)).GetProperty("prompt");

    private static Task<JsonElement> PatchAsync(HttpClient browser, string id, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(browser, HttpMethod.Patch, $"/api/prompts/{id}", body, expected);

    private static string Id(JsonElement prompt) => prompt.GetProperty("id").GetString()!;

    private static string? ImageUrl(JsonElement prompt) => prompt.GetProperty("imageDataUrl").GetString();

    [Fact]
    public async Task An_edit_changes_the_fields_sent_keeps_the_rest_and_records_when()
    {
        await using var factory = CreateFactory();
        var (browser, _) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", category = "bar", title = "Cambridge 18", topic = "Energy", promptText = "The chart shows energy use.", imageDataUrl = Png });
        Assert.Equal(JsonValueKind.Null, created.GetProperty("updatedAt").ValueKind);

        var edited = (await PatchAsync(browser, Id(created), new { promptText = "  The chart shows energy use in 2020.  ", title = "Cambridge 18 Test 2" })).GetProperty("prompt");

        Assert.Equal(Id(created), Id(edited));
        Assert.Equal("The chart shows energy use in 2020.", edited.GetProperty("promptText").GetString());
        Assert.Equal("Cambridge 18 Test 2", edited.GetProperty("title").GetString());
        Assert.Equal("bar", edited.GetProperty("category").GetString());
        Assert.Equal("Energy", edited.GetProperty("topic").GetString());
        Assert.Equal(ImageUrl(created), ImageUrl(edited));
        Assert.Equal(created.GetProperty("createdAt").GetString(), edited.GetProperty("createdAt").GetString());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", edited.GetProperty("updatedAt").GetString());

        // A blank title or topic falls back to the defaults, as on create; blank instructions are refused.
        var blanked = (await PatchAsync(browser, Id(created), new { title = " ", topic = "" })).GetProperty("prompt");
        Assert.Equal("Custom Task 1 Visual", blanked.GetProperty("title").GetString());
        Assert.Equal("Custom Practice", blanked.GetProperty("topic").GetString());
        await PatchAsync(browser, Id(created), new { promptText = "   " }, HttpStatusCode.BadRequest);
        await PatchAsync(browser, Id(created), new { title = new string('x', 301) }, HttpStatusCode.BadRequest);
        await PatchAsync(browser, Id(created), new { taskType = "task3" }, HttpStatusCode.BadRequest);

        var listed = (await SendAsync(browser, HttpMethod.Get, "/api/prompts")).GetProperty("prompts").EnumerateArray().Single(p => Id(p) == Id(created));
        Assert.Equal("The chart shows energy use in 2020.", listed.GetProperty("promptText").GetString());
    }

    [Fact]
    public async Task An_image_is_kept_replaced_or_removed_and_the_old_one_is_discarded()
    {
        await using var factory = CreateFactory();
        var (browser, userId) = await SignUpAsync(factory);
        var images = Service<IImageRepository>(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png, extractedVisualSpec = "old spec" });
        var firstImage = ImageUrl(created)!;

        // Sending the saved link back keeps the image and its visual spec.
        var kept = (await PatchAsync(browser, Id(created), new { imageDataUrl = firstImage })).GetProperty("prompt");
        Assert.Equal(firstImage, ImageUrl(kept));
        Assert.Equal("old spec", kept.GetProperty("extractedVisualSpec").GetString());

        // A new image replaces it, with the visual spec sent alongside (none for a plain image).
        var replaced = (await PatchAsync(browser, Id(created), new { imageDataUrl = OtherPng })).GetProperty("prompt");
        var secondImage = ImageUrl(replaced)!;
        Assert.NotEqual(firstImage, secondImage);
        Assert.StartsWith(ImageStore.RoutePrefix, secondImage);
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("extractedVisualSpec").ValueKind);
        var stored = Assert.Single(await images.ListAsync(userId));
        Assert.Equal(secondImage, $"{ImageStore.RoutePrefix}{stored.Id}");
        Assert.Null(await Service<ImageStore>(factory).GetOwnedAsync(userId, firstImage[ImageStore.RoutePrefix.Length..]));

        // An invalid image is refused and changes nothing.
        await PatchAsync(browser, Id(created), new { imageDataUrl = "data:image/bmp;base64,Qk0=", promptText = "Changed" }, HttpStatusCode.BadRequest);
        var unchanged = (await SendAsync(browser, HttpMethod.Get, "/api/prompts")).GetProperty("prompts").EnumerateArray().Single(p => Id(p) == Id(created));
        Assert.Equal("Describe the chart.", unchanged.GetProperty("promptText").GetString());
        Assert.Single(await images.ListAsync(userId));

        // Null removes it.
        var removed = (await PatchAsync(browser, Id(created), new { imageDataUrl = (string?)null })).GetProperty("prompt");
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("imageDataUrl").ValueKind);
        Assert.Empty(await images.ListAsync(userId));
        Assert.Equal(0, await images.GetTotalBytesAsync(userId));
    }

    [Fact]
    public async Task Replacing_an_image_does_not_count_the_one_it_replaces_against_the_allowance()
    {
        await using var factory = CreateFactory();
        var (browser, userId) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png });

        // Fill the Free plan's 25 MB exactly.
        var images = Service<IImageRepository>(factory);
        var used = await images.GetTotalBytesAsync(userId);
        await images.SaveAsync(userId, TestData.Image("filler", (25L * 1024 * 1024) - used));

        // A new upload no longer fits, but swapping the image for one of the same size does.
        await SendAsync(browser, HttpMethod.Post, "/api/prompts", new { taskType = "task1", promptText = "x", imageDataUrl = Png }, HttpStatusCode.RequestEntityTooLarge);
        var replaced = (await PatchAsync(browser, Id(created), new { imageDataUrl = OtherPng })).GetProperty("prompt");
        Assert.NotEqual(ImageUrl(created), ImageUrl(replaced));
    }

    [Fact]
    public async Task Only_the_owners_uploaded_prompts_can_be_edited_or_deleted()
    {
        await using var factory = CreateFactory();
        var (owner, ownerId) = await SignUpAsync(factory);
        var (intruder, _) = await SignUpAsync(factory);
        var created = await CreateAsync(owner, new { taskType = "task2", promptText = "Discuss both views." });

        // Another user's prompt is not found, never forbidden.
        var notFound = await PatchAsync(intruder, Id(created), new { promptText = "Taken over" }, HttpStatusCode.NotFound);
        Assert.Equal("Prompt not found.", notFound.GetProperty("error").GetString());
        await SendAsync(intruder, HttpMethod.Delete, $"/api/prompts/{Id(created)}", expected: HttpStatusCode.NotFound);
        await PatchAsync(owner, "custom-missing", new { promptText = "x" }, HttpStatusCode.NotFound);

        // Generated prompts are not uploads.
        await Service<IPromptRepository>(factory).SaveAsync(ownerId, TestData.Prompt("generated-1") with { UserId = ownerId, Source = "ai_generated" });
        await PatchAsync(owner, "generated-1", new { promptText = "x" }, HttpStatusCode.NotFound);
        await SendAsync(owner, HttpMethod.Delete, "/api/prompts/generated-1", expected: HttpStatusCode.NotFound);

        var stillThere = await Service<IPromptRepository>(factory).GetVisibleAsync(ownerId, Id(created));
        Assert.Equal("Discuss both views.", stillThere!.PromptText);

        // Signed out is a 401.
        using var signedOut = factory.CreateBrowser();
        using var anonymous = await signedOut.DeleteAsync($"/api/prompts/{Id(created)}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task The_task_type_changes_only_while_no_session_uses_the_prompt()
    {
        await using var factory = CreateFactory();
        var (browser, userId) = await SignUpAsync(factory);
        var unused = await CreateAsync(browser, new { taskType = "task1", category = "bar", promptText = "Describe the chart.", imageDataUrl = Png });

        // Unused: it becomes a Task 2 prompt, with a Task 2 archetype and without its image.
        var switched = (await PatchAsync(browser, Id(unused), new { taskType = "task2" })).GetProperty("prompt");
        Assert.Equal("task2", switched.GetProperty("taskType").GetString());
        Assert.Equal("opinion", switched.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, switched.GetProperty("imageDataUrl").ValueKind);
        Assert.Empty(await Service<IImageRepository>(factory).ListAsync(userId));

        // Used by a session: refused, and the other fields stay editable.
        var used = await CreateAsync(browser, new { taskType = "task2", promptText = "Discuss both views." });
        await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "coach", task2PromptId = Id(used) });
        var conflict = await PatchAsync(browser, Id(used), new { taskType = "task1" }, HttpStatusCode.Conflict);
        Assert.Equal("prompt_in_use", conflict.GetProperty("code").GetString());
        var edited = (await PatchAsync(browser, Id(used), new { taskType = "task2", promptText = "Discuss both views and give your opinion." })).GetProperty("prompt");
        Assert.Equal("Discuss both views and give your opinion.", edited.GetProperty("promptText").GetString());
    }

    [Fact]
    public async Task Deleting_an_unused_prompt_removes_it_and_its_image()
    {
        await using var factory = CreateFactory();
        var (browser, userId) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png });

        var deleted = await SendAsync(browser, HttpMethod.Delete, $"/api/prompts/{Id(created)}");
        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.False(deleted.GetProperty("archived").GetBoolean());

        Assert.Null(await Service<IPromptRepository>(factory).GetVisibleAsync(userId, Id(created)));
        Assert.Empty(await Service<IImageRepository>(factory).ListAsync(userId));
        await SendAsync(browser, HttpMethod.Delete, $"/api/prompts/{Id(created)}", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_a_prompt_that_sessions_use_keeps_it_for_them_but_out_of_the_library()
    {
        await using var factory = CreateFactory();
        var (browser, userId) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png });
        var sessionId = (await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "coach", task1PromptId = Id(created) })).GetProperty("session").GetProperty("id").GetString()!;

        var deleted = await SendAsync(browser, HttpMethod.Delete, $"/api/prompts/{Id(created)}");
        Assert.True(deleted.GetProperty("archived").GetBoolean());

        // Out of the library, the studio and new sessions, and no longer editable.
        Assert.DoesNotContain((await SendAsync(browser, HttpMethod.Get, "/api/prompts")).GetProperty("prompts").EnumerateArray(), p => Id(p) == Id(created));
        var studio = await SendAsync(browser, HttpMethod.Get, "/api/pages/studio");
        Assert.DoesNotContain(studio.GetProperty("prompts").EnumerateArray(), p => Id(p) == Id(created));
        Assert.False(studio.GetProperty("promptUsage").TryGetProperty(Id(created), out _));
        await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "coach", task1PromptId = Id(created) }, HttpStatusCode.NotFound);
        await PatchAsync(browser, Id(created), new { promptText = "x" }, HttpStatusCode.NotFound);
        await SendAsync(browser, HttpMethod.Delete, $"/api/prompts/{Id(created)}", expected: HttpStatusCode.NotFound);

        // The session still opens with its task and image.
        var coach = (await SendAsync(browser, HttpMethod.Get, $"/api/pages/write/{sessionId}")).GetProperty("coach");
        Assert.Equal("Describe the chart.", coach.GetProperty("prompt").GetProperty("promptText").GetString());
        Assert.Equal(ImageUrl(created), ImageUrl(coach.GetProperty("prompt")));
        Assert.Single(await Service<IImageRepository>(factory).ListAsync(userId));
    }

    [Fact]
    public async Task An_unfinished_mock_exam_resumes_both_parts_after_one_of_its_prompts_is_deleted()
    {
        await using var factory = CreateFactory();
        var (browser, userId) = await SignUpAsync(factory);
        var task1 = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart." });
        var task2 = await CreateAsync(browser, new { taskType = "task2", promptText = "Discuss both views." });
        var sessionId = (await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "simulator_full_60m", task1PromptId = Id(task1), task2PromptId = Id(task2) }))
            .GetProperty("session").GetProperty("id").GetString()!;
        var writing = Service<IWritingRepository>(factory);
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-t1", sessionId) with { PromptId = Id(task1), TaskType = "task1", EssayText = "The chart shows a rise.", SecondsSpent = 600 });
        await writing.SaveDraftAsync(userId, TestData.Draft("draft-t2", sessionId) with { PromptId = Id(task2), TaskType = "task2", EssayText = "Some people argue.", SecondsSpent = 900 });

        await SendAsync(browser, HttpMethod.Delete, $"/api/prompts/{Id(task1)}");

        var simulator = (await SendAsync(browser, HttpMethod.Get, $"/api/pages/write/{sessionId}")).GetProperty("simulator");
        Assert.Equal(Id(task1), simulator.GetProperty("task1Prompt").GetProperty("id").GetString());
        var part1 = simulator.GetProperty("initialDrafts").GetProperty("task1");
        Assert.Equal("The chart shows a rise.", part1.GetProperty("essayText").GetString());
        Assert.Equal(600, part1.GetProperty("secondsSpent").GetInt32());
        Assert.Equal("Some people argue.", simulator.GetProperty("initialDrafts").GetProperty("task2").GetProperty("essayText").GetString());
    }

    [Fact]
    public async Task A_session_takes_each_prompt_only_for_its_own_task()
    {
        await using var factory = CreateFactory();
        var (browser, _) = await SignUpAsync(factory);
        var task1 = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart." });
        var task2 = await CreateAsync(browser, new { taskType = "task2", promptText = "Discuss both views." });

        await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "simulator_full_60m", task1PromptId = Id(task2), task2PromptId = Id(task1) }, HttpStatusCode.BadRequest);
        await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "coach", task2PromptId = Id(task1) }, HttpStatusCode.BadRequest);
        await SendAsync(browser, HttpMethod.Post, "/api/sessions", new { mode = "simulator_full_60m", task1PromptId = Id(task1), task2PromptId = Id(task2) });
    }

    [Fact]
    public async Task A_text_edit_keeps_an_image_another_tab_replaced_meanwhile()
    {
        PromptRaceRepository? race = null;
        await using var factory = CreateFactory(configure: services =>
        {
            services.RemoveAll<IPromptRepository>();
            services.AddSingleton<IPromptRepository>(s => race = new PromptRaceRepository(s.GetRequiredService<InMemoryDataAccessor>().Data));
        });
        var (browser, userId) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png });

        // The other tab replaces the image after this request has read the prompt.
        var r = race!;
        string? otherTabImage = null;
        r.AfterNextRead = async () =>
        {
            var store = Service<ImageStore>(factory);
            otherTabImage = await store.StoreAsync(userId, Plans.Free, OtherPng);
            await r.Inner.UpdateAsync(userId, Id(created), p => p with { ImageUrl = otherTabImage });
            await store.DiscardAsync(userId, ImageUrl(created));
        };

        var edited = (await PatchAsync(browser, Id(created), new { promptText = "Describe the chart in detail." })).GetProperty("prompt");

        Assert.Equal("Describe the chart in detail.", edited.GetProperty("promptText").GetString());
        Assert.Equal(otherTabImage, ImageUrl(edited));
        var stored = await r.Inner.GetVisibleAsync(userId, Id(created));
        Assert.Equal(otherTabImage, stored!.ImageUrl);
        Assert.Equal(otherTabImage, $"{ImageStore.RoutePrefix}{Assert.Single(await Service<IImageRepository>(factory).ListAsync(userId)).Id}");
    }

    [Fact]
    public async Task An_edit_does_not_bring_back_a_prompt_deleted_meanwhile()
    {
        PromptRaceRepository? race = null;
        await using var factory = CreateFactory(configure: services =>
        {
            services.RemoveAll<IPromptRepository>();
            services.AddSingleton<IPromptRepository>(s => race = new PromptRaceRepository(s.GetRequiredService<InMemoryDataAccessor>().Data));
        });
        var (browser, userId) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart.", imageDataUrl = Png });

        // Another tab deletes the prompt after this request has read it.
        var r = race!;
        r.AfterNextRead = async () =>
        {
            var deleted = await r.Inner.DeleteIfAsync(userId, Id(created), _ => true);
            await Service<ImageStore>(factory).DiscardAsync(userId, deleted!.ImageUrl);
        };

        await PatchAsync(browser, Id(created), new { promptText = "Changed", imageDataUrl = OtherPng }, HttpStatusCode.NotFound);

        Assert.Null(await r.Inner.GetVisibleAsync(userId, Id(created)));
        // The image stored for the edit is not left behind.
        Assert.Empty(await Service<IImageRepository>(factory).ListAsync(userId));
    }

    [Fact]
    public async Task A_saved_prompts_image_transforms_to_svg_on_pro_for_its_owner_only()
    {
        var fake = new FakeChatClientFactory((req, _) =>
        {
            Assert.True(req.HasImage);
            return new FakeChatResult(JsonSerializer.Serialize(new
            {
                svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 200'><rect x='10' y='10' width='100' height='150' fill='#16656e'/><text x='10' y='190'>2020: 45%</text></svg>",
                visualSpec = new { visualType = "bar chart", unitsAndAxes = "X: Year, Y: Percentage (%)", macroOverviewTrends = new[] { "A steady rise." }, exactFiguresAndStages = new[] { "2020: 45%" } },
            }));
        });
        await using var factory = CreateFactory(fake);
        var (browser, userId) = await SignUpAsync(factory);
        var (intruder, intruderId) = await SignUpAsync(factory);
        var created = await CreateAsync(browser, new { taskType = "task1", category = "bar", promptText = "Describe the chart.", imageDataUrl = Png });
        var withoutImage = await CreateAsync(browser, new { taskType = "task1", promptText = "Describe the chart." });

        // Free: the plan gate comes first.
        var free = await SendAsync(browser, HttpMethod.Post, "/api/ai/transform-svg", new { promptId = Id(created) }, HttpStatusCode.Forbidden);
        Assert.Equal("upgrade_required", free.GetProperty("code").GetString());

        var settings = Service<ISettingsRepository>(factory);
        await settings.UpdateAsync(userId, s => s with { Plan = Plans.Pro });
        await settings.UpdateAsync(intruderId, s => s with { Plan = Plans.Pro });

        var result = await SendAsync(browser, HttpMethod.Post, "/api/ai/transform-svg", new { promptId = Id(created), category = "bar" });
        var svgDataUrl = result.GetProperty("svgDataUrl").GetString()!;
        Assert.StartsWith("data:image/svg+xml;base64,", svgDataUrl);
        Assert.Contains("2020: 45%", result.GetProperty("extractedVisualSpec").GetString());

        // Saving the SVG stores it in place of the original.
        var saved = (await PatchAsync(browser, Id(created), new { imageDataUrl = svgDataUrl, extractedVisualSpec = result.GetProperty("extractedVisualSpec").GetString() })).GetProperty("prompt");
        var image = Assert.Single(await Service<IImageRepository>(factory).ListAsync(userId));
        Assert.Equal(ImageStore.SvgContentType, image.ContentType);
        Assert.Contains("2020: 45%", saved.GetProperty("extractedVisualSpec").GetString());

        // Someone else's prompt is not found, and a prompt without an image has nothing to convert.
        await SendAsync(intruder, HttpMethod.Post, "/api/ai/transform-svg", new { promptId = Id(created) }, HttpStatusCode.NotFound);
        await SendAsync(browser, HttpMethod.Post, "/api/ai/transform-svg", new { promptId = Id(withoutImage) }, HttpStatusCode.BadRequest);
    }
}
