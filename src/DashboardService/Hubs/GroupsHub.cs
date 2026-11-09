using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;

namespace GroupDashboard.Api.Hubs;

[Authorize]
public sealed class GroupsHub : Hub
{
    public static string ManagerGroup(long managerTelegramId) => $"manager:{managerTelegramId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!long.TryParse(userId, out var managerTelegramId) || managerTelegramId <= 0)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ManagerGroup(managerTelegramId));
        await base.OnConnectedAsync();
    }
}
