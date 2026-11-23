using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Escribot.Api.Ai;

/// <summary>
/// Per-call metadata captured from provider HTTP responses (such as OpenRouter's exact USD
/// cost and routed model) that standard <c>OpenAI</c> SDK types do not surface.
/// </summary>
public sealed class ProviderCallMetadata
{
    private StringBuilder? _reasoning;
    private StringBuilder? _refusal;

    public string? ActualModel { get; set; }

    /// <summary>Sum of OpenRouter's <c>usage.cost</c> over every HTTP call in the scope (a tool loop makes several).</summary>
    public double? CostUsd { get; set; }

    /// <summary>Text OpenRouter returned in <c>message.reasoning</c> (or streamed <c>delta.reasoning</c>).</summary>
    public string? Reasoning => _reasoning?.ToString();

    /// <summary>Text OpenRouter returned in <c>message.refusal</c> (or streamed <c>delta.refusal</c>).</summary>
    public string? Refusal => _refusal?.ToString();

    internal void AppendReasoning(string text) => (_reasoning ??= new StringBuilder()).Append(text);

    internal void AppendRefusal(string text) => (_refusal ??= new StringBuilder()).Append(text);
}

/// <summary>
/// Ambient carrier for <see cref="ProviderCallMetadata"/> across a single async LLM call.
/// </summary>
internal static class ProviderCallContext
{
    private static readonly AsyncLocal<ProviderCallMetadata?> CurrentBox = new();

    public static ProviderCallMetadata? Current => CurrentBox.Value;

    public static IDisposable BeginScope(out ProviderCallMetadata metadata)
    {
        var previous = CurrentBox.Value;
        metadata = new ProviderCallMetadata();
        CurrentBox.Value = metadata;
        return new Scope(previous);
    }

    private sealed class Scope(ProviderCallMetadata? previous) : IDisposable
    {
        public void Dispose() => CurrentBox.Value = previous;
    }
}

/// <summary>
/// Intercepts OpenAI-compatible HTTP calls to OpenRouter so Escribot can use the official
/// <c>OpenAI</c> SDK (<c>IChatClient</c>) while keeping OpenRouter's Zero Data Collection policy,
/// 3-model fallback routing, reasoning effort, max_tokens buffer, attribution headers, and exact
/// USD cost capture.
/// </summary>
public sealed class OpenRouterHttpHandler(
    string primaryModel,
    IReadOnlyList<string>? fallbackModels,
    string thinkingMode,
    string reasoningEffort,
    int? requestedMaxTokens,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    /// <summary>The most models OpenRouter accepts in a request's <c>models</c> list.</summary>
    public const int MaxRoutedModels = 3;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://escribot.app");
        request.Headers.TryAddWithoutValidation("X-Title", "Escribot");

        if (request.Content is not null)
        {
            var rawBody = await request.Content.ReadAsStringAsync(cancellationToken);
            if (JsonNode.Parse(rawBody) is JsonObject body)
            {
                RewriteRequestBody(body);
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode || ProviderCallContext.Current is null)
        {
            return response;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            CaptureFromJson(responseText, ProviderCallContext.Current);
            var replacement = new StringContent(responseText, Encoding.UTF8, "application/json");
            CopyContentHeaders(response.Content.Headers, replacement.Headers);
            response.Content = replacement;
        }
        else if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var innerStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var capturingStream = new OpenRouterSseCaptureStream(innerStream, ProviderCallContext.Current);
            var replacement = new StreamContent(capturingStream);
            CopyContentHeaders(response.Content.Headers, replacement.Headers);
            response.Content = replacement;
        }

        return response;
    }

    internal void RewriteRequestBody(JsonObject body)
    {
        var modelList = BuildModelList(primaryModel, fallbackModels);
        if (modelList.Count > 0)
        {
            body["model"] = modelList[0];
        }

        if (modelList.Count > 1)
        {
            var modelsArray = new JsonArray();
            foreach (var m in modelList)
            {
                modelsArray.Add(m);
            }

            body["models"] = modelsArray;
        }

        body["provider"] = new JsonObject { ["data_collection"] = "deny" };

        if (thinkingMode == "enabled")
        {
            body["reasoning"] = new JsonObject { ["effort"] = reasoningEffort };
        }

        if (requestedMaxTokens is > 0)
        {
            // OpenRouter reasoning models allocate internal reasoning tokens against max_tokens before producing content.
            var buffered = Math.Max(requestedMaxTokens.Value + 2048, 4096);
            body.Remove("max_completion_tokens");
            body["max_tokens"] = buffered;
        }

        if (body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var isStreaming) && isStreaming)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
    }

    public static List<string> BuildModelList(string primaryModel, IReadOnlyList<string>? fallbackModels)
    {
        var modelList = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(primaryModel) && seen.Add(primaryModel))
        {
            modelList.Add(primaryModel);
        }

        if (fallbackModels is not null)
        {
            foreach (var fallback in fallbackModels)
            {
                if (modelList.Count >= MaxRoutedModels)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(fallback) && seen.Add(fallback))
                {
                    modelList.Add(fallback);
                }
            }
        }

        return modelList;
    }

    internal static void CaptureFromJson(string json, ProviderCallMetadata target)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return;
            }

            if (obj["model"]?.GetValue<string>() is { Length: > 0 } model)
            {
                target.ActualModel = model;
            }

            if (obj["usage"]?["cost"] is JsonValue costVal && costVal.TryGetValue<double>(out var cost) && double.IsFinite(cost))
            {
                // Add rather than overwrite: one metering scope can span several HTTP calls
                // (a function-invocation loop), and OpenRouter reports cost once per call.
                target.CostUsd = (target.CostUsd ?? 0) + cost;
            }

            // Kept for the empty-content fallback: some OpenRouter reasoning models put the whole
            // answer in "reasoning", and refusals arrive in "refusal", neither of which the OpenAI SDK surfaces.
            var choice = obj["choices"]?[0];
            if ((choice?["message"] ?? choice?["delta"]) is JsonObject message)
            {
                if (message["reasoning"] is JsonValue reasoningVal && reasoningVal.TryGetValue<string>(out var reasoning) && reasoning.Length > 0)
                {
                    target.AppendReasoning(reasoning);
                }

                if (message["refusal"] is JsonValue refusalVal && refusalVal.TryGetValue<string>(out var refusal) && refusal.Length > 0)
                {
                    target.AppendRefusal(refusal);
                }
            }
        }
        catch
        {
            // Non-JSON or partial chunk; ignore.
        }
    }

    private static void CopyContentHeaders(HttpContentHeaders source, HttpContentHeaders destination)
    {
        foreach (var header in source)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destination.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    /// <summary>
    /// Pass-through stream that inspects SSE <c>data:</c> lines from OpenRouter as they are read
    /// so exact <c>usage.cost</c> and routed <c>model</c> are captured without buffering the stream.
    /// </summary>
    private sealed class OpenRouterSseCaptureStream(Stream inner, ProviderCallMetadata target) : Stream
    {
        private readonly StringBuilder _lineBuffer = new();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                Inspect(buffer.AsSpan(offset, read));
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                Inspect(buffer.Span[..read]);
            }

            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            if (read > 0)
            {
                Inspect(buffer.AsSpan(offset, read));
            }

            return read;
        }

        private void Inspect(ReadOnlySpan<byte> bytes)
        {
            var chunk = Encoding.UTF8.GetString(bytes);
            foreach (var ch in chunk)
            {
                if (ch == '\n')
                {
                    ProcessLine(_lineBuffer.ToString());
                    _lineBuffer.Clear();
                }
                else if (ch != '\r')
                {
                    _lineBuffer.Append(ch);
                }
            }
        }

        private void ProcessLine(string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                return;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                return;
            }

            if (payload.Contains("\"usage\"", StringComparison.Ordinal)
                || payload.Contains("\"reasoning\"", StringComparison.Ordinal)
                || payload.Contains("\"refusal\"", StringComparison.Ordinal)
                || target.ActualModel is null)
            {
                CaptureFromJson(payload, target);
            }
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_lineBuffer.Length > 0)
                {
                    ProcessLine(_lineBuffer.ToString());
                    _lineBuffer.Clear();
                }

                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_lineBuffer.Length > 0)
            {
                ProcessLine(_lineBuffer.ToString());
                _lineBuffer.Clear();
            }

            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

/// <summary>
/// Injects DeepSeek-specific <c>thinking</c> and <c>reasoning_effort</c> properties when using
/// the OpenAI-compatible DeepSeek endpoint.
/// </summary>
public sealed class DeepSeekHttpHandler(
    string thinkingMode,
    string reasoningEffort,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var rawBody = await request.Content.ReadAsStringAsync(cancellationToken);
            if (JsonNode.Parse(rawBody) is JsonObject body)
            {
                body["thinking"] = new JsonObject { ["type"] = thinkingMode };
                if (thinkingMode == "enabled")
                {
                    body["reasoning_effort"] = reasoningEffort;
                    body.Remove("temperature");
                }

                if (body.TryGetPropertyValue("max_completion_tokens", out var mct) && mct is not null)
                {
                    body.Remove("max_completion_tokens");
                    body["max_tokens"] = mct.DeepClone();
                }

                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
