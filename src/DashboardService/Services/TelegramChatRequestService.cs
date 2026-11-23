using System.Security.Cryptography;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace GroupDashboard.Api.Services;

/// <summary>
/// Единственное место этого backend, которое вызывает Telegram Bot API:
/// подготавливает request_chat-кнопку для конкретного пользователя Mini App.
/// Получение Updates остаётся во внешнем Telegram Collector.
/// </summary>
public sealed class TelegramChatRequestService
{
    private readonly string? _token;
    private ITelegramBotClient? _botClient;

    public TelegramChatRequestService(IConfiguration configuration)
    {
        // Do not construct the client here: an empty or invalid token must not
        // break group listing and digests. Only request_chat needs Telegram.
        _token = configuration["Telegram:BotToken"];
    }

    public async Task<string> CreatePreparedButtonAsync(long managerTelegramId, CancellationToken ct)
    {
        var botClient = GetBotClient();

        var requestChat = new KeyboardButtonRequestChat(
            requestId: RandomNumberGenerator.GetInt32(1, int.MaxValue),
            chatIsChannel: false)
        {
            RequestTitle = true,
            RequestUsername = true,

            UserAdministratorRights = new ChatAdministratorRights
            {
                CanManageChat = true,
                CanInviteUsers = true,
                CanPromoteMembers = true
            },

            BotAdministratorRights = new ChatAdministratorRights
            {
                CanManageChat = true
            }
        };

        var button = KeyboardButton.WithRequestChat("Добавить бота в группу", requestChat);
        var prepared = await botClient.SavePreparedKeyboardButton(
            managerTelegramId, button, ct);

        return prepared.Id;
    }

    private ITelegramBotClient GetBotClient()
    {
        if (_botClient is not null)
            return _botClient;
        if (string.IsNullOrWhiteSpace(_token))
            throw new InvalidOperationException("Не задан Telegram:BotToken.");
        return _botClient = new TelegramBotClient(_token);
    }
}
