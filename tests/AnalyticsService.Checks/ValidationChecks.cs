using System.ComponentModel.DataAnnotations;
using AnalyticsService.Analysis;
using AnalyticsService.Contracts;
using Shared.Contracts.Telegram;

internal static class ValidationChecks
{
    public static void Run()
    {
        var message = new TelegramMessageCreated
        {
            Source = "telegram",
            CorrelationId = "validation-check",
            ChatId = -100,
            MessageId = 1,
            SenderId = 7,
            SenderUsername = "developer",
            Text = "Work update"
        };
        var dto = TelegramMessageDto.FromEvent(message, "team-a");
        dto.Validate();
        (dto with { MessageId = long.MaxValue, SenderId = long.MaxValue, Text = new string('x', 32000) }).Validate();

        TelegramMessageDto[] invalidMessages =
        [
            dto with { EventId = Guid.Empty },
            dto with { SenderId = null },
            dto with { SenderId = 0 },
            dto with { MessageId = 0 },
            dto with { Text = "   " },
            dto with { Text = new string('x', 32001) },
            dto with { Source = "github" },
            dto with { SourceInstance = " " },
            dto with { CorrelationId = "" },
            dto with { OccurredAt = default },
            dto with { TeamId = null },
            dto with { TeamId = " " }
        ];
        foreach (var invalid in invalidMessages)
        {
            try { invalid.Validate(); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid Telegram DTO passed validation.");
        }

        AssertValid(new AnalyticsOptions(), true);
        AssertValid(new LlmApiOptions(), true);
        AssertValid(new AnalyticsOptions { MaxWaitSeconds = 59 }, false);
        AssertValid(new AnalyticsOptions { MaxMessages = 101 }, false);
        AssertValid(new AnalyticsOptions { PollSeconds = 0 }, false);
        AssertValid(new AnalyticsOptions { MinimumConfidence = double.NaN }, false);
        AssertValid(new AnalyticsOptions { RiskMultiplier = double.PositiveInfinity }, false);
        AssertValid(new AnalyticsOptions { RiskMultiplier = double.NaN }, false);
        AssertValid(new AnalyticsOptions { RiskMultiplier = double.Epsilon }, true);
        AssertValid(new LlmApiOptions { MaxTokens = 0 }, false);
        AssertValid(new LlmApiOptions { Endpoint = "not-a-url" }, false);
        AssertValid(new LlmApiOptions { Endpoint = "file:///model" }, false);
        AssertValid(new LlmApiOptions { Model = " " }, false);
        AssertValid(new LlmApiOptions { TimeoutSeconds = 0 }, false);
        Console.WriteLine("PASS: DTO boundaries, required fields, finite numbers and cross-property validation");
    }

    private static void AssertValid(object value, bool expected)
    {
        var actual = Validator.TryValidateObject(value, new ValidationContext(value), [], validateAllProperties: true);
        if (actual != expected) throw new Exception($"Unexpected validation result for {value.GetType().Name}.");
    }
}
