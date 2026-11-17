using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Ai;

/// <summary>
/// Dispatches LLM requests to the configured provider client with per-request timeouts
/// and records usage in <see cref="IAiUsageRepository"/> and <see cref="IFreeAiDailyCounter"/>.
/// </summary>
public sealed partial class AiCompletionService(
    IEnumerable<IAiProviderClient> clients,
    IAiUsageRepository usageRepo,
    IFreeAiDailyCounter freeDailyCounter,
    TimeProvider time,
    ILogger<AiCompletionService> logger)
{
    public static readonly string[] EvaluationFeatures = ["rubric_score", "rubric_score_fallback"];
    public static readonly string[] AssistFeatures = ["outline_check", "toolkit", "prompt_generation", "drill_generate", "drill_score"];

    private static readonly HashSet<string> BillableFeatures =
        new([.. EvaluationFeatures, .. AssistFeatures], StringComparer.Ordinal);

    private readonly Dictionary<string, IAiProviderClient> _clients =
        clients.ToDictionary(c => c.ProviderId, StringComparer.Ordinal);

    public async Task<LlmResultWithMeta> CallLlmWithMetaAsync(LlmCompletionRequest opts, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(opts.Provider, out var client))
        {
            throw new InvalidOperationException($"Unsupported AI provider: {opts.Provider}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(opts.TimeoutMs));

        LlmResultWithMeta result;
        try
        {
            result = await client.CompleteAsync(
                new LlmProviderRequest(
                    opts.ApiKey,
                    opts.Model,
                    opts.FallbackModels,
                    opts.SystemPrompt,
                    opts.UserPrompt,
                    opts.ImageDataUrl,
                    opts.JsonMode,
                    opts.ThinkingMode,
                    opts.ReasoningEffort,
                    opts.MaxTokens),
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"AI model ({opts.Model}) timed out after {Math.Round(opts.TimeoutMs / 1000.0)}s.");
        }

        var keySource = opts.KeySource == "platform" ? "platform" : "user";
        var feature = string.IsNullOrEmpty(opts.Feature) ? "general" : opts.Feature;

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

        return result;
    }

    public async Task<string> CallLlmAsync(LlmCompletionRequest opts, CancellationToken cancellationToken = default)
    {
        var result = await CallLlmWithMetaAsync(opts, cancellationToken);
        return result.Content;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to record AI usage in database.")]
    private static partial void LogUsageRecordFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to increment Free-plan daily AI counter.")]
    private static partial void LogDailyCounterFailed(ILogger logger, Exception exception);
}
