using System.Collections.Concurrent;

namespace Kooshi.Benchmark.Shared;

public interface IBookingCommitStore
{
    Task<(string BookingId, bool Duplicate)> CommitBookingAsync(Guid executionId, CancellationToken ct);
}

public sealed record BookingOperation(Guid ExecutionId, Booking Booking, string Scenario = "success", int DelayMilliseconds = 100);
public sealed record BookingOperationResult(bool Success, string Outcome, string? Error = null,
    string? BookingId = null, bool Committed = false);

public interface ISyntheticBookingService
{
    Task<BookingOperationResult> ExecuteAsync(BookingOperation operation, IBookingCommitStore store, CancellationToken ct);
}

// The configured scenario is selected per operation. Attempt counts belong to this service instance,
// so a fresh instance gives an isolated, repeatable fault sequence.
public sealed class SyntheticBookingService : ISyntheticBookingService
{
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public async Task<BookingOperationResult> ExecuteAsync(BookingOperation operation, IBookingCommitStore store, CancellationToken ct)
    {
        var errors = BookingRules.Validate(operation.Booking);
        if (errors.Count > 0) return new(false, "invalid_arguments", string.Join("; ", errors));
        var attempt = _attempts.AddOrUpdate(operation.ExecutionId, 1, (_, current) => current + 1);
        var scenario = operation.Scenario.ToLowerInvariant();
        if (scenario == "invalid") return new(false, "invalid_arguments", "Synthetic invalid request.");
        if (scenario == "permanent") return new(false, "downstream_permanent_failure", "Synthetic permanent downstream failure.");
        if (scenario == "transient" && attempt == 1) return new(false, "downstream_transient_failure", "Synthetic transient downstream failure.");
        if (scenario == "timeout") return new(false, "timeout", "Synthetic downstream timeout; effect status unknown.");
        if (scenario is "delayed" or "delayed-response")
            await Task.Delay(Math.Clamp(operation.DelayMilliseconds, 0, 60_000), ct);
        var (bookingId, duplicate) = await store.CommitBookingAsync(operation.ExecutionId, ct);
        if (scenario == "uncertain" && attempt == 1)
            return new(false, "uncertain_outcome", "Downstream committed but response was lost.", bookingId, !duplicate);
        return new(true, duplicate ? "duplicate" : "committed", BookingId: bookingId, Committed: !duplicate);
    }
}
