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
        Assert.Contains(created.ModelToolCalls, x => x.Name == "create_demo_booking");
        Assert.Empty(created.ToolAttempts);
        Assert.Empty(created.BusinessEffects);
        var booked = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Equal("succeeded", booked.Status);
        Assert.Single(booked.BusinessEffects);
        Assert.Single(booked.ToolAttempts);
        var duplicate = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Single(duplicate.BusinessEffects);
        Assert.Single(duplicate.ToolAttempts);
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
        Assert.Equal(2, retried.ToolAttempts.Count);
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
    public async Task ModelSelectedBookingIsValidatedAndHeldForConfirmation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DirectLlm:DatabasePath"] = path }).Build();
        var store = new ExecutionStore(config);
        var arguments = System.Text.Json.JsonSerializer.Serialize(new Booking
        {
            CustomerName = "Ada Lovelace", Email = "ada@example.org", Product = "Analytics",
            PreferredDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)), TimeZone = "UTC"
        });
        var llm = new FixedLlm(new("Proposed", "create_demo_booking", arguments, 11, 7));
        var service = new ExecutionService(store, llm, new SyntheticBookingToolExecutor(store, config), NullLogger<ExecutionService>.Instance);
        var created = await service.CreateAsync(new("demo-booking", "Please book a demo"), default);
        Assert.Equal("ready", created.Status);
        Assert.Single(created.ModelToolCalls);
        Assert.Empty(created.BusinessEffects);
        Assert.Empty(created.ToolAttempts);
        Assert.Equal(11, created.InputTokens);
        Assert.Equal(7, created.OutputTokens);
        var confirmed = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Equal("succeeded", confirmed.Status);
        Assert.Single(confirmed.BusinessEffects);
        Assert.Single(confirmed.ToolAttempts);
    }

    [Fact]
    public async Task ModelCannotCommitUnapprovedOrInvalidOperation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DirectLlm:DatabasePath"] = path }).Build();
        var store = new ExecutionStore(config);
        var service = new ExecutionService(store, new FixedLlm(new("Bad proposal", "delete_all_bookings", "{}")),
            new SyntheticBookingToolExecutor(store, config), NullLogger<ExecutionService>.Instance);
        var created = await service.CreateAsync(new("demo-booking", CompleteInput), default);
        Assert.NotEqual("ready", created.Status);
        Assert.Empty(created.BusinessEffects);
        var afterConfirm = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Empty(afterConfirm.BusinessEffects);
    }

    private sealed class FixedLlm(LlmDecision decision) : ILlmClient
    {
        public Task<LlmDecision> InterpretAsync(ExecutionState state, CancellationToken ct) => Task.FromResult(decision);
    }

    [Fact]
    public async Task CompleteModelUpdateRequiresSeparateModelProposalBeforeConfirmation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DirectLlm:DatabasePath"] = path }).Build();
        var store = new ExecutionStore(config);
        var booking = new Booking { CustomerName = "Ada Lovelace", Email = "ada@example.org", Product = "Analytics",
            PreferredDate = new DateOnly(2030, 11, 1), TimeZone = "UTC" };
        var args = System.Text.Json.JsonSerializer.Serialize(booking);
        var llm = new SequenceLlm(new("Updated", "update_booking", args, 271, 43),
            new("Proposed", "create_demo_booking", args, 100, 20));
        var service = new ExecutionService(store, llm, new SyntheticBookingToolExecutor(store, config), NullLogger<ExecutionService>.Instance);
        var created = await service.CreateAsync(new("demo-booking", "Arrange Ada's demo"), default);
        Assert.Equal("ready", created.Status);
        Assert.Equal(2, created.LlmCalls);
        Assert.Equal(371, created.InputTokens);
        Assert.Equal(63, created.OutputTokens);
        Assert.Equal(new[] { "update_booking", "create_demo_booking" }, created.ModelToolCalls.Select(x => x.Name));
        Assert.Single(created.ToolAttempts);
        Assert.Empty(created.BusinessEffects);
        var confirmed = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Equal("succeeded", confirmed.Status);
        Assert.Single(confirmed.BusinessEffects);
        Assert.Single(confirmed.ToolAttempts, x => x.Name == "create_demo_booking");
        Assert.Single((await service.ConfirmAsync(created.Id, default))!.BusinessEffects);
    }

    [Theory]
    [InlineData("create_demo_booking", "{}")]
    [InlineData("update_booking", null)]
    [InlineData("delete_all_bookings", "{}")]
    public async Task CompleteUpdateDoesNotConfirmWithoutValidModelProposal(string secondTool, string? secondArgs)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DirectLlm:DatabasePath"] = path }).Build();
        var store = new ExecutionStore(config);
        var booking = new Booking { CustomerName = "Ada Lovelace", Email = "ada@example.org", Product = "Analytics",
            PreferredDate = new DateOnly(2030, 11, 1), TimeZone = "UTC" };
        var args = System.Text.Json.JsonSerializer.Serialize(booking);
        var llm = new SequenceLlm(new("Updated", "update_booking", args),
            new("Follow-up", secondTool, secondArgs ?? args));
        var service = new ExecutionService(store, llm, new SyntheticBookingToolExecutor(store, config), NullLogger<ExecutionService>.Instance);
        var created = await service.CreateAsync(new("demo-booking", "Arrange Ada's demo"), default);
        Assert.Equal(2, created.LlmCalls);
        Assert.Equal("collecting", created.Status);
        Assert.Empty(created.BusinessEffects);
        Assert.Empty((await service.ConfirmAsync(created.Id, default))!.BusinessEffects);
    }

    [Fact]
    public async Task FailedFollowUpDecisionCannotCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kooshi-test-{Guid.NewGuid()}.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DirectLlm:DatabasePath"] = path }).Build();
        var store = new ExecutionStore(config);
        var booking = new Booking { CustomerName = "Ada Lovelace", Email = "ada@example.org", Product = "Analytics",
            PreferredDate = new DateOnly(2030, 11, 1), TimeZone = "UTC" };
        var llm = new SequenceLlm(new LlmDecision("Updated", "update_booking", System.Text.Json.JsonSerializer.Serialize(booking)));
        var service = new ExecutionService(store, llm, new SyntheticBookingToolExecutor(store, config), NullLogger<ExecutionService>.Instance);
        var created = await service.CreateAsync(new("demo-booking", "Arrange Ada's demo"), default);
        Assert.Equal(2, created.LlmCalls);
        Assert.Equal("failed", created.Status);
        Assert.Empty(created.BusinessEffects);
        Assert.Empty((await service.ConfirmAsync(created.Id, default))!.BusinessEffects);
    }

    private sealed class SequenceLlm(params LlmDecision[] decisions) : ILlmClient
    {
        private int index;
        public Task<LlmDecision> InterpretAsync(ExecutionState state, CancellationToken ct) => Task.FromResult(decisions[index++]);
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
