using System.Runtime.CompilerServices;
using Escribot.Api.Ai;
using Microsoft.Extensions.AI;

namespace Escribot.Api.Tests;

internal sealed record FakeChatResult(
    string Content,
    string? ModelUsed = null,
    double? CostUsd = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    IReadOnlyList<string>? StreamChunks = null);

/// <summary>
/// Test double for <see cref="IAiChatClientFactory"/> returning a deterministic
/// <see cref="IChatClient"/> that supports both <c>RunAsync</c> and <c>RunStreamingAsync</c>.
/// </summary>
internal sealed class FakeChatClientFactory(
    Func<AiChatClientRequest, string, CancellationToken, Task<FakeChatResult>> handler) : IAiChatClientFactory
{
    public FakeChatClientFactory(Func<AiChatClientRequest, string, FakeChatResult> syncHandler)
        : this((req, prompt, _) => Task.FromResult(syncHandler(req, prompt)))
    {
    }

    public List<AiChatClientRequest> Requests { get; } = [];

    public IChatClient CreateClient(AiChatClientRequest request)
    {
        Requests.Add(request);
        return new FakeChatClient(request, handler);
    }

    private sealed class FakeChatClient(
        AiChatClientRequest request,
        Func<AiChatClientRequest, string, CancellationToken, Task<FakeChatResult>> handler) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = ExtractUserPrompt(messages);
            var res = await handler(request, prompt, cancellationToken);
            ApplyCost(res);

            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, res.Content))
            {
                ModelId = res.ModelUsed ?? request.Model,
                Usage = BuildUsage(res),
            };
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var prompt = ExtractUserPrompt(messages);
            var res = await handler(request, prompt, cancellationToken);
            ApplyCost(res);

            var chunks = res.StreamChunks is { Count: > 0 } ? res.StreamChunks : [res.Content];
            var model = res.ModelUsed ?? request.Model;

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk)
                {
                    ModelId = model,
                };
            }

            if (BuildUsage(res) is { } usage)
            {
                yield return new ChatResponseUpdate
                {
                    ModelId = model,
                    Contents = [new UsageContent(usage)],
                };
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }

        private static string ExtractUserPrompt(IEnumerable<ChatMessage> messages) =>
            string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));

        private static void ApplyCost(FakeChatResult res)
        {
            if (ProviderCallContext.Current is { } meta)
            {
                if (!string.IsNullOrWhiteSpace(res.ModelUsed))
                {
                    meta.ActualModel = res.ModelUsed;
                }

                if (res.CostUsd is { } cost)
                {
                    meta.CostUsd = cost;
                }
            }
        }

        private static UsageDetails? BuildUsage(FakeChatResult res) =>
            res.PromptTokens is null && res.CompletionTokens is null && res.TotalTokens is null
                ? null
                : new UsageDetails
                {
                    InputTokenCount = res.PromptTokens,
                    OutputTokenCount = res.CompletionTokens,
                    TotalTokenCount = res.TotalTokens,
                };
    }
}
