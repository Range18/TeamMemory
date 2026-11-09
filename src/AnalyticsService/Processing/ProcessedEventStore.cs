namespace AnalyticsService.Processing;

// Bounded, process-local deduplication. Replace with a transactional inbox when
// task state is persisted, to support restarts and multiple Analytics instances.
public sealed class ProcessedEventStore
{
    private const int Capacity = 100_000;
    private readonly HashSet<string> _processed = [];
    private readonly Queue<string[]> _order = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool> Process(Guid eventId, Guid? messageId, Func<Task> handler,
        CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId must not be empty.", nameof(eventId));

        string[] keys = messageId is { } id
            ? [$"event:{eventId}", $"message:{id}"]
            : [$"event:{eventId}"];

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (keys.Any(_processed.Contains))
                return false;

            await handler();
            foreach (var key in keys)
                _processed.Add(key);
            _order.Enqueue(keys);
            while (_order.Count > Capacity)
                foreach (var key in _order.Dequeue())
                    _processed.Remove(key);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
