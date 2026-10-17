namespace Kooshi.Benchmark.DirectLlm.Api;

public sealed record ConversationEntry(string Role, string Content, DateTimeOffset At);
public sealed record ToolAttempt(int Number, string Name, string Arguments, string Outcome, string? Error, DateTimeOffset At);
public sealed record ExecutionEvent(string Type, string Detail, DateTimeOffset At);
public sealed record BusinessEffect(string BookingId, DateTimeOffset At);

public sealed class ExecutionState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Architecture { get; set; } = "DirectLlm";
    public string ScenarioId { get; set; } = "demo-booking";
    public string Status { get; set; } = "collecting";
    public Booking Booking { get; set; } = new();
    public string? Response { get; set; }
    public List<ConversationEntry> Messages { get; set; } = [];
    public List<ToolAttempt> ToolAttempts { get; set; } = [];
    public List<BusinessEffect> BusinessEffects { get; set; } = [];
    public List<string> ValidationErrors { get; set; } = [];
    public List<string> Failures { get; set; } = [];
    public List<ExecutionEvent> Events { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public double ElapsedMilliseconds => ((CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalMilliseconds;
    public int LlmCalls { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public Kooshi.Benchmark.Shared.ExecutionMetrics Metrics => Kooshi.Benchmark.Shared.ExecutionMetricRules.Create(
        Architecture, ScenarioId, Id, Status, StartedAt, CompletedAt, Messages.Count(m => m.Role == "user"),
        LlmCalls, InputTokens, OutputTokens, ToolAttempts.Select(t => t.Outcome), BusinessEffects.Count,
        null, null, null, Events.Select(e => e.Type), Failures);

}

