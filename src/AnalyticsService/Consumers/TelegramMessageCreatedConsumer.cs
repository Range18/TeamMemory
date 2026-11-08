using MassTransit;
using Shared.Contracts.Telegram;

namespace AnalyticsService.Consumers;

public sealed class TelegramMessageCreatedConsumer : IConsumer<TelegramMessageCreated>
{
    private readonly ILogger<TelegramMessageCreatedConsumer> _logger;

    public TelegramMessageCreatedConsumer(ILogger<TelegramMessageCreatedConsumer> logger)
        => _logger = logger;

    public Task Consume(ConsumeContext<TelegramMessageCreated> context)
    {
        _logger.LogInformation(
            "Получено сообщение из Telegram: ChatId={ChatId}, MessageId={MessageId}, Sender={SenderUsername}",
            context.Message.ChatId,
            context.Message.MessageId,
            context.Message.SenderUsername);

        return Task.CompletedTask;
    }
}
