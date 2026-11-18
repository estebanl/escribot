using Escribot.Api.Api;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;

namespace Escribot.Api.Ai;

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
    private static readonly HashSet<string> BillableFeatures =
        new([.. UsageService.EvaluationFeatures, .. UsageService.AssistFeatures], StringComparer.Ordinal);

    private readonly Dictionary<string, IAiProviderClient> _clients =
        clients.ToDictionary(c => c.ProviderId, StringComparer.Ordinal);

    public async Task<LlmResultWithMeta> CallLlmWithMetaAsync(LlmCompletionRequest opts, CancellationToken cancellationToken = default)
    {
        var result = await DispatchAsync(opts, cancellationToken);
        await RecordUsageAsync(opts, result, opts.Feature);
        return result;
    }

    /// <summary>
    /// Dispatches the request and parses the output before recording billable usage.
    /// If parsing fails, cost/tokens are still logged under <c>{feature}_invalid_output</c>
    /// so a failed parse that triggers a fallback does not consume two quota units.
    /// </summary>
    public async Task<(T Parsed, LlmResultWithMeta Meta)> CallLlmAndParseAsync<T>(
        LlmCompletionRequest opts,
        Func<string, T> parse,
        CancellationToken cancellationToken = default)
    {
        var result = await DispatchAsync(opts, cancellationToken);
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

    private async Task<LlmResultWithMeta> DispatchAsync(LlmCompletionRequest opts, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(opts.Provider, out var client))
        {
            throw new InvalidOperationException($"Unsupported AI provider: {opts.Provider}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(opts.TimeoutMs));

        try
        {
            return await client.CompleteAsync(
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
    }

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
