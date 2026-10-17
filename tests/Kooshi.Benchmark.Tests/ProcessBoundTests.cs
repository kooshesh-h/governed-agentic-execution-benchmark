using Kooshi.Benchmark.ProcessBound.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kooshi.Benchmark.Tests;

public class ProcessBoundTests
{
    private static string Input => $"customerName: Ada Lovelace, email: ada@example.org, product: Analytics, preferredDate: {DateTime.UtcNow.AddDays(14):yyyy-MM-dd}, timeZone: UTC";
    private static (WorkflowService Service, ExecutionStore Store, SyntheticBookingToolExecutor Tool, ProcessRegistry Registry) Fixture(bool fail = false, string? path = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProcessBound:DatabasePath"] = path ?? Path.Combine(Path.GetTempPath(), $"process-{Guid.NewGuid()}.db"),
            ["ProcessBound:SimulateDownstreamFailure"] = fail.ToString(),
            ["ProcessBound:RegistryPath"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "processes.json"))
        }).Build();
        var registry = new ProcessRegistry(config);
        var store = new ExecutionStore(config);
        var tool = new SyntheticBookingToolExecutor(store, config);
        return (new WorkflowService(store, new SimulatedLlmClient(), tool, registry, NullLogger<WorkflowService>.Instance), store, tool, registry);
    }

    [Fact]
    public async Task ContractResolvesAndBookingCommitsOnceAfterConfirmation()
    {
        var (service, store, _, registry) = Fixture();
        Assert.NotNull(registry.Resolve("demo-booking"));
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        Assert.Equal("1.0", state.Contract?.ProcessVersion);
        Assert.Equal("ready", state.Status);
        Assert.Empty(state.BusinessEffects);
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Succeeded, state.Stage);
        Assert.Single(state.BusinessEffects);
        Assert.NotNull(await store.FindBookingAsync(state.Id, default));
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Single(state.BusinessEffects);
        Assert.Single(state.ToolAttempts, a => a.Name == "create_demo_booking");
        Assert.Contains(state.Events, e => e.Type == "confirmation");
    }

    [Fact]
    public async Task MissingInvalidAndCorrectedFieldsCannotExecuteEarly()
    {
        var (service, store, _, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", "I need a demo"), default);
        Assert.Equal(WorkflowStage.Collecting, state.Stage);
        Assert.Contains("email", state.MissingFields);
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Null(await store.FindBookingAsync(state.Id, default));
        state = (await service.MessageAsync(state.Id, new("email: invalid, preferredDate: 2000-01-01"), default))!;
        Assert.Contains("email", state.InvalidFields);
        state = (await service.MessageAsync(state.Id, new(Input), default))!;
        Assert.Equal(WorkflowStage.AwaitingConfirmation, state.Stage);
        Assert.Equal(3, state.InteractionTurns);
    }

    [Fact]
    public async Task UnknownIntentAndDisallowedToolAreRejected()
    {
        var (service, store, tool, _) = Fixture();
        var unknown = await service.CreateAsync(new("nonexistent", Input), default);
        Assert.Equal(WorkflowStage.Failed, unknown.Stage);
        Assert.Null(unknown.Contract);
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        var denied = await tool.ExecuteAsync(state, "delete_all_bookings", "{}", default);
        Assert.Equal("unauthorized", denied.Outcome);
        Assert.Equal(1, state.UnauthorizedToolExecutions);
        Assert.Null(await store.FindBookingAsync(state.Id, default));
        Assert.Contains(state.Events, e => e.Type == "policy_violation");
        var persisted = (await store.GetAsync(state.Id, default))!;
        Assert.Contains(persisted.ToolAttempts, a => a.Name == "delete_all_bookings" && a.Outcome == "unauthorized");
    }

    [Fact]
    public async Task ConfirmationIsBoundToArgumentsAndCancellationPreventsEffect()
    {
        var (service, store, tool, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        var denied = await tool.ExecuteAsync(state, "create_demo_booking", "{}", default);
        Assert.Equal("unauthorized", denied.Outcome);
        state = (await service.CancelAsync(state.Id, default))!;
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Cancelled, state.Stage);
        Assert.Null(await store.FindBookingAsync(state.Id, default));
        Assert.Throws<InvalidOperationException>(() => WorkflowTransitions.Move(state, WorkflowStage.Executing));
    }

    [Fact]
    public async Task FailureRetryAndRestartPersistAudit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"process-{Guid.NewGuid()}.db");
        var (service, _, _, _) = Fixture(true, path);
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Failed, state.Stage);
        Assert.Empty(state.BusinessEffects);
        var (restarted, store, _, _) = Fixture(true, path);
        state = (await restarted.GetAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Failed, state.Stage);
        state = (await restarted.RetryAsync(state.Id, default))!;
        Assert.Equal(2, state.ToolAttempts.Count(a => a.Name == "create_demo_booking"));
        Assert.Equal(1, state.RecoveryAttempts);
        Assert.Null(await store.FindBookingAsync(state.Id, default));
        Assert.Contains(state.Events, e => e.Type == "recovery_attempt");
    }

    [Fact]
    public async Task AlteredArgumentsInvalidateStoredConfirmation()
    {
        var (service, store, tool, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        state.Confirmed = true;
        state.ConfirmedArgumentsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(state.Booking))));
        WorkflowTransitions.Move(state, WorkflowStage.Executing);
        state.Booking.Product = "Changed product";
        var result = await tool.ExecuteAsync(state, "create_demo_booking", "{}", default);
        Assert.Equal("unauthorized", result.Outcome);
        Assert.Null(await store.FindBookingAsync(state.Id, default));
    }

    [Fact]
    public async Task TransientFailureCanRecoverWithoutDuplicateEffect()
    {
        var path = Path.Combine(Path.GetTempPath(), $"process-{Guid.NewGuid()}.db");
        var (failing, _, _, _) = Fixture(true, path);
        var state = await failing.CreateAsync(new("demo-booking", Input), default);
        state = (await failing.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Failed, state.Stage);
        var (recovered, store, _, _) = Fixture(false, path);
        state = (await recovered.RetryAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Succeeded, state.Stage);
        Assert.Single(state.BusinessEffects);
        var booking = await store.FindBookingAsync(state.Id, default);
        Assert.Equal(booking, state.BusinessEffects[0].BookingId);
        state = (await recovered.RetryAsync(state.Id, default))!;
        Assert.Single(state.BusinessEffects);
    }

    [Fact]
    public async Task InterruptedExecutionReconcilesPersistedCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"process-{Guid.NewGuid()}.db");
        var (service, store, _, _) = Fixture(path: path);
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        state.Confirmed = true;
        state.ConfirmedArgumentsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(state.Booking))));
        WorkflowTransitions.Move(state, WorkflowStage.Executing);
        await store.SaveAsync(state, default);
        var (bookingId, duplicate) = await store.CommitBookingAsync(state.Id, default);
        Assert.False(duplicate);
        var (restarted, _, _, _) = Fixture(path: path);
        state = (await restarted.GetAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Succeeded, state.Stage);
        Assert.Equal(bookingId, Assert.Single(state.BusinessEffects).BookingId);
        Assert.Contains(state.Events, e => e.Type == "recovery_succeeded");
    }

    [Fact]
    public async Task IdempotencyKeyReusesExecutionAndRejectsDifferentPayload()
    {
        var (service, store, _, _) = Fixture();
        var first = await service.CreateAsync(new("demo-booking", Input), default, "request-1");
        var repeat = await service.CreateAsync(new("demo-booking", Input), default, "request-1");
        Assert.Equal(first.Id, repeat.Id);
        Assert.Contains(repeat.Events, e => e.Type == "duplicate_request");
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => service.CreateAsync(new("demo-booking", "other input"), default, "request-1"));
        repeat = (await service.ConfirmAsync(first.Id, default))!;
        Assert.Single(repeat.BusinessEffects);
        Assert.NotNull(await store.FindBookingAsync(first.Id, default));
    }

    [Fact]
    public void RegistryCannotIntroduceAnUnimplementedTool()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "processes.json"));
        var path = Path.Combine(Path.GetTempPath(), $"registry-{Guid.NewGuid()}.json");
        File.WriteAllText(path, source.Replace("create_demo_booking", "unregistered_tool"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProcessBound:RegistryPath"] = path
            }).Build();
            Assert.Throws<InvalidOperationException>(() => new ProcessRegistry(configuration));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ProcessVersionMismatchBlocksBusinessTool()
    {
        var (service, store, tool, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        state.Contract = state.Contract! with { ProcessVersion = "unregistered-version" };
        state.Confirmed = true;
        state.ConfirmedArgumentsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(state.Booking))));
        WorkflowTransitions.Move(state, WorkflowStage.Executing);
        var result = await tool.ExecuteAsync(state, "create_demo_booking", "{}", default);
        Assert.Equal("unauthorized", result.Outcome);
        Assert.Null(await store.FindBookingAsync(state.Id, default));
    }
}
