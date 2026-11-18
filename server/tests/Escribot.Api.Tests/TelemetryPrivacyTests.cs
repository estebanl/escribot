using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Escribot.Api.Tests;

public class TelemetryPrivacyTests
{
    // The second case is how the Aspire AppHost starts the API locally: with the switch
    // that records query string values turned on. The service defaults must overrule it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Traces_do_not_record_credentials_cookies_query_values_or_bodies(bool hostDisablesQueryRedaction)
    {
        var authorization = TestSecrets.NewSecret();
        var cookie = TestSecrets.NewSecret();
        var apiKey = TestSecrets.NewSecret();
        var queryToken = TestSecrets.NewSecret();
        var body = TestSecrets.NewSecret();

        List<Activity> exported = [];
        await using var factory = new ApiFactory
        {
            Settings = hostDisablesQueryRedaction
                ? new() { ["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = "true" }
                : [],
            ConfigureServices = services =>
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported)),
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/not-a-route?token={queryToken}")
        {
            Content = new StringContent(body),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {authorization}");
        request.Headers.TryAddWithoutValidation("Cookie", $"session={cookie}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        using var response = await client.SendAsync(request);

        // The span ends just after the response is sent.
        for (var attempt = 0; attempt < 50 && SnapshotOf(exported).Length == 0; attempt++)
        {
            await Task.Delay(100);
        }

        var spans = SnapshotOf(exported);
        Assert.NotEmpty(spans);

        var recorded = string.Join('\n', spans.SelectMany(span =>
            span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(span.Events.SelectMany(e => e.Tags.Select(tag => $"{tag.Key}={tag.Value}")))
                .Append(span.DisplayName)));

        Assert.Contains("/api/not-a-route", recorded);
        Assert.DoesNotContain(authorization, recorded);
        Assert.DoesNotContain(cookie, recorded);
        Assert.DoesNotContain(apiKey, recorded);
        Assert.DoesNotContain(queryToken, recorded);
        Assert.DoesNotContain(body, recorded);
    }

    private static Activity[] SnapshotOf(List<Activity> exported)
    {
        lock (exported)
        {
            return [.. exported];
        }
    }
}
