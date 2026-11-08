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
            "Successfully received event from RabbitMQ! EventId: {EventId}, ChatId: {ChatId}, Sender: {SenderUsername}",
            context.Message.EventId,
            context.Message.ChatId,
            context.Message.SenderUsername);

        return Task.CompletedTask;
    }
}
