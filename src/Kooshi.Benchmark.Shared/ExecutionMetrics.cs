namespace Kooshi.Benchmark.Shared;

public sealed record ExecutionMetrics(
    string ArchitectureId, string ScenarioId, Guid ExecutionId, string FinalStatus,
    bool SuccessfulCompletion, double? DurationMilliseconds, int ConversationTurns,
    int? LlmRequestCount, int? InputTokens, int? OutputTokens,
    int ToolInvocationAttempts, int CommittedBusinessEffects, int? DuplicateCommittedEffects,
    int UnauthorizedToolAttempts, int? UnauthorizedCommittedExecutions,
    int? ConfirmationViolations, int RetryAttempts, int RecoveryAttempts,
    int RecoveryOutcomes, IReadOnlyList<string> ErrorCategories,
    double? LlmRequestMilliseconds = null, double? DownstreamOperationMilliseconds = null);

public static class ExecutionMetricRules
{
    public static ExecutionMetrics Create(string architecture, string scenario, Guid id, string status,
        DateTimeOffset started, DateTimeOffset? completed, int userTurns, int? llmRequests,
        int? inputTokens, int? outputTokens, IEnumerable<string> toolOutcomes,
        int committedEffects, int? duplicateEffects, int? unauthorizedCommitted,
        int? confirmationViolations, IEnumerable<string> eventTypes, IEnumerable<string> failures,
        double? llmRequestMilliseconds = null, double? downstreamOperationMilliseconds = null)
    {
        var outcomes = toolOutcomes.ToArray();
        var events = eventTypes.ToArray();
        var categories = outcomes.Where(x => x is not ("updated" or "committed" or "duplicate"))
            .Distinct(StringComparer.Ordinal).ToList();
        if (failures.Any() && categories.Count == 0) categories.Add("unclassified_failure");
        return new(architecture, scenario, id, status, status == "succeeded",
            completed is null ? null : (completed.Value - started).TotalMilliseconds,
            userTurns, llmRequests, inputTokens, outputTokens, outcomes.Length, committedEffects,
            duplicateEffects, outcomes.Count(x => x == "unauthorized"), unauthorizedCommitted,
            confirmationViolations, events.Count(x => x == "recovery_attempt"),
            events.Count(x => x == "recovery_attempt" || x == "recovery_succeeded" || x == "recovery_failed"),
            events.Count(x => x == "recovery_succeeded" || x == "recovery_failed"),
            categories, llmRequestMilliseconds, downstreamOperationMilliseconds);
    }
}
