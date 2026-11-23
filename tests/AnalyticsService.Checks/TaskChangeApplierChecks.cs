using AnalyticsService.Analysis;
using Shared.Contracts.Analytics;

internal static class TaskChangeApplierChecks
{
    public static void Run()
    {
        CheckNewTaskSequence();
        CheckRejectedBatchDoesNotMutateInput();
        CheckEvidenceOrderAndVersions();
        Console.WriteLine("PASS: task change sequences, rejected-batch isolation, evidence order and versions");
    }

    private static void CheckNewTaskSequence()
    {
        var input = Input();
        var creation = Change(input, "create_task") with
        {
            NewTaskRef = "new-task",
            EmployeeId = 7,
            Title = "Implement analytics"
        };
        var blocker = Change(input, "add_blocker") with { NewTaskRef = "new-task", Description = "Missing API" };
        var result = Apply(input,
            creation,
            Change(input, "set_estimate") with { NewTaskRef = "new-task", EstimateDays = 2 },
            blocker,
            blocker with { ChangeRef = "duplicate-blocker", Description = "missing api" },
            Change(input, "resolve_blocker") with { NewTaskRef = "new-task", Description = "Missing API" },
            Change(input, "set_next_step") with { NewTaskRef = "new-task", Description = "Deploy" },
            Change(input, "record_progress") with { NewTaskRef = "new-task", Description = "Implementation ready" },
            Change(input, "set_status") with { NewTaskRef = "new-task", Status = "done" });

        var task = result.Tasks.Single();
        Assert(task.Status == "done" && task.Blockers.Count == 0, "Task lifecycle was not applied.");
        Assert(task.EstimateDays == 2 && task.NextStep == "Deploy", "Task details were lost.");
        Assert(task.Version == 1 && result.Changes.Length == 8, "Version must increment once per batch.");
        Assert(result.Events.OfType<NewTaskReported>().Count() == 1, "Missing new task event.");
        Assert(result.Events.OfType<BlockerDetected>().Count() == 1, "Duplicate blocker event was emitted.");
        Assert(result.Events.All(e => e.TaskId == task.Id && e.TeamId == input.TeamId
            && e.CorrelationId == input.BatchId.ToString()), "Event identifiers were not preserved.");
    }

    private static void CheckRejectedBatchDoesNotMutateInput()
    {
        var input = Input();
        var original = ExistingTask(input);
        input = input with { Tasks = [original] };
        var snapshot = AnalysisJson.Write(original);
        ExpectRejected(() => Apply(input,
            Change(input, "add_blocker") with { TaskId = original.Id, Description = "Missing API" },
            Change(input, "set_status") with { TaskId = original.Id, Status = "done" }));
        Assert(AnalysisJson.Write(original) == snapshot, "Rejected batch modified the input task or blocker list.");

        var progress = Change(input, "record_progress") with { TaskId = original.Id, Description = "Updated" };
        ExpectRejected(() => Apply(input, progress, progress));
        ExpectRejected(() => Apply(input, progress with { Confidence = .84 }));
        ExpectRejected(() => Apply(input, progress with { EvidenceEventIds = [Guid.NewGuid()] }));
        ExpectRejected(() => Apply(input, progress with { NewTaskRef = "another-reference" }));
        ExpectRejected(() => Apply(input, progress with { Action = "unknown" }));
        ExpectRejected(() => Apply(input, Change(input, "set_estimate") with { TaskId = original.Id, EstimateDays = double.NaN }));
    }

    private static void CheckEvidenceOrderAndVersions()
    {
        var input = Input();
        var original = ExistingTask(input);
        original.AtRisk = true;
        var earlierMessage = input.Messages[0];
        var laterMessage = earlierMessage with { EventId = Guid.NewGuid(), OccurredAt = earlierMessage.OccurredAt.AddHours(1) };
        input = input with { Tasks = [original], Messages = [earlierMessage, laterMessage] };
        var result = Apply(input,
            Change(input, "record_progress") with
            {
                TaskId = original.Id,
                Description = "Later update",
                EvidenceEventIds = [laterMessage.EventId]
            },
            Change(input, "set_status") with { TaskId = original.Id, Status = "done" });

        var updated = result.Tasks.Single();
        Assert(updated.Version == original.Version + 1, "Existing task version must increment once.");
        Assert(updated.LastActivityAt == laterMessage.OccurredAt, "Last activity must not move backwards within a batch.");
        Assert(!updated.AtRisk && original.AtRisk, "Completion must clear risk only on the task copy.");
        Assert(result.Changes[0].EffectiveAt == laterMessage.OccurredAt
            && result.Changes[1].EffectiveAt == earlierMessage.OccurredAt, "History must preserve evidence timestamps.");
        var stale = input with { Messages = [earlierMessage with { OccurredAt = original.LastActivityAt.AddSeconds(-1) }] };
        ExpectRejected(() => Apply(stale, Change(stale, "record_progress") with { TaskId = original.Id, Description = "Stale update" }));
    }

    private static AnalysisInput Input() => new(Guid.NewGuid(), "team-a",
        [new AnalysisMessage(Guid.NewGuid(), 1, 7, "developer", "Work update", DateTimeOffset.UtcNow)], []);

    private static TrackedTask ExistingTask(AnalysisInput input) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = input.TeamId,
        EmployeeId = 7,
        Title = "Existing task",
        Version = 3,
        StartedAt = input.Messages[0].OccurredAt.AddDays(-1),
        LastActivityAt = input.Messages[0].OccurredAt.AddMinutes(-1)
    };

    private static AnalysisChange Change(AnalysisInput input, string action) => new()
    {
        ChangeRef = Guid.NewGuid().ToString(),
        Action = action,
        Confidence = .99,
        EvidenceEventIds = [input.Messages[0].EventId]
    };

    private static AppliedAnalysis Apply(AnalysisInput input, params AnalysisChange[] changes) =>
        TaskChangeApplier.Apply(input,
            new AnalysisResult { SchemaVersion = 1, Changes = changes, Ambiguities = [] }, .85);

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (AnalysisValidationException) { return; }
        throw new Exception("Invalid change was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
