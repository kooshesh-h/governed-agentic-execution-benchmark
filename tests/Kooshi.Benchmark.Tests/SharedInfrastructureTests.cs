using Kooshi.Benchmark.Shared;
using Kooshi.Benchmark.ProcessBound.Api;
using Microsoft.Extensions.Configuration;

namespace Kooshi.Benchmark.Tests;

public class SharedInfrastructureTests
{
    private static Booking ValidBooking() => new()
    {
        CustomerName = "Ada Lovelace", Email = "ada@example.org", Product = "Analytics",
        PreferredDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)), TimeZone = "UTC"
    };

    [Fact]
    public void ProcessRegistryHasTheCanonicalFields()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProcessBound:RegistryPath"] = Path.Combine(AppContext.BaseDirectory, "processes.json")
        }).Build();
        var fields = new ProcessRegistry(config).Resolve("demo-booking")!.Fields;
        Assert.Equal(BookingRules.Fields.Select(x => x.Name), fields.Select(x => x.Name));
        Assert.Equal(BookingRules.Fields.Select(x => x.Type), fields.Select(x => x.Type));
        Assert.Equal(BookingRules.Fields.Select(x => x.Required), fields.Select(x => x.Required));
        Assert.Equal(BookingRules.Fields.Select(x => x.Validation), fields.Select(x => x.Validation));
        Assert.Empty(BookingRules.Validate(ValidBooking()));
        Assert.Contains("email is invalid", BookingRules.Validate(ValidBooking().WithEmail("bad")));
    }

    [Theory]
    [InlineData("success", true, "committed", 1)]
    [InlineData("invalid", false, "invalid_arguments", 0)]
    [InlineData("permanent", false, "downstream_permanent_failure", 0)]
    [InlineData("timeout", false, "timeout", 0)]
    [InlineData("uncertain", false, "uncertain_outcome", 1)]
    public async Task FaultsSeparateAttemptsFromEffects(string scenario, bool success, string outcome, int effects)
    {
        var service = new SyntheticBookingService();
        var store = new InMemoryStore();
        var operation = new BookingOperation(Guid.NewGuid(), ValidBooking(), scenario);
        var first = await service.ExecuteAsync(operation, store, default);
        Assert.Equal(success, first.Success);
        Assert.Equal(outcome, first.Outcome);
        Assert.Equal(effects, store.EffectCount);
        if (scenario == "success")
        {
            var duplicate = await service.ExecuteAsync(operation, store, default);
            Assert.Equal("duplicate", duplicate.Outcome);
            Assert.False(duplicate.Committed);
            Assert.Equal(1, store.EffectCount);
        }
        if (scenario == "uncertain")
        {
            Assert.True(first.Committed);
            var reconciled = await service.ExecuteAsync(operation, store, default);
            Assert.Equal("duplicate", reconciled.Outcome);
            Assert.Equal(1, store.EffectCount);
        }
    }

    [Fact]
    public async Task TransientFailureSucceedsOnSecondAttempt()
    {
        var service = new SyntheticBookingService();
        var store = new InMemoryStore();
        var operation = new BookingOperation(Guid.NewGuid(), ValidBooking(), "transient");
        Assert.False((await service.ExecuteAsync(operation, store, default)).Success);
        Assert.Equal(0, store.EffectCount);
        Assert.True((await service.ExecuteAsync(operation, store, default)).Success);
        Assert.Equal(1, store.EffectCount);
    }

    private sealed class InMemoryStore : IBookingCommitStore
    {
        private readonly Dictionary<Guid, string> _bookings = [];
        public int EffectCount => _bookings.Count;
        public Task<(string BookingId, bool Duplicate)> CommitBookingAsync(Guid id, CancellationToken ct)
        {
            if (_bookings.TryGetValue(id, out var existing)) return Task.FromResult((existing, true));
            var bookingId = Guid.NewGuid().ToString();
            _bookings[id] = bookingId;
            return Task.FromResult((bookingId, false));
        }
    }
}

internal static class BookingTestExtensions
{
    public static Booking WithEmail(this Booking booking, string email) { booking.Email = email; return booking; }
}

