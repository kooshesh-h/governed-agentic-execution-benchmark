using Kooshi.Benchmark.Shared;
using System.Text.Json;

namespace Kooshi.Benchmark.ProcessBound.Api;

public sealed record ToolResult(bool Success, string Outcome, string? Error = null, string? BookingId = null, bool Committed = false);
public interface IBusinessToolExecutor
{
    Task<ToolResult> ExecuteAsync(ExecutionState state, string name, string arguments, CancellationToken ct);
}

public sealed class SyntheticBookingToolExecutor(ExecutionStore store, IConfiguration configuration, ISyntheticBookingService? bookingService = null, ToolRegistry? toolRegistry = null, ProcessRegistry? processRegistry = null) : IBusinessToolExecutor
{
    public async Task<ToolResult> ExecuteAsync(ExecutionState state, string name, string arguments, CancellationToken ct)
    {
        if (name == "update_booking")
        {
            if (state.Contract is null || state.Stage != WorkflowStage.Interpreting)
                return await DenyAsync(state, name, "Field updates require an active interpretation stage.", ct);
            try
            {
                using var document = JsonDocument.Parse(arguments);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return new(false, "invalid_arguments", "Expected a JSON object.");
                BookingRules.Merge(state.Booking, document.RootElement);
                return new(true, "updated");
            }
            catch (JsonException) { return new(false, "invalid_arguments", "Arguments are not valid JSON."); }
        }
        var definition = (toolRegistry ?? new ToolRegistry()).Resolve(name);
        var process = state.Contract is null ? null : (processRegistry ?? new ProcessRegistry(configuration, toolRegistry)).Resolve(state.Contract.ProcessId);
        if (definition is null || name != "create_demo_booking" || state.Contract is null || process is null ||
            process.Version != state.Contract.ProcessVersion || !process.AllowedTools.Contains(name) || !state.Contract.AllowedTools.Contains(name))
            return await DenyAsync(state, name, "Tool is not permitted for this process.", ct);
        if (state.Stage != definition.Stage || !state.Confirmed || state.ConfirmedArgumentsHash is null ||
            state.ConfirmedArgumentsHash != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state.Booking)))))
            return await DenyAsync(state, name, "Booking requires confirmation of current arguments.", ct);
        var scenario = configuration["SyntheticBooking:Scenario"] ?? "success";
        if (configuration.GetValue("ProcessBound:SimulateDownstreamFailure", false)) scenario = "permanent";
        var result = await (bookingService ?? new SyntheticBookingService()).ExecuteAsync(new(state.Id, state.Booking, scenario, configuration.GetValue("SyntheticBooking:DelayMilliseconds", 100)), store, ct);
        return new(result.Success, result.Outcome, result.Error, result.BookingId, result.Committed);
    }

    private async Task<ToolResult> DenyAsync(ExecutionState state, string name, string reason, CancellationToken ct)
    {
        state.UnauthorizedToolExecutions++;
        state.Events.Add(new("policy_violation", $"{name}: {reason}", DateTimeOffset.UtcNow));
        state.ToolAttempts.Add(new(state.ToolAttempts.Count + 1, name, "{}", "unauthorized", reason, DateTimeOffset.UtcNow));
        await store.SaveAsync(state, ct);
        return new(false, "unauthorized", reason);
    }
}






