using GroupDashboard.Api.Contracts;
using GroupDashboard.Api.Auth;
using GroupDashboard.Api.Data;
using GroupDashboard.Api.Entities;
using GroupDashboard.Api.Hubs;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GroupDashboard.Api.Messaging;

public sealed class GroupUpdatesConsumer(
    AppDbContext db,
    IHubContext<GroupsHub> hub,
    GroupAccessPolicy access,
    ILogger<GroupUpdatesConsumer> logger) : IConsumer<GroupUpdatedMessage>
{
    public async Task Consume(ConsumeContext<GroupUpdatedMessage> context)
    {
        var message = context.Message;
        Validate(message);

        var result = await SaveGroupAsync(message, context.CancellationToken);
        if (result is null)
            return;

        foreach (var managerId in access.NotificationRecipients(result.ManagerTelegramIds))
        {
            await hub.Clients.Group(GroupsHub.ManagerGroup(managerId))
                .SendAsync("GroupUpdated", result.Group, context.CancellationToken);
        }

        logger.LogInformation("Updated Telegram group {ChatId} from RabbitMQ event.", message.ChatId);
    }

    private async Task<GroupUpdateResult?> SaveGroupAsync(
        GroupUpdatedMessage message,
        CancellationToken cancellationToken)
    {
        var updatedAtUtc = message.UpdatedAtUtc.ToUniversalTime();
        var group = await db.Groups.SingleOrDefaultAsync(
            x => x.ChatId == message.ChatId, cancellationToken);
        var changed = false;

        if (group is null)
        {
            group = new GroupEntity
            {
                ChatId = message.ChatId,
                Title = message.Title.Trim(),
                Username = message.Username,
                MemberCount = message.MemberCount,
                Status = message.Status,
                UpdatedAtUtc = updatedAtUtc
            };
            db.Groups.Add(group);
            changed = true;
        }
        else if (updatedAtUtc >= group.UpdatedAtUtc)
        {
            group.Title = message.Title.Trim();
            group.Username = message.Username;
            group.MemberCount = message.MemberCount;
            group.Status = message.Status;
            group.UpdatedAtUtc = updatedAtUtc;
            changed = true;
        }

        if (message.ManagerTelegramId is long managerId)
        {
            var alreadyLinked = await db.GroupManagers.AnyAsync(
                x => x.ChatId == message.ChatId && x.ManagerTelegramId == managerId,
                cancellationToken);

            if (!alreadyLinked)
            {
                db.GroupManagers.Add(new GroupManagerEntity
                {
                    ChatId = message.ChatId,
                    ManagerTelegramId = managerId,
                    ConnectedAtUtc = DateTimeOffset.UtcNow
                });
                changed = true;
            }
        }

        if (!changed)
            return null;

        await db.SaveChangesAsync(cancellationToken);

        var managerIds = await db.GroupManagers
            .Where(x => x.ChatId == message.ChatId)
            .Select(x => x.ManagerTelegramId)
            .ToArrayAsync(cancellationToken);

        var dto = new GroupDto(
            group.ChatId.ToString(),
            group.Title,
            group.Username,
            group.MemberCount,
            group.Status,
            group.UpdatedAtUtc);

        return new GroupUpdateResult(dto, managerIds);
    }

    private static void Validate(GroupUpdatedMessage message)
    {
        if (message.ChatId == 0)
            throw new InvalidDataException("ChatId must not be zero.");
        if (string.IsNullOrWhiteSpace(message.Title) || message.Title.Length > 256)
            throw new InvalidDataException("Title is required and must be at most 256 characters.");
        if (message.MemberCount is < 0)
            throw new InvalidDataException("MemberCount must not be negative.");
        if (string.IsNullOrWhiteSpace(message.Status) || message.Status.Length > 32)
            throw new InvalidDataException("Status is required and must be at most 32 characters.");
        if (message.UpdatedAtUtc == default)
            throw new InvalidDataException("UpdatedAtUtc is required.");
        if (message.ManagerTelegramId is <= 0)
            throw new InvalidDataException("ManagerTelegramId must be positive when specified.");
    }

    private sealed record GroupUpdateResult(GroupDto Group, long[] ManagerTelegramIds);
}
