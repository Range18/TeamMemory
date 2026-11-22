using System.Text.Json.Serialization;

namespace AnalyticsService.Contracts.Llm;

public sealed record ChatCompletionChoice(
    ChatMessage? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);
