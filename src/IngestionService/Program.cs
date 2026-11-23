using DotNetEnv;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
builder.Services.AddHealthChecks();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

var webhookSecret = builder.Configuration["TelegramWebhook:SecretToken"];
if ((!builder.Environment.IsDevelopment() || !string.IsNullOrEmpty(webhookSecret))
    && (string.IsNullOrEmpty(webhookSecret) || webhookSecret.Length > 256
        || webhookSecret.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
{
    throw new InvalidOperationException("TelegramWebhook:SecretToken must contain 1-256 ASCII letters, digits, underscores or hyphens.");
}

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

// Explicit maintenance command: configures Telegram without starting the HTTP host or bus.
if (args.Contains("--register-telegram-webhook", StringComparer.Ordinal))
{
    var baseUrl = app.Configuration["WEBHOOK_BASE_URL"];
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
        || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        throw new InvalidOperationException("WEBHOOK_BASE_URL must be an HTTPS origin.");

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.Services.GetRequiredService<ITelegramBotClient>().SetWebhook(
            $"{baseUrl!.TrimEnd('/')}/api/telegram/webhook",
            allowedUpdates: new[] { UpdateType.Message }, secretToken: webhookSecret,
            cancellationToken: timeout.Token);
        app.Logger.LogInformation("Telegram webhook registered successfully.");
    }
    catch (Exception)
    {
        // Telegram request URLs contain the bot token. Do not log exception details.
        app.Logger.LogError("Telegram webhook registration failed. Check connectivity, bot token and HTTPS origin.");
        Environment.ExitCode = 1;
    }
    await app.DisposeAsync();
    return;
}

app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/api/telegram/webhook", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(webhookSecret))
    {
        var supplied = context.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(webhookSecret)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next(context);
});

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

        try
        {
            var botClient = app.Services.GetRequiredService<ITelegramBotClient>();
            await botClient.SetWebhook(webhookUrl, allowedUpdates: new[] { UpdateType.Message }, secretToken: webhookSecret);
            app.Logger.LogInformation("Telegram webhook set: {WebhookUrl}", webhookUrl);
        }
        catch (Exception)
        {
            // Do not stop the service when the token is missing or invalid:
            // the Development-only POST /api/dev/telegram/messages still works.
            app.Logger.LogError("Failed to set Telegram webhook. Check TELEGRAM_BOT_TOKEN and WEBHOOK_BASE_URL.");
        }
    }
}

app.Run();
