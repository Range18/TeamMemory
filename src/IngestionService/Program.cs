using IngestionService.Contracts;
using MassTransit;
using Shared.Contracts;
using Shared.Contracts.Telegram;

var builder = WebApplication.CreateBuilder(args);

var rabbitMq = builder.Configuration.GetSection("RabbitMq");

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(
            rabbitMq["Host"] ?? "localhost",
            rabbitMq["VirtualHost"] ?? "/",
            h =>
            {
                h.Username(rabbitMq["Username"] ?? "teammemory");
                h.Password(rabbitMq["Password"] ?? "teammemory");
            });
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/telegram/messages", async (
        PublishTelegramMessageRequest request,
        IPublishEndpoint publishEndpoint) =>
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "Text is required." });
        }

        var message = new TelegramMessageCreated
        {
            Source = IntegrationEventSources.Telegram,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ChatId = request.ChatId,
            MessageId = request.MessageId,
            SenderUsername = request.SenderUsername,
            Text = request.Text,
            ReplyToMessageId = request.ReplyToMessageId
        };

        await publishEndpoint.Publish(message);

        return Results.Accepted(value: new { eventId = message.EventId });
    });
}

app.Run();
