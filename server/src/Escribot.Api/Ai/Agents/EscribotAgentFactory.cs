using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Escribot.Api.Ai.Agents;

/// <summary>
/// Creates stateless <see cref="ChatClientAgent"/> instances for Escribot's AI workflows.
/// Each agent wraps a request-scoped <see cref="IChatClient"/> configured for the user's
/// effective provider, model, and role.
/// </summary>
public sealed partial class EscribotAgentFactory
{
    public ChatClientAgent CreateAgent(
        IChatClient chatClient,
        string name,
        string instructions,
        bool jsonMode,
        int? maxTokens,
        float? temperature = 0.25f,
        IList<AITool>? tools = null)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = instructions,
            Temperature = temperature,
            MaxOutputTokens = maxTokens,
            ResponseFormat = jsonMode ? ChatResponseFormat.Json : ChatResponseFormat.Text,
            Tools = tools is { Count: > 0 } ? tools : null,
        };

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = name,
                ChatOptions = chatOptions,
            });
    }

    /// <summary>
    /// Builds a user <see cref="ChatMessage"/>, attaching an optional <c>data:mime;base64,...</c>
    /// image as <see cref="DataContent"/> for multimodal vision agents.
    /// </summary>
    public static ChatMessage BuildUserMessage(string userPrompt, string? imageDataUrl = null)
    {
        if (string.IsNullOrEmpty(imageDataUrl))
        {
            return new ChatMessage(ChatRole.User, userPrompt);
        }

        var match = DataUrlRegex().Match(imageDataUrl);
        if (!match.Success)
        {
            return new ChatMessage(ChatRole.User, userPrompt);
        }

        var mimeType = match.Groups[1].Value;
        var bytes = Convert.FromBase64String(match.Groups[2].Value);
        return new ChatMessage(
            ChatRole.User,
            [
                new TextContent(userPrompt),
                new DataContent(bytes, mimeType)
                {
                    // Read by the OpenAI-compatible clients (OpenAI, OpenRouter, DeepSeek) as image_url.detail;
                    // "high" keeps small chart labels legible for Task 1 extraction. Other providers ignore it.
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["detail"] = "high" },
                },
            ]);
    }

    [GeneratedRegex(@"^data:([^;]+);base64,(.+)$")]
    private static partial Regex DataUrlRegex();
}
