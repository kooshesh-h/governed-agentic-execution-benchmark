using Kooshi.Benchmark.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Direct = Kooshi.Benchmark.DirectLlm.Api;
using Workflow = Kooshi.Benchmark.WorkflowAgent.Api;
using Process = Kooshi.Benchmark.ProcessBound.Api;

namespace Kooshi.Benchmark.Tests;

public class CrossArchitectureTests
{
    private static string Input => $"customerName: Ada Lovelace, email: ada@example.org, product: Analytics, preferredDate: {DateTime.UtcNow.AddDays(14):yyyy-MM-dd}, timeZone: UTC";

    [Theory]
    [InlineData("success", "succeeded", 1)]
    [InlineData("permanent", "failed", 0)]
    [InlineData("transient", "failed", 0)]
    [InlineData("uncertain", "failed", 1)]
    public async Task EquivalentDownstreamConditionsHaveConsistentEffects(string scenario, string firstStatus, int effects)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DirectLlm:DatabasePath"] = TempDb(),
            ["WorkflowAgent:DatabasePath"] = TempDb(),
            ["ProcessBound:DatabasePath"] = TempDb(),
            ["ProcessBound:RegistryPath"] = Path.Combine(AppContext.BaseDirectory, "processes.json"),
            ["SyntheticBooking:Scenario"] = scenario
        }).Build();
        var dStore = new Direct.ExecutionStore(configuration);
        var wStore = new Workflow.ExecutionStore(configuration);
        var pStore = new Process.ExecutionStore(configuration);
        var d = new Direct.ExecutionService(dStore, new Direct.SimulatedLlmClient(),
            new Direct.SyntheticBookingToolExecutor(dStore, configuration, new SyntheticBookingService()), NullLogger<Direct.ExecutionService>.Instance);
        var w = new Workflow.WorkflowService(wStore, new Workflow.SimulatedLlmClient(),
            new Workflow.SyntheticBookingToolExecutor(wStore, configuration, new SyntheticBookingService()), configuration, NullLogger<Workflow.WorkflowService>.Instance);
        var p = new Process.WorkflowService(pStore, new Process.SimulatedLlmClient(),
            new Process.SyntheticBookingToolExecutor(pStore, configuration, new SyntheticBookingService()),
            new Process.ProcessRegistry(configuration), NullLogger<Process.WorkflowService>.Instance);

        var createdD = await d.CreateAsync(new("demo-booking", Input), default);
        var createdW = await w.CreateAsync(new("demo-booking", Input), default);
        var createdP = await p.CreateAsync(new("demo-booking", Input), default);
        Assert.Equal("ready", createdD.Status);
        Assert.Equal("ready", createdW.Status);
        Assert.Equal("ready", createdP.Status);
        var states = new object[]
        {
            (await d.ConfirmAsync(createdD.Id, default))!,
            (await w.ConfirmAsync(createdW.Id, default))!,
            (await p.ConfirmAsync(createdP.Id, default))!
        };
        var metrics = states.Select(state => state switch
        {
            Direct.ExecutionState x => x.Metrics,
            Workflow.ExecutionState x => x.Metrics,
            Process.ExecutionState x => x.Metrics,
            _ => throw new InvalidOperationException()
        }).ToArray();
        Assert.All(metrics, m =>
        {
            Assert.Equal(firstStatus, m.FinalStatus);
            Assert.Equal(effects, m.CommittedBusinessEffects);
            Assert.Equal(m.ArchitectureId == "DirectLlm" ? 1 : 2, m.ToolInvocationAttempts);
            Assert.Equal(1, m.ConversationTurns);
            Assert.Equal(1, m.LlmRequestCount);
            Assert.Null(m.InputTokens);
        });
        Assert.Single(((Direct.ExecutionState)states[0]).ModelToolCalls);
        Assert.Equal("create_demo_booking", ((Direct.ExecutionState)states[0]).ModelToolCalls[0].Name);
        Assert.Equal("create_demo_booking", ((Direct.ExecutionState)states[0]).ToolAttempts[0].Name);
        if (scenario == "transient")
        {
            Assert.Equal("succeeded", (await d.RetryAsync(createdD.Id, default))!.Status);
            Assert.Equal("succeeded", (await w.RetryAsync(createdW.Id, default))!.Status);
            Assert.Equal("succeeded", (await p.RetryAsync(createdP.Id, default))!.Status);
        }
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"cross-{Guid.NewGuid()}.db");
}

