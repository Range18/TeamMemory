using DotNetEnv;
using IngestionService.Contracts;
using MassTransit;
using Shared.Contracts;
using Shared.Contracts.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

Env.NoClobber().TraversePath().Load(Path.Combine(AppContext.BaseDirectory, ".env"));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var rabbitMq = builder.Configuration.GetSection("RabbitMq");
builder.Services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
var telegramBotToken = builder.Configuration["TELEGRAM_BOT_TOKEN"];

if (string.IsNullOrWhiteSpace(telegramBotToken))
{
    throw new InvalidOperationException(
        "TELEGRAM_BOT_TOKEN is not configured. Add it to the .env file in the repository root.");
}

builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(telegramBotToken));

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
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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

        if (request.SenderId is null or <= 0)
            return Results.BadRequest(new { error = "A stable positive SenderId is required." });

        var message = new TelegramMessageCreated
        {
            Source = IntegrationEventSources.Telegram,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ChatId = request.ChatId,
            MessageId = request.MessageId,
            SenderUsername = request.SenderUsername,
            Text = request.Text,
            ReplyToMessageId = request.ReplyToMessageId,
            SenderId = request.SenderId,
            ThreadId = request.ThreadId
        };

        await publishEndpoint.Publish(message);

        return Results.Accepted(value: new { eventId = message.EventId });
    });
}

app.MapPost("/api/telegram/webhook", async (
    Update update,
    IPublishEndpoint publishEndpoint,
    ILogger<Program> logger) =>
{
    if (update.Message is not { } message || message.From is null
        || string.IsNullOrWhiteSpace(message.Text ?? message.Caption))
    {
        return Results.Ok();
    }

    var telegramMessage = new TelegramMessageCreated
    {
        Source = IntegrationEventSources.Telegram,
        CorrelationId = Guid.NewGuid().ToString("N"),
        ChatId = message.Chat.Id,
        MessageId = message.MessageId,
        SenderUsername = message.From?.Username ?? message.From?.FirstName ?? "unknown",
        Text = message.Text ?? message.Caption ?? string.Empty,
        ReplyToMessageId = message.ReplyToMessage?.MessageId,
        SenderId = message.From!.Id,
        ThreadId = message.MessageThreadId,
        OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(message.Date, DateTimeKind.Utc))
    };

    logger.LogInformation(
        "Received Telegram webhook. ChatId: {ChatId}, MessageId: {MessageId}. Publishing event with EventId: {EventId}",
        telegramMessage.ChatId,
        telegramMessage.MessageId,
        telegramMessage.EventId);

    await publishEndpoint.Publish(telegramMessage);

    return Results.Ok();
});

if (app.Environment.IsDevelopment())
{
    var webhookBaseUrl = app.Configuration["WEBHOOK_BASE_URL"];

    if (string.IsNullOrWhiteSpace(webhookBaseUrl))
    {
        app.Logger.LogWarning("WEBHOOK_BASE_URL is not configured. Skipping SetWebhook.");
    }
    else
    {
        var webhookUrl = $"{webhookBaseUrl.TrimEnd('/')}/api/telegram/webhook";
        var botClient = app.Services.GetRequiredService<ITelegramBotClient>();

        try
        {
            await botClient.SetWebhook(webhookUrl, allowedUpdates: new[] { UpdateType.Message });
            app.Logger.LogInformation("Telegram webhook set: {WebhookUrl}", webhookUrl);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Failed to set Telegram webhook: {WebhookUrl}", webhookUrl);
        }
    }
}

app.Run();
