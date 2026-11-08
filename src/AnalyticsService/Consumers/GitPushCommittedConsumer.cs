using MassTransit;
using Shared.Contracts.Git;

namespace AnalyticsService.Consumers;

public sealed class GitPushCommittedConsumer : IConsumer<GitPushCommitted>
{
    private readonly ILogger<GitPushCommittedConsumer> _logger;

    public GitPushCommittedConsumer(ILogger<GitPushCommittedConsumer> logger)
        => _logger = logger;

    public Task Consume(ConsumeContext<GitPushCommitted> context)
    {
        _logger.LogInformation("Received push to branch {Branch}", context.Message.Branch);
        return Task.CompletedTask;
    }
}
