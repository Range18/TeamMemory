using System.Text.Json.Serialization;

namespace AnalyticsService.Contracts.Llm;

public sealed record ChatCompletionRequest(
    string Model,
    ChatMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("response_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ChatResponseFormat? ResponseFormat)
{
    public double Temperature => 0.1;
    public bool Stream => false;
}
