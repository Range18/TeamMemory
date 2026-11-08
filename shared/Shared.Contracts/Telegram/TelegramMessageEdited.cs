namespace Shared.Contracts.Telegram;

public record TelegramMessageEdited : IntegrationEventBase
{
    public required long ChatId { get; init; }
    public required long MessageId { get; init; }
    public required string NewText { get; init; }
    public required string EditedByUsername { get; init; }
}
