namespace GroupDashboard.Api.Contracts;

// Контракт входящего сообщения от внешнего Telegram-сервиса.
// ManagerTelegramId заполняется для события chat_shared (первичная привязка группы).
public sealed record GroupUpdatedMessage(
    long ChatId,
    string Title,
    string? Username,
    int? MemberCount,
    string Status,
    DateTimeOffset UpdatedAtUtc,
    long? ManagerTelegramId = null);
