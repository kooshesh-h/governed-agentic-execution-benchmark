using System.Text.Json;
using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.ProcessBound.Api;

public sealed class WorkflowService(ExecutionStore store, ILlmClient llm, IBusinessToolExecutor tool, ProcessRegistry registry, ILogger<WorkflowService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string Fingerprint(ExecutionState state) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.Booking))));
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

    public async Task<ExecutionState> CreateAsync(CreateExecutionRequest request, CancellationToken ct, string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return await CreateCoreAsync(request, ct);
        await Gate.WaitAsync(ct);
        try
        {
            var existing = await store.FindIdempotentExecutionAsync(idempotencyKey, ct);
            if (existing is not null)
            {
                var state = (await store.GetAsync(existing.Value, ct))!;
                if (state.ScenarioId != request.ScenarioId || state.Messages.FirstOrDefault(m => m.Role == "user")?.Content != request.Input)
                    throw new IdempotencyConflictException();
                state.Events.Add(new("duplicate_request", idempotencyKey, DateTimeOffset.UtcNow));
                await store.SaveAsync(state, ct);
                return state;
            }
            var created = await CreateCoreAsync(request, ct);
            var winner = await store.BindIdempotencyKeyAsync(idempotencyKey, created.Id, ct);
            return winner == created.Id ? created : (await store.GetAsync(winner, ct))!;
        }
        finally { Gate.Release(); }
    }

    private async Task<ExecutionState> CreateCoreAsync(CreateExecutionRequest request, CancellationToken ct)
    {
        var state = new ExecutionState { ScenarioId = request.ScenarioId };
        state.Messages.Add(new("user", request.Input, DateTimeOffset.UtcNow));
        var process = registry.Resolve(request.ScenarioId);
        if (process is null)
        {
            state.Stage = WorkflowStage.Failed;
            state.Response = "Unsupported process intent.";
            state.Failures.Add(state.Response);
            state.Events.Add(new("process_rejected", request.ScenarioId, DateTimeOffset.UtcNow));
            await store.SaveAsync(state, ct);
            return state;
        }
        state.Contract = ContractFactory.Create(process, state.Id);
        state.Fields = [.. process.Fields];
        state.Events.Add(new("contract_constructed", $"{process.Id}@{process.Version}", DateTimeOffset.UtcNow));
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
            state.ConfirmedArgumentsHash = null;
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
                Validate(state);
                if (state.ValidationErrors.Count > 0) { await store.SaveAsync(state, ct); return state; }
                state.Confirmed = true;
                state.ConfirmedArgumentsHash = Fingerprint(state);
                if (!Authorize(state)) { state.Confirmed = false; await store.SaveAsync(state, ct); return state; }
                state.Events.Add(new("confirmation", state.ConfirmedArgumentsHash, DateTimeOffset.UtcNow));
                WorkflowTransitions.Move(state, WorkflowStage.Executing);
                await store.SaveAsync(state, ct);
                await ExecuteAsync(state, ct);
            }
            else if (action == "retry" && state.Stage == WorkflowStage.Failed && state.Confirmed && state.RecoveryAttempts < (state.Contract?.RetryLimit ?? 0) && Authorize(state))
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
                state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, decision.ToolName, decision.Arguments ?? "{}", "unauthorized", "Model-requested business tool", DateTimeOffset.UtcNow));
                state.Events.Add(new("policy_violation", decision.ToolName, DateTimeOffset.UtcNow));
                state.Failures.Add("Unauthorized model tool request: " + decision.ToolName);
            }
            WorkflowTransitions.Move(state, WorkflowStage.Validating);
            Validate(state);
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
            if (!Authorize(state))
            {
                WorkflowTransitions.Move(state, WorkflowStage.Failed);
                state.Response = "Execution policy denied the booking tool.";
                return;
            }
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

    private static void Validate(ExecutionState state)
    {
        var json = JsonSerializer.SerializeToElement(state.Booking, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        state.Contract!.Arguments.Clear();
        state.MissingFields.Clear();
        state.InvalidFields.Clear();
        foreach (var field in state.Fields)
        {
            var value = json.TryGetProperty(field.Name, out var element) && element.ValueKind != JsonValueKind.Null ? element.ToString() : null;
            if (string.IsNullOrWhiteSpace(value)) { if (field.Required) state.MissingFields.Add(field.Name); }
            else state.Contract.Arguments[field.Name] = value;
        }
        state.ValidationErrors = BookingRules.Validate(state.Booking);
        state.InvalidFields.AddRange(state.ValidationErrors.Where(e => !e.Contains("is required")).Select(e => e.Split(' ')[0]).Distinct());
    }

    private bool Authorize(ExecutionState state)
    {
        var c = state.Contract;
        var reason = c is null ? "missing_contract" :
            c.ProcessId != state.ScenarioId ? "process_mismatch" :
            registry.Resolve(c.ProcessId)?.Version != c.ProcessVersion ? "unregistered_process_version" :
            c.Preconditions.Any(p => p != "valid-fields") ? "unsupported_precondition" :
            c.Authorization.Any(a => a != "registered-process") ? "unsupported_authorization_rule" :
            !c.AllowedTools.Contains("create_demo_booking") ? "tool_not_allowed" :
            state.ValidationErrors.Count != 0 ? "invalid_fields" :
            c.ConfirmationRequired && (!state.Confirmed || state.ConfirmedArgumentsHash != Fingerprint(state)) ? "confirmation_mismatch" :
            null;
        if (reason is null) { state.AuthorizationDecisions.Add("allowed"); return true; }
        state.AuthorizationDecisions.Add("denied:" + reason);
        state.Events.Add(new("policy_violation", reason, DateTimeOffset.UtcNow));
        state.UnauthorizedToolExecutions++;
        if (reason == "confirmation_mismatch") state.ConfirmationViolations++;
        return false;
    }
}

public sealed class IdempotencyConflictException : Exception { }
