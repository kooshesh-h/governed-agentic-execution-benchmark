using Kooshi.Benchmark.Shared;
using Kooshi.Benchmark.WorkflowAgent.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kooshi.Benchmark.Tests;

public class WorkflowTests
{
    private static (WorkflowService Service, ExecutionStore Store, IConfiguration Config) Fixture(bool fail = false, string? path = null, int retries = 3)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkflowAgent:DatabasePath"] = path ?? Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid()}.db"),
            ["WorkflowAgent:SimulateDownstreamFailure"] = fail.ToString(),
            ["WorkflowAgent:RetryLimit"] = retries.ToString()
        }).Build();
        var store = new ExecutionStore(config);
        return (new WorkflowService(store, new SimulatedLlmClient(), new SyntheticBookingToolExecutor(store, config), config, NullLogger<WorkflowService>.Instance), store, config);
    }

    private static string Input => $"customerName: Ada Lovelace, email: ada@example.org, product: Analytics, preferredDate: {DateTime.UtcNow.AddDays(14):yyyy-MM-dd}, timeZone: UTC";

    [Fact]
    public async Task BookingRequiresConfirmationAndCommitsOnce()
    {
        var (service, _, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        Assert.Equal(WorkflowStage.AwaitingConfirmation, state.Stage);
        Assert.Empty(state.BusinessEffects);
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Succeeded, state.Stage);
        Assert.Single(state.BusinessEffects);
        Assert.Equal(2, state.ToolAttempts.Count);
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Single(state.BusinessEffects);
        Assert.Equal(2, state.ToolAttempts.Count);
    }

    [Fact]
    public async Task MissingInvalidAndCorrectedFields()
    {
        var (service, _, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", "I need a demo"), default);
        Assert.Equal(WorkflowStage.Collecting, state.Stage);
        Assert.Contains(state.ValidationErrors, e => e.Contains("email"));
        state = (await service.MessageAsync(state.Id, new("email: bad, preferredDate: 2000-01-01"), default))!;
        Assert.Contains(state.ValidationErrors, e => e.Contains("email is invalid"));
        state = (await service.MessageAsync(state.Id, new(Input), default))!;
        Assert.Equal(WorkflowStage.AwaitingConfirmation, state.Stage);
        Assert.Equal(3, state.InteractionTurns);
    }

    [Fact]
    public async Task CancellationAndInvalidTransitionsPreventSideEffect()
    {
        var (service, _, _) = Fixture();
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        state = (await service.CancelAsync(state.Id, default))!;
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Cancelled, state.Stage);
        Assert.Empty(state.BusinessEffects);
        Assert.Throws<InvalidOperationException>(() => WorkflowTransitions.Move(state, WorkflowStage.Executing));
    }

    [Fact]
    public async Task FailureRetryLimitAndAuthorization()
    {
        var (service, store, config) = Fixture(fail: true, retries: 1);
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        var unauthorized = await new SyntheticBookingToolExecutor(store, config).ExecuteAsync(state, "create_demo_booking", "{}", default);
        Assert.Equal("unauthorized", unauthorized.Outcome);
        state = (await service.ConfirmAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Failed, state.Stage);
        Assert.Empty(state.BusinessEffects);
        state = (await service.RetryAsync(state.Id, default))!;
        Assert.Equal(2, state.ToolAttempts.Count(a => a.Name == "create_demo_booking"));
        state = (await service.RetryAsync(state.Id, default))!;
        Assert.Equal(2, state.ToolAttempts.Count(a => a.Name == "create_demo_booking"));
        Assert.Equal(1, state.RecoveryAttempts);
    }

    [Fact]
    public async Task RestartAndReconnectPreservesWorkflow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid()}.db");
        var (service, _, _) = Fixture(path: path);
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        var (reconnected, _, _) = Fixture(path: path);
        var restored = (await reconnected.GetAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.AwaitingConfirmation, restored.Stage);
        Assert.NotEmpty(restored.Events);
        restored = (await reconnected.ConfirmAsync(state.Id, default))!;
        Assert.Single(restored.BusinessEffects);
    }

    [Fact]
    public async Task InterruptedExecutionReconcilesCommittedBooking()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid()}.db");
        var (service, store, _) = Fixture(path: path);
        var state = await service.CreateAsync(new("demo-booking", Input), default);
        state.Confirmed = true;
        WorkflowTransitions.Move(state, WorkflowStage.Executing);
        await store.SaveAsync(state, default);
        var (bookingId, duplicate) = await store.CommitBookingAsync(state.Id, default);
        Assert.False(duplicate);
        var (reconnected, _, _) = Fixture(path: path);
        var restored = (await reconnected.GetAsync(state.Id, default))!;
        Assert.Equal(WorkflowStage.Succeeded, restored.Stage);
        Assert.Single(restored.BusinessEffects);
        Assert.Equal(bookingId, restored.BusinessEffects[0].BookingId);
        Assert.Equal(1, restored.RecoveryAttempts);
        Assert.Contains(restored.Events, e => e.Type == "recovery_succeeded");
    }
}
