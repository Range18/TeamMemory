using System.Net;
using System.Text;
using System.Text.Json;
using AnalyticsService.Analysis;
using Microsoft.Extensions.Options;

internal static class LlmApiChecks
{
    public static async Task Run()
    {
        var input = new AnalysisInput(Guid.NewGuid(), "team-a",
            [new AnalysisMessage(Guid.NewGuid(), 1, 7, "developer", "Work update", DateTimeOffset.UtcNow)], []);
        var settings = new LlmApiOptions { Endpoint = "https://llm.example/v1/chat/completions", Model = "test-model", ApiKey = "test-key" };
        var analyzer = Create(settings, async (request, ct) =>
        {
            Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == settings.Endpoint, "Wrong API endpoint/method.");
            Assert(request.Headers.Authorization?.ToString() == "Bearer test-key", "Missing bearer authentication.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            Assert(body.GetProperty("model").GetString() == settings.Model, "Wrong model.");
            Assert(body.GetProperty("max_tokens").GetInt32() == settings.MaxTokens, "Wrong token limit.");
            Assert(body.GetProperty("stream").GetBoolean() == false, "Streaming must be disabled.");
            Assert(body.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "JSON mode missing.");
            var messages = body.GetProperty("messages");
            Assert(messages[0].GetProperty("role").GetString() == "system", "Missing system instructions.");
            Assert(messages[1].GetProperty("content").GetString() == AnalysisJson.Write(input), "Input snapshot changed in transit.");
            return Completion("{\"schema_version\":1,\"changes\":[],\"ambiguities\":[]}");
        });
        var result = await analyzer.Analyze(input, CancellationToken.None);
        Assert(result.SchemaVersion == 1 && result.Changes.Length == 0, "API completion was not parsed.");

        var anonymousSettings = new LlmApiOptions { UseJsonMode = false };
        await Create(anonymousSettings, async (request, ct) =>
        {
            Assert(request.Headers.Authorization is null, "Local API must allow requests without a key.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert(!body.RootElement.TryGetProperty("response_format", out _), "Disabled JSON mode was sent.");
            return Completion("{\"schema_version\":1,\"changes\":[],\"ambiguities\":[]}");
        }).Analyze(input, CancellationToken.None);

        await ExpectFailure<HttpRequestException>(() => Create(settings, (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).Analyze(input, CancellationToken.None));
        await ExpectFailure<AnalysisValidationException>(() => Create(settings, (_, _) =>
            Task.FromResult(JsonResponse("{\"choices\":[]}"))).Analyze(input, CancellationToken.None));
        await ExpectFailure<AnalysisValidationException>(() => Create(settings, (_, _) =>
            Task.FromResult(Completion("", "stop"))).Analyze(input, CancellationToken.None));
        await ExpectFailure<AnalysisValidationException>(() => Create(settings, (_, _) =>
            Task.FromResult(Completion("{}", "length"))).Analyze(input, CancellationToken.None));
        await ExpectFailure<JsonException>(() => Create(settings, (_, _) =>
            Task.FromResult(Completion("not-json"))).Analyze(input, CancellationToken.None));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectFailure<OperationCanceledException>(() => Create(settings, (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Completion("{}"));
        }).Analyze(input, canceled.Token));
        await ExpectFailure<OperationCanceledException>(() => Create(new LlmApiOptions { TimeoutSeconds = 1 }, async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Completion("{}");
        }).Analyze(input, CancellationToken.None));
        Console.WriteLine("PASS: LLM API request/auth/JSON mode, response parsing, HTTP failures, timeout and cancellation");
    }

    private static HttpMessageBatchAnalyzer Create(LlmApiOptions settings,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubClientFactory(send), Options.Create(settings));

    private static HttpResponseMessage Completion(string content, string finishReason = "stop") =>
        JsonResponse(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finishReason } }
        }));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static async Task ExpectFailure<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class StubClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(send)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
