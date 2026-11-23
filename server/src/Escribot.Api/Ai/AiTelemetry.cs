namespace Escribot.Api.Ai;

/// <summary>
/// OpenTelemetry identifiers for Escribot's AI agent and <c>IChatClient</c> pipeline.
/// Registered in <c>Escribot.ServiceDefaults</c> with sensitive content capture forced off.
/// </summary>
public static class AiTelemetry
{
    public const string SourceName = "Escribot.Ai";
}
