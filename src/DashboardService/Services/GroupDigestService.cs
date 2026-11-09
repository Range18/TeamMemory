using GroupDashboard.Api.Contracts;
using GroupDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GroupDashboard.Api.Services;

public sealed class GroupDigestService(AppDbContext db, IOptions<DigestOptions> options, TimeProvider timeProvider)
{
    public async Task<DigestDto?> Get(long chatId, long managerId, DateOnly? requestedDate, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking()
            .Where(g => g.ChatId == chatId && g.Managers.Any(m => m.ManagerTelegramId == managerId))
            .Select(g => new { g.Title }).SingleOrDefaultAsync(ct);
        if (group is null) return null;
        var settings = options.Value;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var now = timeProvider.GetUtcNow();
        var date = requestedDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var (from, to) = DigestFormatter.UtcPeriod(date, zone);
        var events = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.ChatId == chatId && e.OccurredAtUtc >= from && e.OccurredAtUtc < to)
            .ToArrayAsync(ct);
        return DigestFormatter.Format(chatId, group.Title, date, settings, events, now);
    }
}
