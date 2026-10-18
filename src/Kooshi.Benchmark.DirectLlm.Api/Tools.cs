using Kooshi.Benchmark.Shared;
using System.Text.Json;

namespace Kooshi.Benchmark.DirectLlm.Api;

public sealed record ToolResult(bool Success, string Outcome, string? Error = null, string? BookingId = null, bool Committed = false);
public interface IBusinessToolExecutor
{
    Task<ToolResult> ExecuteAsync(ExecutionState state, string name, string arguments, CancellationToken ct);
}

public sealed class SyntheticBookingToolExecutor(ExecutionStore store, IConfiguration configuration, ISyntheticBookingService? bookingService = null) : IBusinessToolExecutor
{
    public async Task<ToolResult> ExecuteAsync(ExecutionState state, string name, string arguments, CancellationToken ct)
    {
        if (name == "update_booking")
        {
            try
            {
                using var document = JsonDocument.Parse(arguments);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return new(false, "invalid_arguments", "Expected a JSON object.");
                BookingRules.Merge(state.Booking, document.RootElement);
                return new(true, "updated");
            }
            catch (JsonException) { return new(false, "invalid_arguments", "Arguments are not valid JSON."); }
        }
        if (name != "create_demo_booking") return new(false, "unauthorized", "Tool is not permitted.");
        if (state.Status != "confirmed" && state.Status != "failed") return new(false, "unauthorized", "Booking requires explicit confirmation.");
        if (state.ProposedBookingArguments is null || arguments != state.ProposedBookingArguments)
            return new(false, "unauthorized", "Booking must match the model proposal confirmed by the user.");
        Booking proposed;
        try { proposed = JsonSerializer.Deserialize<Booking>(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException(); }
        catch (JsonException) { return new(false, "invalid_arguments", "Booking arguments are invalid JSON."); }
        if (BookingRules.Validate(proposed).Count > 0 || JsonSerializer.Serialize(proposed) != JsonSerializer.Serialize(state.Booking))
            return new(false, "invalid_arguments", "Booking arguments are invalid or changed.");
        var scenario = configuration["SyntheticBooking:Scenario"] ?? "success";
        if (configuration.GetValue("DirectLlm:SimulateDownstreamFailure", false)) scenario = "permanent";
        var result = await (bookingService ?? new SyntheticBookingService()).ExecuteAsync(new(state.Id, state.Booking, scenario, configuration.GetValue("SyntheticBooking:DelayMilliseconds", 100)), store, ct);
        return new(result.Success, result.Outcome, result.Error, result.BookingId, result.Committed);
    }
}



