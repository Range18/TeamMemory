using Shared.Contracts.Analytics;

namespace AnalyticsService.Analysis;

public sealed record AppliedAnalysis(TrackedTask[] Tasks, AppliedChange[] Changes, TaskAnalyticsEvent[] Events);
