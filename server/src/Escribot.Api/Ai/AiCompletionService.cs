using System.Text;
using Escribot.Api.Ai.Agents;
using Escribot.Api.Api;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Escribot.Api.Ai;

/// <summary>
/// Executes Escribot AI workflows through <see cref="ChatClientAgent"/> (<c>Microsoft.Agents.AI</c>)
/// backed by <see cref="IAiChatClientFactory"/> (<c>Microsoft.Extensions.AI</c>), enforcing
/// per-request timeouts and recording disconnect-safe usage in <see cref="IAiUsageRepository"/>
/// and <see cref="IFreeAiDailyCounter"/>.
/// </summary>
public sealed partial class AiCompletionService(
    IAiChatClientFactory clientFactory,
    EscribotAgentFactory agentFactory,
    IAiUsageRepository usageRepo,
    IFreeAiDailyCounter freeDailyCounter,
    TimeProvider time,
    ILogger<AiCompletionService> logger)
{
    private static readonly HashSet<string> BillableFeatures =
        new([.. UsageService.EvaluationFeatures, .. UsageService.AssistFeatures], StringComparer.Ordinal);

    public async Task<LlmResultWithMeta> CallLlmWithMetaAsync(
        LlmCompletionRequest opts,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchAgentAsync(opts, onChunk: null, stream: false, cancellationToken);
        await RecordUsageAsync(opts, result, opts.Feature);
        return result;
    }

    /// <summary>
    /// Dispatches the agent request and parses the output before recording billable usage.
    /// If parsing fails, cost/tokens are still logged under <c>{feature}_invalid_output</c>
    /// so a failed parse that triggers a fallback does not consume two quota units.
    /// </summary>
    public async Task<(T Parsed, LlmResultWithMeta Meta)> CallLlmAndParseAsync<T>(
        LlmCompletionRequest opts,
        Func<string, T> parse,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchAgentAsync(opts, onChunk: null, stream: false, cancellationToken);
        T parsed;
        try
        {
            parsed = parse(result.Content);
        }
        catch
        {
            var baseFeature = string.IsNullOrEmpty(opts.Feature) ? "general" : opts.Feature;
            await RecordUsageAsync(opts, result, $"{baseFeature}_invalid_output");
            throw;
        }

        await RecordUsageAsync(opts, result, opts.Feature);
        return (parsed, result);
    }

    /// <summary>
    /// Runs the <see cref="ChatClientAgent"/> via <see cref="AIAgent.RunStreamingAsync(ChatMessage, AgentSession?, AgentRunOptions?, CancellationToken)"/>,
    /// invoking <paramref name="onChunk"/> as text chunks arrive, then parses and records usage.
    /// </summary>
    public async Task<(T Parsed, LlmResultWithMeta Meta)> StreamAgentAndParseAsync<T>(
        LlmCompletionRequest opts,
        Func<string, T> parse,
        Action<string>? onChunk = null,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchAgentAsync(opts, onChunk, stream: true, cancellationToken);
        T parsed;
        try
        {
            parsed = parse(result.Content);
        }
        catch
        {
            var baseFeature = string.IsNullOrEmpty(opts.Feature) ? "general" : opts.Feature;
            await RecordUsageAsync(opts, result, $"{baseFeature}_invalid_output");
            throw;
        }

        await RecordUsageAsync(opts, result, opts.Feature);
        return (parsed, result);
    }

    public async Task<string> CallLlmAsync(LlmCompletionRequest opts, CancellationToken cancellationToken = default)
    {
        var result = await CallLlmWithMetaAsync(opts, cancellationToken);
        return result.Content;
    }

    private async Task<LlmResultWithMeta> DispatchAgentAsync(
        LlmCompletionRequest opts,
        Action<string>? onChunk,
        bool stream,
        CancellationToken cancellationToken)
    {
        var captured = new CapturedLlmUsage();
        var hasTools = opts.Tools is { Count: > 0 };

        using var providerClient = clientFactory.CreateClient(new AiChatClientRequest(
            Provider: opts.Provider,
            ApiKey: opts.ApiKey,
            Model: opts.Model,
            FallbackModels: opts.FallbackModels,
            ThinkingMode: opts.ThinkingMode,
            ReasoningEffort: opts.ReasoningEffort,
            MaxTokens: opts.MaxTokens,
            HasImage: !string.IsNullOrEmpty(opts.ImageDataUrl),
            EnableTools: hasTools));

        using var meteredClient = new MeteringChatClient(providerClient, captured);

        var instructions = opts.Provider == "anthropic" && opts.JsonMode
            ? $"{opts.SystemPrompt}\n\nRespond strictly with a single valid JSON object and no markdown prose outside JSON."
            : opts.SystemPrompt;

        float? temperature = opts.Provider == "openai" || opts.ThinkingMode == "enabled"
            ? null
            : 0.25f;

        // Gemini thinking models (e.g., Gemini 4) allocate internal thinking tokens against
        // maxOutputTokens before emitting visible JSON; mirror OpenRouter's reasoning buffer so
        // thinking never truncates the response to length=0.
        int? effectiveMaxTokens = opts.Provider == "gemini" && opts.MaxTokens is > 0
            ? Math.Max(opts.MaxTokens.Value + 4096, 8192)
            : opts.MaxTokens;

        var agent = agentFactory.CreateAgent(
            chatClient: meteredClient,
            name: opts.AgentName ?? opts.Feature,
            instructions: instructions,
            jsonMode: opts.JsonMode,
            maxTokens: effectiveMaxTokens,
            temperature: temperature,
            tools: opts.Tools);

        var userMessage = EscribotAgentFactory.BuildUserMessage(opts.UserPrompt, opts.ImageDataUrl);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(opts.TimeoutMs));

        var streamed = new StringBuilder();
        try
        {
            string content;
            if (stream)
            {
                var sb = streamed;
                await foreach (var update in agent.RunStreamingAsync(userMessage, cancellationToken: timeoutCts.Token))
                {
                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text);
                        onChunk?.Invoke(text);
                    }
                }

                content = sb.ToString();
            }
            else
            {
                var response = await agent.RunAsync(userMessage, cancellationToken: timeoutCts.Token);
                content = response.Text ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                // OpenRouter reasoning models can return content="" with the answer in "reasoning";
                // the removed OpenRouterClient used that text, and surfaced refusals, so keep doing both.
                if (!string.IsNullOrWhiteSpace(captured.Refusal) && string.IsNullOrWhiteSpace(captured.Reasoning))
                {
                    throw new InvalidOperationException($"{opts.Provider} model refused request: {captured.Refusal}");
                }

                content = captured.Reasoning ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"Empty response from {opts.Provider} agent ({opts.Model}).");
            }

            var modelUsed = !string.IsNullOrWhiteSpace(captured.ModelUsed) ? captured.ModelUsed : opts.Model;
            return new LlmResultWithMeta(
                Content: content,
                ModelUsed: modelUsed,
                CostUsd: captured.CostUsd,
                PromptTokens: captured.InputTokens is { } i ? (int)Math.Min(i, int.MaxValue) : null,
                CompletionTokens: captured.OutputTokens is { } o ? (int)Math.Min(o, int.MaxValue) : null,
                TotalTokens: captured.TotalTokens is { } t ? (int)Math.Min(t, int.MaxValue) : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"AI model ({opts.Model}) timed out after {Math.Round(opts.TimeoutMs / 1000.0)}s.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            && (captured.TotalTokens is > 0 || captured.CostUsd is not null || streamed.Length > 0))
        {
            // Disconnect-safe metering: record partial usage if the client disconnected mid-stream.
            // Streaming providers report usage only in the final chunk, so a mid-stream disconnect
            // usually has no token counts yet; estimate them (about 4 characters per token) from the
            // prompt and the text streamed so far rather than recording nothing.
            int? promptTokens = captured.InputTokens is { } i
                ? (int)Math.Min(i, int.MaxValue)
                : EstimateTokens(instructions.Length + opts.UserPrompt.Length);
            int? completionTokens = captured.OutputTokens is { } o
                ? (int)Math.Min(o, int.MaxValue)
                : EstimateTokens(streamed.Length);
            int? totalTokens = captured.TotalTokens is { } t
                ? (int)Math.Min(t, int.MaxValue)
                : promptTokens + completionTokens;
            var partial = new LlmResultWithMeta(
                Content: string.Empty,
                ModelUsed: !string.IsNullOrWhiteSpace(captured.ModelUsed) ? captured.ModelUsed : opts.Model,
                CostUsd: captured.CostUsd,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens,
                TotalTokens: totalTokens);
            var baseFeature = string.IsNullOrEmpty(opts.Feature) ? "general" : opts.Feature;
            await RecordUsageAsync(opts, partial, $"{baseFeature}_aborted");
            throw;
        }
    }

    private static int EstimateTokens(long characters) => (int)Math.Min((characters + 3) / 4, int.MaxValue);

    private async Task RecordUsageAsync(LlmCompletionRequest opts, LlmResultWithMeta result, string? rawFeature)
    {
        var keySource = opts.KeySource == "platform" ? "platform" : "user";
        var feature = string.IsNullOrEmpty(rawFeature) ? "general" : rawFeature;

        try
        {
            await usageRepo.RecordAsync(
                opts.UserId,
                new AiUsageRecord
                {
                    Id = $"use-{Guid.NewGuid()}",
                    UserId = opts.UserId,
                    Feature = feature,
                    Provider = opts.Provider,
                    KeySource = keySource,
                    Plan = opts.Plan,
                    ModelUsed = result.ModelUsed,
                    PromptTokens = result.PromptTokens,
                    CompletionTokens = result.CompletionTokens,
                    TotalTokens = result.TotalTokens,
                    CostUsd = result.CostUsd,
                    CreatedAt = time.GetUtcNow(),
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogUsageRecordFailed(logger, ex);
        }

        if (keySource == "platform" && opts.Plan == Plans.Free && BillableFeatures.Contains(feature))
        {
            try
            {
                await freeDailyCounter.IncrementTodayAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogDailyCounterFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to record AI usage in database.")]
    private static partial void LogUsageRecordFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to increment Free-plan daily AI counter.")]
    private static partial void LogDailyCounterFailed(ILogger logger, Exception exception);
}

/// <summary>A request named an AI provider the server has no client for.</summary>
public sealed class UnsupportedAiProviderException(string provider)
    : InvalidOperationException($"Unsupported AI provider: {provider}");
