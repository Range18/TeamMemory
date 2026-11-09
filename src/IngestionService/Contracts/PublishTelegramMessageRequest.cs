namespace IngestionService.Contracts;

public sealed record PublishTelegramMessageRequest(
    long ChatId,
    long MessageId,
    string SenderUsername,
    string Text,
    long? ReplyToMessageId = null,
    long? SenderId = null,
    long? ThreadId = null);
