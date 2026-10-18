namespace Kooshi.Benchmark.ProcessBound.Api;

public enum WorkflowStage { Created, Interpreting, Collecting, Validating, AwaitingConfirmation, Executing, Succeeded, Failed, Cancelled }

public static class WorkflowTransitions
{
    private static readonly Dictionary<WorkflowStage, WorkflowStage[]> Allowed = new()
    {
        [WorkflowStage.Created] = [WorkflowStage.Interpreting, WorkflowStage.Cancelled],
        [WorkflowStage.Interpreting] = [WorkflowStage.Validating, WorkflowStage.Failed],
        [WorkflowStage.Collecting] = [WorkflowStage.Interpreting, WorkflowStage.Cancelled],
        [WorkflowStage.Validating] = [WorkflowStage.Collecting, WorkflowStage.AwaitingConfirmation, WorkflowStage.Failed],
        [WorkflowStage.AwaitingConfirmation] = [WorkflowStage.Interpreting, WorkflowStage.Executing, WorkflowStage.Cancelled],
        [WorkflowStage.Executing] = [WorkflowStage.Succeeded, WorkflowStage.Failed],
        [WorkflowStage.Failed] = [WorkflowStage.Interpreting, WorkflowStage.Executing, WorkflowStage.Cancelled],
        [WorkflowStage.Succeeded] = [],
        [WorkflowStage.Cancelled] = []
    };

    public static void Move(ExecutionState state, WorkflowStage next)
    {
        if (!Allowed[state.Stage].Contains(next)) throw new InvalidOperationException($"Invalid transition {state.Stage} -> {next}");
        state.Events.Add(new("transition", $"{state.Stage} -> {next}", DateTimeOffset.UtcNow));
        state.Stage = next;
    }
}

public sealed record ConversationEntry(string Role, string Content, DateTimeOffset At);
public sealed record ToolAttempt(int Number, string Name, string Arguments, string Outcome, string? Error, DateTimeOffset At);
public sealed record ExecutionEvent(string Type, string Detail, DateTimeOffset At);
public sealed record BusinessEffect(string BookingId, DateTimeOffset At);

public sealed class ExecutionState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Architecture { get; set; } = "ProcessBound";
    public string ScenarioId { get; set; } = "demo-booking";
    public WorkflowStage Stage { get; set; } = WorkflowStage.Created;
    public string Status => Stage switch { WorkflowStage.AwaitingConfirmation => "ready", WorkflowStage.Collecting => "collecting", _ => Stage.ToString().ToLowerInvariant() };
    public Booking Booking { get; set; } = new();
    public IntentExecutionContract? Contract { get; set; }
    public string? ConfirmedArgumentsHash { get; set; }
    public List<FieldDefinition> Fields { get; set; } = [];
    public List<string> MissingFields { get; set; } = [];
    public List<string> InvalidFields { get; set; } = [];
    public List<string> AuthorizationDecisions { get; set; } = [];
    public int ConfirmationViolations { get; set; }
    public int UnauthorizedCommittedExecutions { get; set; }
    public string? Response { get; set; }
    public List<ConversationEntry> Messages { get; set; } = [];
    public List<ToolAttempt> ToolAttempts { get; set; } = [];
    public List<BusinessEffect> BusinessEffects { get; set; } = [];
    public List<string> ValidationErrors { get; set; } = [];
    public List<string> Failures { get; set; } = [];
    public List<ExecutionEvent> Events { get; set; } = [];
    public bool Confirmed { get; set; }
    public int RecoveryAttempts { get; set; }
    public int SuccessfulToolResponses { get; set; }
    public int DuplicateSideEffects { get; set; }
    public int UnauthorizedToolExecutions { get; set; }
    public int LlmCalls { get; set; }
    public int InteractionTurns => Messages.Count(m => m.Role == "user");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public double ElapsedMilliseconds => ((CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalMilliseconds;
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public double? LlmRequestMilliseconds { get; set; }
    public double? DownstreamOperationMilliseconds { get; set; }
    public Kooshi.Benchmark.Shared.ExecutionMetrics Metrics => Kooshi.Benchmark.Shared.ExecutionMetricRules.Create(
        Architecture, ScenarioId, Id, Status, StartedAt, CompletedAt, Messages.Count(m => m.Role == "user"),
        LlmCalls, InputTokens, OutputTokens, ToolAttempts.Select(t => t.Outcome), BusinessEffects.Count,
        DuplicateSideEffects, UnauthorizedCommittedExecutions, ConfirmationViolations, Events.Select(e => e.Type), Failures, LlmRequestMilliseconds, DownstreamOperationMilliseconds);

}


