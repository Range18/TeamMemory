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
    private readonly ITelegramBotClient? _botClient;

    public TelegramChatRequestService(IConfiguration configuration)
    {
        var token = configuration["Telegram:BotToken"];
        if (!string.IsNullOrWhiteSpace(token))
            _botClient = new TelegramBotClient(token);
    }

    public async Task<string> CreatePreparedButtonAsync(long managerTelegramId, CancellationToken ct)
    {
        if (_botClient is null)
            throw new InvalidOperationException("Не задан Telegram:BotToken.");

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
        var prepared = await _botClient.SavePreparedKeyboardButton(
            managerTelegramId, button, ct);

        return prepared.Id;
    }
}
