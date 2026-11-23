namespace AnalyticsService.Analysis;

public sealed record AppliedChange(TrackedTask Task, AnalysisChange Change, DateTimeOffset EffectiveAt);
