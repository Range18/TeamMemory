using Medallion.Threading.Postgres;
using Microsoft.EntityFrameworkCore;

namespace AnalyticsService.Infrastructure.Persistence;

public static class DatabaseSessionLock
{
    public const long Initializer = 7421983100;
    public const long Worker = 7421983101;
    public const long Publisher = 7421983102;

    public static async Task<PostgresDistributedLockHandle?> TryAcquire(
        AnalyticsDbContext db, long key, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return await Create(db, key).TryAcquireAsync(cancellationToken: ct);
    }

    public static async Task<PostgresDistributedLockHandle> Acquire(
        AnalyticsDbContext db, long key, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        return await Create(db, key).AcquireAsync(cancellationToken: ct);
    }

    private static PostgresDistributedLock Create(AnalyticsDbContext db, long key) =>
        new(new PostgresAdvisoryLockKey(key), db.Database.GetDbConnection());
}
