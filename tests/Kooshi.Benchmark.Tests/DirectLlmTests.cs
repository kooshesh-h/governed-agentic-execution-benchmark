using Kooshi.Benchmark.DirectLlm.Api;
using Kooshi.Benchmark.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kooshi.Benchmark.Tests;

public class DirectLlmTests
{
    private static (ExecutionService Service, ExecutionStore Store) Fixture(bool fail = false, string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DirectLlm:DatabasePath"] = path,
            ["DirectLlm:SimulateDownstreamFailure"] = fail.ToString()
        }).Build();
        var store = new ExecutionStore(config);
        var service = new ExecutionService(store, new SimulatedLlmClient(), new SyntheticBookingToolExecutor(store, config), NullLogger<ExecutionService>.Instance);
        return (service, store);
    }

    private static string CompleteInput => $"customerName: Ada Lovelace, email: ada@example.org, product: Analytics, preferredDate: {DateTime.UtcNow.AddDays(14):yyyy-MM-dd}, timeZone: UTC";

    [Fact]
    public async Task SuccessfulBookingAndDuplicateConfirmation()
    {
        var (service, _) = Fixture();
        var created = await service.CreateAsync(new("demo-booking", CompleteInput), default);
        Assert.Equal("ready", created.Status);
        var booked = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Equal("succeeded", booked.Status);
        Assert.Single(booked.BusinessEffects);
        Assert.Equal(2, booked.ToolAttempts.Count);
        var duplicate = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Single(duplicate.BusinessEffects);
        Assert.Equal(2, duplicate.ToolAttempts.Count);
    }

    [Fact]
    public async Task MissingAndInvalidFieldsCanBeCorrected()
    {
        var (service, _) = Fixture();
        var created = await service.CreateAsync(new("demo-booking", "I want a demo"), default);
        Assert.Equal("collecting", created.Status);
        Assert.Contains(created.ValidationErrors, e => e.Contains("email"));
        var invalid = (await service.MessageAsync(created.Id, new("email: bad, preferredDate: 2000-01-01"), default))!;
        Assert.Contains(invalid.ValidationErrors, e => e.Contains("invalid"));
        var corrected = (await service.MessageAsync(created.Id, new(CompleteInput), default))!;
        Assert.Equal("ready", corrected.Status);
    }

    [Fact]
    public async Task CancellationPreventsBooking()
    {
        var (service, _) = Fixture();
        var created = await service.CreateAsync(new("demo-booking", CompleteInput), default);
        var cancelled = (await service.CancelAsync(created.Id, default))!;
        Assert.Equal("cancelled", cancelled.Status);
        var confirmed = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Empty(confirmed.BusinessEffects);
    }

    [Fact]
    public async Task DownstreamFailureAndRetryRemainObservable()
    {
        var (service, _) = Fixture(fail: true);
        var created = await service.CreateAsync(new("demo-booking", CompleteInput), default);
        var failed = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Equal("failed", failed.Status);
        Assert.Empty(failed.BusinessEffects);
        var retried = (await service.RetryAsync(created.Id, default))!;
        Assert.Equal("failed", retried.Status);
        Assert.Equal(3, retried.ToolAttempts.Count);
        Assert.Contains(retried.Events, e => e.Type == "recovery_attempt");
    }

    [Fact]
    public async Task UnauthorizedToolIsRejected()
    {
        var (_, store) = Fixture();
        var config = new ConfigurationBuilder().Build();
        var executor = new SyntheticBookingToolExecutor(store, config);
        var state = new ExecutionState();
        var result = await executor.ExecuteAsync(state, "delete_all_bookings", "{}", default);
        Assert.Equal("unauthorized", result.Outcome);
        Assert.Empty(state.BusinessEffects);
    }

    [Fact]
    public async Task StateSurvivesStoreReconstruction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var (service, _) = Fixture(path: path);
        var created = await service.CreateAsync(new("demo-booking", CompleteInput), default);
        var (reconnected, _) = Fixture(path: path);
        var recovered = (await reconnected.GetAsync(created.Id, default))!;
        Assert.Equal("ready", recovered.Status);
        Assert.Equal(created.Id, recovered.Id);
        Assert.NotEmpty(recovered.Messages);
        var booked = (await reconnected.ConfirmAsync(created.Id, default))!;
        Assert.Equal("succeeded", booked.Status);
    }
}
