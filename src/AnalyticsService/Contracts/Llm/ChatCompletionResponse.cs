namespace AnalyticsService.Contracts.Llm;

public sealed record ChatCompletionResponse(ChatCompletionChoice?[]? Choices);
