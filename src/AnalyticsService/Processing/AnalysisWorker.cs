using System.Text.Json;
using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AnalyticsService.Processing;

public sealed class AnalyticsDatabaseInitializer(AnalyticsStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.Initialize(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class AnalysisWorker(NpgsqlDataSource dataSource, AnalyticsStore store,
    IMessageBatchAnalyzer analyzer, IOptions<AnalyticsOptions> options, ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                await using var workerLock = await DatabaseSessionLock.TryAcquire(connection, AnalyticsStore.WorkerLock, stoppingToken);
                if (workerLock is not null)
                {
                    await store.RecalculateRisks(connection, stoppingToken);
                    await store.BuildBatches(connection, stoppingToken);
                    var batch = await store.NextBatch(connection, stoppingToken);
                    if (batch is not null) await Process(connection, batch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Analytics iteration failed: {ErrorType}", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task Process(NpgsqlConnection connection, PendingAnalysis batch, CancellationToken ct)
    {
        var id = batch.Input.BatchId;
        try
        {
            if (batch.Result is null && batch.Attempts >= options.Value.MaxAttempts)
            {
                await AnalyticsStore.FailBatch(connection, id, "failed", "Maximum attempts reached.", ct);
                return;
            }
            AnalysisResult result;
            if (batch.Result is not null) result = AnalysisJson.Read<AnalysisResult>(batch.Result);
            else
            {
                await AnalyticsStore.BeginAttempt(connection, id, ct);
                result = await analyzer.Analyze(batch.Input, ct);
                await AnalyticsStore.SaveResult(connection, id, result, ct);
            }
            var applied = TaskChangeApplier.Apply(batch.Input, result, options.Value.MinimumConfidence);
            await AnalyticsStore.Apply(connection, batch.Input, applied, ct);
            logger.LogInformation("Applied batch {BatchId}, changes: {ChangeCount}", id, applied.Changes.Length);
        }
        catch (Exception exception) when (exception is AnalysisValidationException or JsonException)
        {
            await AnalyticsStore.FailBatch(connection, id, "needs_review", "Invalid or ambiguous model result.", ct);
            logger.LogWarning("Batch {BatchId} requires review", id);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            if (batch.Attempts + 1 >= options.Value.MaxAttempts)
                await AnalyticsStore.FailBatch(connection, id, "failed", "Maximum attempts reached.", ct);
            else
                await AnalyticsStore.RetryBatch(connection, id, batch.Attempts + 1, ct);
            logger.LogWarning("Batch {BatchId} attempt failed: {ErrorType}", id, exception.GetType().Name);
        }
    }
}

public sealed class DatabaseSessionLock(NpgsqlConnection connection, long key) : IAsyncDisposable
{
    public static async Task<DatabaseSessionLock?> TryAcquire(NpgsqlConnection connection, long key, CancellationToken ct)
    {
        await using var command = AnalyticsStore.Command(connection, "SELECT pg_try_advisory_lock($1)", key);
        return (bool)(await command.ExecuteScalarAsync(ct))! ? new(connection, key) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.State != System.Data.ConnectionState.Open) return;
        await AnalyticsStore.Execute(connection, "SELECT pg_advisory_unlock($1)", CancellationToken.None, key);
    }
}
