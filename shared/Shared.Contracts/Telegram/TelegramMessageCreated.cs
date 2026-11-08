namespace Shared.Contracts.Telegram;

public record TelegramMessageCreated : IntegrationEventBase
{
    public required long ChatId { get; init; }
    public required long MessageId { get; init; }
    public required string SenderUsername { get; init; }
    public required string Text { get; init; }
    public long? ReplyToMessageId { get; init; }
}
