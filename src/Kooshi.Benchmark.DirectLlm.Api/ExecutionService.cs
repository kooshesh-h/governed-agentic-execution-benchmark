using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.DirectLlm.Api;

public sealed class ExecutionService(ExecutionStore store, ILlmClient llm, IBusinessToolExecutor tools, ILogger<ExecutionService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public Task<ExecutionState?> GetAsync(Guid id, CancellationToken ct) => store.GetAsync(id, ct);

    public async Task<ExecutionState> CreateAsync(CreateExecutionRequest request, CancellationToken ct)
    {
        var state = new ExecutionState { ScenarioId = request.ScenarioId };
        state.Messages.Add(new("user", request.Input, DateTimeOffset.UtcNow));
        state.Events.Add(new("created", request.ScenarioId, DateTimeOffset.UtcNow));
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
            if (state.Status is "succeeded" or "cancelled") return state;
            state.Messages.Add(new("user", request.Message, DateTimeOffset.UtcNow));
            await InterpretAsync(state, ct);
            await store.SaveAsync(state, ct);
            return state;
        }
        finally { Gate.Release(); }
    }

    public Task<ExecutionState?> ConfirmAsync(Guid id, CancellationToken ct) => ChangeAsync(id, "confirm", ct);
    public Task<ExecutionState?> CancelAsync(Guid id, CancellationToken ct) => ChangeAsync(id, "cancel", ct);
    public Task<ExecutionState?> RetryAsync(Guid id, CancellationToken ct) => ChangeAsync(id, "retry", ct);

    private async Task<ExecutionState?> ChangeAsync(Guid id, string action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var state = await store.GetAsync(id, ct);
            if (state is null) return null;
            var now = DateTimeOffset.UtcNow;
            if (action == "cancel" && state.Status is not ("succeeded" or "cancelled"))
            {
                state.Status = "cancelled";
                state.CompletedAt = now;
                state.Response = "Booking cancelled.";
                state.Events.Add(new("cancelled", "User cancelled", now));
            }
            else if (action == "confirm" && state.Status == "ready")
            {
                state.Status = "confirmed";
                state.Events.Add(new("confirmed", "User confirmed", now));
                await ExecuteBookingAsync(state, ct);
            }
            else if (action == "retry" && state.Status == "failed" && state.Events.Any(e => e.Type == "confirmed"))
            {
                state.Events.Add(new("recovery_attempt", "User requested retry", now));
                await ExecuteBookingAsync(state, ct);
            }
            await store.SaveAsync(state, ct);
            return state;
        }
        finally { Gate.Release(); }
    }

    private async Task InterpretAsync(ExecutionState state, CancellationToken ct)
    {
        try
        {
            state.LlmCalls++;
            var decision = await llm.InterpretAsync(state, ct);
            if (decision.InputTokens is not null) state.InputTokens = (state.InputTokens ?? 0) + decision.InputTokens;
            if (decision.OutputTokens is not null) state.OutputTokens = (state.OutputTokens ?? 0) + decision.OutputTokens;
            if (decision.ToolName is not null)
            {
                var result = await tools.ExecuteAsync(state, decision.ToolName, decision.Arguments ?? "{}", ct);
                state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, decision.ToolName, decision.Arguments ?? "{}", result.Outcome, result.Error, DateTimeOffset.UtcNow));
                if (!result.Success) state.Failures.Add(result.Error ?? result.Outcome);
            }
            state.ValidationErrors = BookingRules.Validate(state.Booking);
            state.Status = state.ValidationErrors.Count == 0 ? "ready" : "collecting";
            state.Response = state.Status == "ready" ? "Please review the booking details and confirm." : $"Please provide or correct: {string.Join("; ", state.ValidationErrors)}";
            if (!string.IsNullOrWhiteSpace(decision.Message)) state.Messages.Add(new("assistant", decision.Message, DateTimeOffset.UtcNow));
            state.Messages.Add(new("assistant", state.Response, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LLM interpretation failed for execution {ExecutionId}", state.Id);
            state.Status = "failed";
            state.Failures.Add(ex.Message);
            state.Response = "LLM interpretation failed. Retry or send another message.";
        }
    }

    private async Task ExecuteBookingAsync(ExecutionState state, CancellationToken ct)
    {
        var args = System.Text.Json.JsonSerializer.Serialize(state.Booking);
        try
        {
            var result = await tools.ExecuteAsync(state, "create_demo_booking", args, ct);
            state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, "create_demo_booking", args, result.Outcome, result.Error, DateTimeOffset.UtcNow));
            if (result.Committed && result.BookingId is not null) state.BusinessEffects.Add(new(result.BookingId, DateTimeOffset.UtcNow));
            if (result.Success)
            {

                state.Status = "succeeded";
                state.Response = $"Demo booking confirmed. Booking ID: {result.BookingId}";
                state.CompletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                state.Status = "failed";
                state.Response = result.Error ?? "Booking failed.";
                state.Failures.Add(state.Response);
                logger.LogWarning("Booking tool failed for {ExecutionId}: {Outcome}", state.Id, result.Outcome);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = "failed";
            state.Response = "Booking tool failed.";
            state.Failures.Add(ex.Message);
            logger.LogError(ex, "Booking tool exception for {ExecutionId}", state.Id);
        }
        state.Messages.Add(new("assistant", state.Response!, DateTimeOffset.UtcNow));
    }
}



