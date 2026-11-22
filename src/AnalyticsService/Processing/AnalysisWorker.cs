using System.Text.Json;
using AnalyticsService.Analysis;
using AnalyticsService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AnalyticsService.Processing;

public sealed class AnalyticsDatabaseInitializer(AnalyticsStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.Initialize(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class AnalysisWorker(IDbContextFactory<AnalyticsDbContext> contextFactory, AnalyticsStore store,
    IMessageBatchAnalyzer analyzer, IOptions<AnalyticsOptions> options, ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await contextFactory.CreateDbContextAsync(stoppingToken);
                await using var workerLock = await DatabaseSessionLock.TryAcquire(db, DatabaseSessionLock.Worker, stoppingToken);
                if (workerLock is not null)
                {
                    await store.RecalculateRisks(db, stoppingToken);
                    await store.BuildBatches(db, stoppingToken);
                    var batch = await store.NextBatch(db, stoppingToken);
                    if (batch is not null) await Process(db, batch, stoppingToken);
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

    private async Task Process(AnalyticsDbContext db, PendingAnalysis batch, CancellationToken ct)
    {
        var id = batch.Input.BatchId;
        try
        {
            if (batch.Result is null && batch.Attempts >= options.Value.MaxAttempts)
            {
                await AnalyticsStore.FailBatch(db, id, "failed", "Maximum attempts reached.", ct);
                return;
            }
            AnalysisResult result;
            if (batch.Result is not null) result = AnalysisJson.Read<AnalysisResult>(batch.Result);
            else
            {
                await AnalyticsStore.BeginAttempt(db, id, ct);
                result = await analyzer.Analyze(batch.Input, ct);
                await AnalyticsStore.SaveResult(db, id, result, ct);
            }
            var applied = TaskChangeApplier.Apply(batch.Input, result, options.Value.MinimumConfidence);
            await AnalyticsStore.Apply(db, batch.Input, applied, ct);
            logger.LogInformation("Applied batch {BatchId}, changes: {ChangeCount}", id, applied.Changes.Length);
        }
        catch (Exception exception) when (exception is AnalysisValidationException or JsonException)
        {
            db.ChangeTracker.Clear();
            await AnalyticsStore.FailBatch(db, id, "needs_review", "Invalid or ambiguous model result.", ct);
            logger.LogWarning("Batch {BatchId} requires review", id);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            if (batch.Attempts + 1 >= options.Value.MaxAttempts)
                await AnalyticsStore.FailBatch(db, id, "failed", "Maximum attempts reached.", ct);
            else
                await AnalyticsStore.RetryBatch(db, id, batch.Attempts + 1, ct);
            logger.LogWarning("Batch {BatchId} attempt failed: {ErrorType}", id, exception.GetType().Name);
        }
    }
}
