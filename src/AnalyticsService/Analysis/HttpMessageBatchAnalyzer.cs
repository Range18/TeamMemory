using System.Net.Http.Headers;
using System.Net.Http.Json;
using AnalyticsService.Contracts.Llm;
using Microsoft.Extensions.Options;

namespace AnalyticsService.Analysis;

public sealed class HttpMessageBatchAnalyzer(IHttpClientFactory clientFactory, IOptions<LlmApiOptions> options)
    : IMessageBatchAnalyzer
{
    public const string HttpClientName = "LlmApi";

    public async Task<AnalysisResult> Analyze(AnalysisInput input, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                settings.Model,
                [new("system", AnalysisPrompt.SystemInstructions), new("user", AnalysisJson.Write(input))],
                settings.MaxTokens,
                settings.UseJsonMode ? new ChatResponseFormat("json_object") : null))
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var client = clientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();

        var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: timeout.Token);
        var choice = completion?.Choices?.FirstOrDefault()
            ?? throw new AnalysisValidationException("LLM API returned no completion choices.");
        if (choice.FinishReason is not (null or "stop"))
            throw new AnalysisValidationException("LLM API did not return a complete analysis response.");
        var content = choice.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new AnalysisValidationException("LLM API returned empty analysis content.");

        return AnalysisJson.Read<AnalysisResult>(content.Trim());
    }
}
