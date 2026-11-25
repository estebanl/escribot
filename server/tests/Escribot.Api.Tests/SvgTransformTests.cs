using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Escribot.Api.Ai;
using Escribot.Api.Api;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Escribot.Api.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Escribot.Api.Tests;

public class SvgTransformTests
{
    // Minimal valid 1x1 PNG bytes.
    private static readonly string SamplePngDataUrl =
        "data:image/png;base64," + Convert.ToBase64String([
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
            0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82,
        ]);

    private const string ValidSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 600 400' width='600' height='400'>" +
        "<rect x='0' y='0' width='600' height='400' fill='#ffffff'/>" +
        "<rect x='80' y='120' width='60' height='200' fill='#16656e'/>" +
        "<text x='80' y='350' fill='#0f172a'>2020: 45%</text>" +
        "</svg>";

    private static string SampleSvgDataUrl(string svg = ValidSvg) =>
        $"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(svg))}";

    private static ApiFactory CreateFactory(FakeChatClientFactory? chatFactory = null, Dictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = chatFactory is null ? null : "or-test-key",
        };
        if (extraSettings is not null)
        {
            foreach (var (k, v) in extraSettings)
            {
                settings[k] = v;
            }
        }

        return new ApiFactory
        {
            Settings = settings,
            ConfigureServices = services =>
            {
                if (chatFactory is not null)
                {
                    services.RemoveAll<IAiChatClientFactory>();
                    services.AddSingleton<IAiChatClientFactory>(chatFactory);
                }
            },
        };
    }

    private static async Task<(HttpClient Browser, string UserId)> SignUpAsync(ApiFactory factory, string label, string? emailOverride = null)
    {
        var browser = factory.CreateBrowser();
        var email = emailOverride ?? $"{label}-{Guid.NewGuid():N}@example.test";
        using var res = await AuthEndpointTests.SignUpAsync(browser, email, $"pw-{TestSecrets.NewSecret()}", $"User {label}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var userId = (await AuthEndpointTests.Json(res)).GetProperty("user").GetProperty("id").GetString()!;
        return (browser, userId);
    }

    // ---- SvgSanitizer unit tests ----

    [Fact]
    public void SvgSanitizer_preserves_safe_chart_elements_and_local_refs()
    {
        const string input = """
            <svg xmlns="http://www.w3.org/2000/svg" width="800" height="500">
              <defs>
                <style>.label { font-size: 12px; fill: #0f172a; } @import url('https://evil.test/x.css');</style>
                <marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5"><path d="M 1 1 L 8 5 L 1 9 z" fill="#16656e"/></marker>
              </defs>
              <rect x="0" y="0" width="800" height="500" fill="#ffffff"/>
              <line x1="50" y1="250" x2="700" y2="250" stroke="#16656e" marker-end="url(#arrow)"/>
              <text x="60" y="40" class="label">Renewable Share</text>
            </svg>
            """;

        var clean = SvgSanitizer.Sanitize(input);
        Assert.NotNull(clean);
        Assert.Contains("viewBox=\"0 0 800 500\"", clean);
        Assert.Contains("marker-end=\"url(#arrow)\"", clean);
        Assert.Contains("Renewable Share", clean);
        Assert.DoesNotContain("@import", clean);
        Assert.DoesNotContain("evil.test", clean);
    }

    [Fact]
    public void SvgSanitizer_strips_scripts_event_handlers_foreignObject_and_external_refs()
    {
        const string malicious = """
            <svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)" viewBox="0 0 400 200">
              <script>alert('xss')</script>
              <foreignObject width="100" height="100"><body xmlns="http://www.w3.org/1999/xhtml"><script>alert(2)</script></body></foreignObject>
              <image href="https://evil.test/track.png" width="10" height="10"/>
              <use href="https://evil.test/sprite.svg#x"/>
              <a href="javascript:alert(3)"><rect x="0" y="0" width="10" height="10"/></a>
              <rect x="10" y="10" width="100" height="50" fill="#16656e" onclick="alert(4)" style="background:url(https://evil.test/bg.png)"/>
              <text x="20" y="40">Safe Label</text>
            </svg>
            """;

        var clean = SvgSanitizer.Sanitize(malicious);
        Assert.NotNull(clean);
        Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreignObject", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<image", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<use", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.test", clean, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Safe Label", clean);
    }

    [Fact]
    public void SvgSanitizer_rejects_dtd_xxe_and_empty_svgs()
    {
        const string xxe = """
            <!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <svg xmlns="http://www.w3.org/2000/svg"><text>&xxe;</text></svg>
            """;
        Assert.Null(SvgSanitizer.Sanitize(xxe));
        Assert.Null(SvgSanitizer.Sanitize("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"));
        Assert.Null(SvgSanitizer.Sanitize("<html><body>Not an svg</body></html>"));
    }

    [Fact]
    public void ImageStore_sanitizes_svg_uploads_and_rejects_svg_when_raster_only()
    {
        var maliciousDataUrl = SampleSvgDataUrl(
            "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'><script>evil()</script><rect width='10' height='10'/></svg>");

        var (contentType, data) = ImageStore.ParseDataUrl(maliciousDataUrl, allowSvg: true);
        Assert.Equal("image/svg+xml", contentType);
        var storedText = Encoding.UTF8.GetString(data);
        Assert.DoesNotContain("script", storedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", storedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<rect", storedText);

        var ex = Assert.Throws<ImageUploadException>(() => ImageStore.ParseDataUrl(maliciousDataUrl, allowSvg: false));
        Assert.Equal(400, ex.StatusCode);
    }

    // ---- Endpoint gating & AI pipeline tests ----

    [Fact]
    public async Task Transform_svg_returns_403_for_free_users_even_with_their_own_api_key()
    {
        var fake = new FakeChatClientFactory((_, _) => new FakeChatResult("""{"svg":"","visualSpec":{}}"""));
        await using var factory = CreateFactory(fake);
        var (browser, _) = await SignUpAsync(factory, "free-byok");

        // Free user adds their own OpenRouter API key.
        using var keyRes = await browser.PutAsJsonAsync("/api/settings/providers/openrouter", new
        {
            apiKey = "sk-or-v1-test-user-key-1234567890",
        });
        Assert.Equal(HttpStatusCode.OK, keyRes.StatusCode);

        using var res = await browser.PostAsJsonAsync("/api/ai/transform-svg", new
        {
            imageDataUrl = SamplePngDataUrl,
            category = "bar",
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await AuthEndpointTests.Json(res);
        Assert.Equal("upgrade_required", body.GetProperty("code").GetString());
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Transform_svg_succeeds_for_paid_pro_user_records_assist_usage_and_sanitizes_output()
    {
        var fake = new FakeChatClientFactory((req, _) =>
        {
            Assert.True(req.HasImage);
            return new FakeChatResult(
                Content: JsonSerializer.Serialize(new
                {
                    svg = "<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)' viewBox='0 0 800 480'><script>bad()</script><rect x='0' y='0' width='800' height='480' fill='#ffffff'/><rect x='100' y='120' width='80' height='260' fill='#16656e'/><text x='100' y='410'>2020: 62%</text></svg>",
                    visualSpec = new
                    {
                        visualType = "bar chart",
                        unitsAndAxes = "X: Year, Y: Percentage (%)",
                        macroOverviewTrends = new[] { "Steady upward trend in renewable usage." },
                        exactFiguresAndStages = new[] { "2020: 62%" },
                    },
                }),
                ModelUsed: "deepseek/deepseek-v4.1-flash-1017",
                TotalTokens: 1200,
                CostUsd: 0.002);
        });

        await using var factory = CreateFactory(fake);
        var (browser, userId) = await SignUpAsync(factory, "pro-paid");

        // Grant an active Stripe subscription.
        var subscriptions = factory.Services.GetRequiredService<ISubscriptionRepository>();
        await subscriptions.CreateAsync(userId, "cus_svg_test");
        await subscriptions.UpdateAsync(userId, s => s with
        {
            StripeSubscriptionId = "sub_svg_test",
            Status = "active",
        });

        using var res = await browser.PostAsJsonAsync("/api/ai/transform-svg", new
        {
            imageDataUrl = SamplePngDataUrl,
            category = "bar",
            promptText = "The bar chart shows renewable energy share in 2020.",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await AuthEndpointTests.Json(res);
        var svgDataUrl = body.GetProperty("svgDataUrl").GetString()!;
        Assert.StartsWith("data:image/svg+xml;base64,", svgDataUrl);

        var svgXml = Encoding.UTF8.GetString(Convert.FromBase64String(svgDataUrl["data:image/svg+xml;base64,".Length..]));
        Assert.DoesNotContain("<script", svgXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", svgXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2020: 62%", svgXml);

        var spec = body.GetProperty("extractedVisualSpec").GetString();
        Assert.NotNull(spec);
        Assert.Contains("bar chart", spec);
        Assert.Contains("2020: 62%", spec);

        // Verify Assist usage was recorded under "svg_transform".
        var usageRepo = factory.Services.GetRequiredService<IAiUsageRepository>();
        var count = await usageRepo.CountAsync(userId, DateTimeOffset.UtcNow.AddDays(-1), UsageService.AssistFeatures, "platform");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Transform_svg_allows_owner_and_complimentary_pro()
    {
        var fake = new FakeChatClientFactory((_, _) => new FakeChatResult(
            JsonSerializer.Serialize(new { svg = ValidSvg, visualSpec = new { visualType = "bar chart" } })));

        const string ownerEmail = "owner-svg@example.test";
        await using var factory = CreateFactory(fake, new Dictionary<string, string?>
        {
            ["OWNER_EMAIL"] = ownerEmail,
        });

        // Owner account (verified).
        var (ownerBrowser, ownerId) = await SignUpAsync(factory, "owner", ownerEmail);
        await factory.Services.GetRequiredService<IUserAccountRepository>().UpdateAsync(ownerId, a => a with { EmailVerified = true });

        using var ownerRes = await ownerBrowser.PostAsJsonAsync("/api/ai/transform-svg", new { imageDataUrl = SamplePngDataUrl });
        Assert.Equal(HttpStatusCode.OK, ownerRes.StatusCode);

        // Admin-granted complimentary Pro account.
        var (compBrowser, compId) = await SignUpAsync(factory, "comp-pro");
        await factory.Services.GetRequiredService<ISettingsRepository>().UpdateAsync(compId, s => s with { Plan = Plans.Pro });

        using var compRes = await compBrowser.PostAsJsonAsync("/api/ai/transform-svg", new { imageDataUrl = SamplePngDataUrl });
        Assert.Equal(HttpStatusCode.OK, compRes.StatusCode);
    }

    [Fact]
    public async Task Transform_svg_records_non_billable_invalid_output_when_model_returns_invalid_svg()
    {
        var fake = new FakeChatClientFactory((_, _) => new FakeChatResult("Sorry, I cannot draw that."));
        await using var factory = CreateFactory(fake);
        var (browser, userId) = await SignUpAsync(factory, "pro-invalid");
        await factory.Services.GetRequiredService<ISettingsRepository>().UpdateAsync(userId, s => s with { Plan = Plans.Pro });

        using var res = await browser.PostAsJsonAsync("/api/ai/transform-svg", new { imageDataUrl = SamplePngDataUrl });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);

        var usageRepo = factory.Services.GetRequiredService<IAiUsageRepository>();
        var billableCount = await usageRepo.CountAsync(userId, DateTimeOffset.UtcNow.AddDays(-1), UsageService.AssistFeatures, "platform");
        Assert.Equal(0, billableCount);

        var invalidCount = await usageRepo.CountAsync(userId, DateTimeOffset.UtcNow.AddDays(-1), ["svg_transform_invalid_output"], "platform");
        Assert.Equal(1, invalidCount);
    }

    [Fact]
    public async Task Saved_svg_prompt_serves_with_sandbox_csp_enforces_isolation_and_grades_without_vision_call()
    {
        var visionCalls = 0;
        var fake = new FakeChatClientFactory((req, userPrompt) =>
        {
            if (req.HasImage)
            {
                visionCalls++;
                throw new InvalidOperationException("Vision should not be invoked for SVG prompts.");
            }

            if (userPrompt.Contains("taTrBand", StringComparison.Ordinal))
            {
                return new FakeChatResult(
                    """{"taTrBand":7.5,"ccBand":7.0,"lrBand":7.0,"graBand":7.0,"examinerSummary":"Accurate data.","criteria":{"TA_TR":{"band":7.5},"CC":{"band":7.0},"LR":{"band":7.0},"GRA":{"band":7.0}},"topPriorities":["Refine overview"]}""");
            }

            return new FakeChatResult(
                """{"inlineAnnotations":[],"targetBandRewrite":"Polished report.","rewriteKeyChanges":["Stronger overview"],"vaultItems":[]}""");
        });

        await using var factory = CreateFactory(fake);
        var (browserA, proId) = await SignUpAsync(factory, "svg-owner");
        await factory.Services.GetRequiredService<ISettingsRepository>().UpdateAsync(proId, s => s with { Plan = Plans.Pro });
        var (browserB, _) = await SignUpAsync(factory, "svg-other");

        // Save custom prompt with SVG and NO extractedVisualSpec to verify fallback text context works too.
        using var createRes = await browserA.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task1",
            category = "bar",
            title = "SVG Energy Chart",
            promptText = "The bar chart shows renewable share in 2020.",
            imageDataUrl = SampleSvgDataUrl(),
        });
        Assert.Equal(HttpStatusCode.OK, createRes.StatusCode);
        var promptJson = (await AuthEndpointTests.Json(createRes)).GetProperty("prompt");
        var promptId = promptJson.GetProperty("id").GetString()!;
        var imageUrl = promptJson.GetProperty("imageDataUrl").GetString()!;
        Assert.StartsWith("/api/images/", imageUrl);

        // Owner receives SVG with strict sandbox CSP.
        using var imgRes = await browserA.GetAsync(imageUrl);
        Assert.Equal(HttpStatusCode.OK, imgRes.StatusCode);
        Assert.Equal("image/svg+xml", imgRes.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            SecurityHeaders.UploadedSvgContentSecurityPolicy,
            string.Join(", ", imgRes.Headers.GetValues("Content-Security-Policy")));

        // Another user gets 404 (tenant isolation).
        using var otherImgRes = await browserB.GetAsync(imageUrl);
        Assert.Equal(HttpStatusCode.NotFound, otherImgRes.StatusCode);

        // Evaluate essay against SVG prompt: should decode SVG markup as text context, never send SVG to vision.
        using var sessionRes = await browserA.PostAsJsonAsync("/api/sessions", new { mode = "coach", task1PromptId = promptId });
        var sessionId = (await AuthEndpointTests.Json(sessionRes)).GetProperty("session").GetProperty("id").GetString()!;

        using var evalRes = await browserA.PostAsJsonAsync("/api/ai/evaluate", new
        {
            sessionId,
            submissions = new[]
            {
                new
                {
                    promptId,
                    taskType = "task1",
                    essayText = "Overall, renewable energy accounted for a substantial share in 2020, reaching 45% across the measured sector.",
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, evalRes.StatusCode);
        var evalBody = await evalRes.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"result\"", evalBody);
        Assert.Equal(0, visionCalls);
    }

    [Fact]
    public async Task Create_prompt_persists_extractedVisualSpec_when_supplied_with_svg()
    {
        await using var factory = CreateFactory();
        var (browser, proId) = await SignUpAsync(factory, "spec-save");
        await factory.Services.GetRequiredService<ISettingsRepository>().UpdateAsync(proId, s => s with { Plan = Plans.Pro });

        const string spec = """{"visualType":"bar chart","exactFiguresAndStages":["2020: 45%"]}""";
        using var res = await browser.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task1",
            category = "bar",
            title = "With Spec",
            promptText = "Summarise the bar chart.",
            imageDataUrl = SampleSvgDataUrl(),
            extractedVisualSpec = spec,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var prompt = (await AuthEndpointTests.Json(res)).GetProperty("prompt");
        Assert.Equal(spec, prompt.GetProperty("extractedVisualSpec").GetString());
    }

    [Fact]
    public async Task Free_users_cannot_upload_svg_prompts()
    {
        await using var factory = CreateFactory();
        var (browser, _) = await SignUpAsync(factory, "free-svg");

        using var res = await browser.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task1",
            category = "bar",
            promptText = "Summarise the bar chart.",
            imageDataUrl = SampleSvgDataUrl(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await AuthEndpointTests.Json(res);
        Assert.Equal("upgrade_required", body.GetProperty("code").GetString());

        // Nothing was saved: no custom prompt and no stored image.
        using var list = await browser.GetAsync("/api/prompts");
        var prompts = (await AuthEndpointTests.Json(list)).GetProperty("prompts").EnumerateArray();
        Assert.DoesNotContain(prompts, p => p.GetProperty("source").GetString() == "custom");
        Assert.Empty(factory.Files.Saved);

        // Raster uploads stay open to Free.
        using var png = await browser.PostAsJsonAsync("/api/prompts", new
        {
            taskType = "task1",
            category = "bar",
            promptText = "Summarise the bar chart.",
            imageDataUrl = SamplePngDataUrl,
        });
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
    }

    [Fact]
    public async Task Restoring_a_backup_on_free_drops_svg_images_but_keeps_the_prompts()
    {
        // A backup made on Pro (prompts carry their images inline), restored on Free.
        await using var factory = CreateFactory();
        var (free, _) = await SignUpAsync(factory, "backup-free");
        const string spec = """{"visualType":"bar chart","exactFiguresAndStages":["2020: 45%"]}""";
        object Prompt(string id, string title, string imageDataUrl, string? extractedVisualSpec = null) => new
        {
            id,
            taskType = "task1",
            category = "bar",
            topic = "Energy",
            title,
            promptText = "Summarise the bar chart.",
            imageDataUrl,
            extractedVisualSpec,
            source = "custom",
            createdAt = DateTimeOffset.UtcNow,
        };
        var backup = new
        {
            version = 2,
            data = new
            {
                prompts = new[]
                {
                    Prompt("custom-svg", "SVG Chart", SampleSvgDataUrl(), spec),
                    Prompt("custom-png", "PNG Chart", SamplePngDataUrl),
                },
            },
        };

        using var import = await free.PostAsJsonAsync("/api/backup", backup);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        using var list = await free.GetAsync("/api/prompts");
        var restored = (await AuthEndpointTests.Json(list)).GetProperty("prompts").EnumerateArray()
            .Where(p => p.GetProperty("source").GetString() == "custom")
            .ToDictionary(p => p.GetProperty("title").GetString()!);

        // The SVG is dropped like any image that cannot be stored; the prompt and its spec stay.
        var svgPrompt = restored["SVG Chart"];
        Assert.True(svgPrompt.GetProperty("imageDataUrl").ValueKind is JsonValueKind.Null);
        Assert.Equal(spec, svgPrompt.GetProperty("extractedVisualSpec").GetString());
        Assert.StartsWith("/api/images/", restored["PNG Chart"].GetProperty("imageDataUrl").GetString());
        Assert.Single(factory.Files.Saved);
    }
}
