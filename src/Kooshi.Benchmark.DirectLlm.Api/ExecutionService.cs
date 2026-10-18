using Kooshi.Benchmark.Shared;
using System.Text.Json;

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
                state.ProposedBookingArguments = null;
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
            var decision = await DecideAsync(state, ct);
            if (decision.ToolName is not null)
            {
                var arguments = decision.Arguments ?? "{}";
                state.ModelToolCalls.Add(new(decision.ToolName, arguments, DateTimeOffset.UtcNow));
                state.ProposedBookingArguments = null;
                if (decision.ToolName == "create_demo_booking")
                {
                    try
                    {
                        using var document = JsonDocument.Parse(arguments);
                        if (document.RootElement.ValueKind != JsonValueKind.Object ||
                            document.RootElement.EnumerateObject().Any(p => p.Name.ToLowerInvariant() is not ("customername" or "email" or "product" or "preferreddate" or "timezone")))
                            throw new JsonException("Unexpected booking arguments.");
                        var proposed = new Booking();
                        BookingRules.Merge(proposed, document.RootElement);
                        if (BookingRules.Validate(proposed).Count > 0) throw new JsonException("Proposed booking is incomplete or invalid.");
                        state.Booking = proposed;
                        state.ProposedBookingArguments = JsonSerializer.Serialize(proposed);
                    }
                    catch (JsonException ex) { state.Failures.Add(ex.Message); }
                }
                else
                {
                    var result = await tools.ExecuteAsync(state, decision.ToolName, arguments, ct);
                    state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, decision.ToolName, arguments, result.Outcome, result.Error, DateTimeOffset.UtcNow));
                    if (!result.Success) state.Failures.Add(result.Error ?? result.Outcome);
                    if (decision.ToolName == "update_booking") RecordValidationFeedback(state, decision, arguments, result);
                    if (result.Success && decision.ToolName == "update_booking" && BookingRules.Validate(state.Booking).Count == 0)
                    {
                        // A complete update is still only data collection. Let the model decide
                        // whether to propose the business operation before confirmation is offered.
                        decision = await DecideAsync(state, ct);
                        if (decision.ToolName is not null)
                        {
                            arguments = decision.Arguments ?? "{}";
                            state.ModelToolCalls.Add(new(decision.ToolName, arguments, DateTimeOffset.UtcNow));
                            if (decision.ToolName == "create_demo_booking")
                            {
                                try
                                {
                                    using var proposal = JsonDocument.Parse(arguments);
                                    if (proposal.RootElement.ValueKind != JsonValueKind.Object ||
                                        proposal.RootElement.EnumerateObject().Any(p => p.Name.ToLowerInvariant() is not ("customername" or "email" or "product" or "preferreddate" or "timezone")))
                                        throw new JsonException("Unexpected booking arguments.");
                                    var proposed = new Booking();
                                    BookingRules.Merge(proposed, proposal.RootElement);
                                    if (BookingRules.Validate(proposed).Count > 0) throw new JsonException("Proposed booking is incomplete or invalid.");
                                    state.Booking = proposed;
                                    state.ProposedBookingArguments = JsonSerializer.Serialize(proposed);
                                }
                                catch (JsonException ex) { state.Failures.Add(ex.Message); }
                            }
                            else
                            {
                                var followUp = await tools.ExecuteAsync(state, decision.ToolName, arguments, ct);
                                state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, decision.ToolName, arguments, followUp.Outcome, followUp.Error, DateTimeOffset.UtcNow));
                                if (!followUp.Success) state.Failures.Add(followUp.Error ?? followUp.Outcome);
                                if (decision.ToolName == "update_booking") RecordValidationFeedback(state, decision, arguments, followUp);
                            }
                        }
                    }
                }
            }
            state.ValidationErrors = BookingRules.Validate(state.Booking);
            state.Status = state.ValidationErrors.Count == 0 && state.ProposedBookingArguments is not null ? "ready" : "collecting";
            state.Response = state.Status == "ready" ? "Please review the model-proposed booking details and confirm." :
                state.ValidationErrors.Count > 0 ? $"Please provide or correct: {string.Join("; ", state.ValidationErrors)}" : "Booking details are valid; awaiting a model proposal for review.";
            if (!string.IsNullOrWhiteSpace(decision.Message) && decision.ToolName != "update_booking")
                state.Messages.Add(new("assistant", decision.Message, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LLM interpretation failed for execution {ExecutionId}", state.Id);
            state.Status = "failed";
            state.Failures.Add("llm_interpretation_error: " + ex.Message);
            state.Response = "LLM interpretation failed. Retry or send another message.";
        }
    }

    private static void RecordValidationFeedback(ExecutionState state, LlmDecision decision, string arguments, ToolResult result)
    {
        var errors = BookingRules.Validate(state.Booking);
        var feedback = result.Success
            ? errors.Count == 0
                ? "Booking fields were updated successfully. All five required fields are present and validation succeeded. No booking has been proposed or executed. Decide the appropriate next action."
                : "Booking fields were updated. Validation failed: " + string.Join("; ", errors) + ". No booking has been proposed or executed. Decide the appropriate next action."
            : "Booking fields were not updated: " + (result.Error ?? result.Outcome) + ". Current booking validation: " + string.Join("; ", errors) + ". No booking has been proposed or executed. Decide the appropriate next action.";
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(decision.ToolCallId))
        {
            state.Messages.Add(new("assistant_tool_call", decision.Message, now, decision.ToolCallId, "update_booking", arguments));
            state.Messages.Add(new("tool", feedback, now, decision.ToolCallId));
        }
        else state.Messages.Add(new("application", feedback, now));
    }

    private async Task<LlmDecision> DecideAsync(ExecutionState state, CancellationToken ct)
    {
        state.LlmCalls++;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        LlmDecision decision;
        try { decision = await llm.InterpretAsync(state, ct); }
        catch (Kooshi.Benchmark.Shared.LlmResponseParseException ex)
        {
            if (ex.InputTokens is not null) state.InputTokens = (state.InputTokens ?? 0) + ex.InputTokens;
            if (ex.OutputTokens is not null) state.OutputTokens = (state.OutputTokens ?? 0) + ex.OutputTokens;
            throw;
        }
        finally { state.LlmRequestMilliseconds = (state.LlmRequestMilliseconds ?? 0) + System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
        if (decision.InputTokens is not null) state.InputTokens = (state.InputTokens ?? 0) + decision.InputTokens;
        if (decision.OutputTokens is not null) state.OutputTokens = (state.OutputTokens ?? 0) + decision.OutputTokens;
        return decision;
    }

    private async Task ExecuteBookingAsync(ExecutionState state, CancellationToken ct)
    {
        var args = state.ProposedBookingArguments;
        if (args is null)
        {
            state.Status = "failed";
            state.Failures.Add("No model-proposed booking operation exists.");
            return;
        }
        try
        {
            var toolStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ToolResult result;
            try { result = await tools.ExecuteAsync(state, "create_demo_booking", args, ct); }
            finally { state.DownstreamOperationMilliseconds = (state.DownstreamOperationMilliseconds ?? 0) + System.Diagnostics.Stopwatch.GetElapsedTime(toolStart).TotalMilliseconds; }
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
