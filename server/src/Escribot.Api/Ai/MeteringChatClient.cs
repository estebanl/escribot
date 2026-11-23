using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Escribot.Api.Ai;

/// <summary>
/// Usage and model metadata accumulated across one <see cref="IChatClient"/> execution
/// (including multi-turn tool calls or streaming updates).
/// </summary>
public sealed class CapturedLlmUsage
{
    public string? ModelUsed { get; set; }
    public double? CostUsd { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }

    /// <summary>OpenRouter <c>reasoning</c> text, used as the answer when the visible content is empty.</summary>
    public string? Reasoning { get; set; }

    /// <summary>OpenRouter <c>refusal</c> text, reported when the visible content is empty.</summary>
    public string? Refusal { get; set; }

    public void Add(UsageDetails? usage, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            ModelUsed = modelId;
        }

        if (usage is null)
        {
            return;
        }

        if (usage.InputTokenCount is { } input)
        {
            InputTokens = (InputTokens ?? 0) + input;
        }

        if (usage.OutputTokenCount is { } output)
        {
            OutputTokens = (OutputTokens ?? 0) + output;
        }

        if (usage.TotalTokenCount is { } total)
        {
            TotalTokens = (TotalTokens ?? 0) + total;
        }
        else if (usage.InputTokenCount is not null || usage.OutputTokenCount is not null)
        {
            TotalTokens = (InputTokens ?? 0) + (OutputTokens ?? 0);
        }
    }

    public void MergeProviderMetadata(ProviderCallMetadata? meta)
    {
        if (meta is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(meta.ActualModel))
        {
            ModelUsed = meta.ActualModel;
        }

        if (meta.CostUsd is { } cost)
        {
            CostUsd = (CostUsd ?? 0) + cost;
        }

        if (!string.IsNullOrEmpty(meta.Reasoning))
        {
            Reasoning += meta.Reasoning;
        }

        if (!string.IsNullOrEmpty(meta.Refusal))
        {
            Refusal += meta.Refusal;
        }
    }
}

/// <summary>
/// <see cref="DelegatingChatClient"/> middleware that captures token usage, routed model,
/// and OpenRouter USD cost from both <see cref="GetResponseAsync"/> and
/// <see cref="GetStreamingResponseAsync"/> (including tool-calling loops).
/// </summary>
public sealed class MeteringChatClient(IChatClient innerClient, CapturedLlmUsage captured)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = ProviderCallContext.BeginScope(out var providerMeta);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            captured.Add(response.Usage, response.ModelId);
            return response;
        }
        finally
        {
            captured.MergeProviderMetadata(providerMeta);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = ProviderCallContext.BeginScope(out var providerMeta);
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(update.ModelId))
                {
                    captured.ModelUsed = update.ModelId;
                }

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        captured.Add(usageContent.Details, update.ModelId);
                    }
                }

                yield return update;
            }
        }
        finally
        {
            captured.MergeProviderMetadata(providerMeta);
        }
    }
}
