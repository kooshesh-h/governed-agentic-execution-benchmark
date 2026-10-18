using System.Text.Json;
using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.WorkflowAgent.Api;

public sealed class WorkflowService(ExecutionStore store, ILlmClient llm, IBusinessToolExecutor tool, IConfiguration configuration, ILogger<WorkflowService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private int RetryLimit => Math.Max(0, configuration.GetValue("WorkflowAgent:RetryLimit", 3));
    public async Task<ExecutionState?> GetAsync(Guid id, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var state = await store.GetAsync(id, ct);
            if (state?.Stage == WorkflowStage.Executing)
            {
                state.RecoveryAttempts++;
                var bookingId = await store.FindBookingAsync(id, ct);
                if (bookingId is not null)
                {
                    if (state.BusinessEffects.All(e => e.BookingId != bookingId)) state.BusinessEffects.Add(new(bookingId, DateTimeOffset.UtcNow));
                    WorkflowTransitions.Move(state, WorkflowStage.Succeeded);
                    state.CompletedAt = DateTimeOffset.UtcNow;
                    state.Response = $"Demo booking confirmed. Booking ID: {bookingId}";
                    state.Events.Add(new("recovery_succeeded", "Committed booking reconciled", DateTimeOffset.UtcNow));
                }
                else
                {
                    WorkflowTransitions.Move(state, WorkflowStage.Failed);
                    state.Response = "Execution was interrupted before a booking was committed. Retry to continue.";
                    state.Failures.Add(state.Response);
                    state.Events.Add(new("recovery_failed", "No committed booking found", DateTimeOffset.UtcNow));
                }
                await store.SaveAsync(state, ct);
            }
            return state;
        }
        finally { Gate.Release(); }
    }

    public async Task<ExecutionState> CreateAsync(CreateExecutionRequest request, CancellationToken ct)
    {
        var state = new ExecutionState { ScenarioId = request.ScenarioId };
        state.Messages.Add(new("user", request.Input, DateTimeOffset.UtcNow));
        await InterpretAsync(state, ct);
        await store.SaveAsync(state, ct);
        return state;
    }

    public async Task<ExecutionState?> MessageAsync(Guid id, AddMessageRequest request, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var state = await store.GetAsync(id, ct);
            if (state is null) return null;
            if (state.Stage is WorkflowStage.Succeeded or WorkflowStage.Cancelled or WorkflowStage.Executing) return state;
            if (state.Stage == WorkflowStage.Failed && state.Confirmed) return state;
            state.Confirmed = false;
            state.Messages.Add(new("user", request.Message, DateTimeOffset.UtcNow));
            await InterpretAsync(state, ct);
            await store.SaveAsync(state, ct);
            return state;
        }
        finally { Gate.Release(); }
    }

    public async Task<ExecutionState?> ConfirmAsync(Guid id, CancellationToken ct) => await ChangeAsync(id, "confirm", ct);
    public async Task<ExecutionState?> CancelAsync(Guid id, CancellationToken ct) => await ChangeAsync(id, "cancel", ct);
    public async Task<ExecutionState?> RetryAsync(Guid id, CancellationToken ct) => await ChangeAsync(id, "retry", ct);

    private async Task<ExecutionState?> ChangeAsync(Guid id, string action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var state = await store.GetAsync(id, ct);
            if (state is null) return null;
            if (action == "cancel" && state.Stage is WorkflowStage.Created or WorkflowStage.Collecting or WorkflowStage.AwaitingConfirmation or WorkflowStage.Failed && state.BusinessEffects.Count == 0)
            {
                WorkflowTransitions.Move(state, WorkflowStage.Cancelled);
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.Response = "Booking cancelled.";
            }
            else if (action == "confirm" && state.Stage == WorkflowStage.AwaitingConfirmation)
            {
                state.Confirmed = true;
                WorkflowTransitions.Move(state, WorkflowStage.Executing);
                await store.SaveAsync(state, ct);
                await ExecuteAsync(state, ct);
            }
            else if (action == "retry" && state.Stage == WorkflowStage.Failed && state.Confirmed && state.RecoveryAttempts < RetryLimit)
            {
                state.RecoveryAttempts++;
                state.Events.Add(new("recovery_attempt", "User requested retry", DateTimeOffset.UtcNow));
                WorkflowTransitions.Move(state, WorkflowStage.Executing);
                await store.SaveAsync(state, ct);
                await ExecuteAsync(state, ct);
            }
            await store.SaveAsync(state, ct);
            return state;
        }
        finally { Gate.Release(); }
    }

    private async Task InterpretAsync(ExecutionState state, CancellationToken ct)
    {
        WorkflowTransitions.Move(state, WorkflowStage.Interpreting);
        try
        {
            state.LlmCalls++;
            var llmStart = System.Diagnostics.Stopwatch.GetTimestamp();
            LlmDecision decision;
            try { decision = await llm.InterpretAsync(state, ct); }
            finally { state.LlmRequestMilliseconds = (state.LlmRequestMilliseconds ?? 0) + System.Diagnostics.Stopwatch.GetElapsedTime(llmStart).TotalMilliseconds; }
            if (decision.InputTokens is not null) state.InputTokens = (state.InputTokens ?? 0) + decision.InputTokens;
            if (decision.OutputTokens is not null) state.OutputTokens = (state.OutputTokens ?? 0) + decision.OutputTokens;
            if (decision.ToolName == "update_booking")
            {
                var result = await tool.ExecuteAsync(state, decision.ToolName, decision.Arguments ?? "{}", ct);
                state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, decision.ToolName, decision.Arguments ?? "{}", result.Outcome, result.Error, DateTimeOffset.UtcNow));
                if (result.Success) state.SuccessfulToolResponses++;
                else state.Failures.Add(result.Error ?? result.Outcome);
            }
            else if (decision.ToolName is not null)
            {
                state.UnauthorizedToolExecutions++;
                state.Failures.Add("Unauthorized model tool request: " + decision.ToolName);
            }
            WorkflowTransitions.Move(state, WorkflowStage.Validating);
            state.ValidationErrors = BookingRules.Validate(state.Booking);
            WorkflowTransitions.Move(state, state.ValidationErrors.Count == 0 ? WorkflowStage.AwaitingConfirmation : WorkflowStage.Collecting);
            state.Response = state.Stage == WorkflowStage.AwaitingConfirmation ? "Please review the booking details and confirm." : $"Please provide or correct: {string.Join("; ", state.ValidationErrors)}";
            if (!string.IsNullOrWhiteSpace(decision.Message)) state.Messages.Add(new("assistant", decision.Message, DateTimeOffset.UtcNow));
            state.Messages.Add(new("assistant", state.Response, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Interpretation failed for {ExecutionId}", state.Id);
            WorkflowTransitions.Move(state, WorkflowStage.Failed);
            state.Failures.Add("llm_interpretation_error: " + ex.Message);
            state.Response = "LLM interpretation failed. Send another message.";
        }
    }

    private async Task ExecuteAsync(ExecutionState state, CancellationToken ct)
    {
        var args = JsonSerializer.Serialize(state.Booking);
        try
        {
            var toolStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ToolResult result;
            try { result = await tool.ExecuteAsync(state, "create_demo_booking", args, ct); }
            finally { state.DownstreamOperationMilliseconds = (state.DownstreamOperationMilliseconds ?? 0) + System.Diagnostics.Stopwatch.GetElapsedTime(toolStart).TotalMilliseconds; }
            state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, "create_demo_booking", args, result.Outcome, result.Error, DateTimeOffset.UtcNow));
            if (result.Committed && result.BookingId is not null) state.BusinessEffects.Add(new(result.BookingId, DateTimeOffset.UtcNow));
            if (result.Success)
            {
                state.SuccessfulToolResponses++;

                WorkflowTransitions.Move(state, WorkflowStage.Succeeded);
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.Response = $"Demo booking confirmed. Booking ID: {result.BookingId}";
            }
            else
            {
                WorkflowTransitions.Move(state, WorkflowStage.Failed);
                state.Response = result.Error ?? "Booking failed.";
                state.Failures.Add(state.Response);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WorkflowTransitions.Move(state, WorkflowStage.Failed);
            state.Response = "Booking tool failed.";
            state.Failures.Add(ex.Message);
            logger.LogError(ex, "Booking failed for {ExecutionId}", state.Id);
        }
        state.Messages.Add(new("assistant", state.Response!, DateTimeOffset.UtcNow));
    }
}
