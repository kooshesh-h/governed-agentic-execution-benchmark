using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Kooshi.Benchmark.Shared;
using Direct = Kooshi.Benchmark.DirectLlm.Api;
using Workflow = Kooshi.Benchmark.WorkflowAgent.Api;
using Process = Kooshi.Benchmark.ProcessBound.Api;

namespace Kooshi.Benchmark.Tests;

public class RealModeTransportTests
{
    private const string Response = """{"choices":[{"message":{"content":"Review this booking","tool_calls":[{"function":{"name":"create_demo_booking","arguments":"{\"customerName\":\"Ada Lovelace\",\"email\":\"ada@example.org\",\"product\":\"Analytics\",\"preferredDate\":\"2030-11-01\",\"timeZone\":\"UTC\"}"}}]}}],"usage":{"prompt_tokens":31,"completion_tokens":17}}""";

    [Fact]
    public void RealSettingsRequireExplicitModelAndCredential()
    {
        Assert.Throws<InvalidOperationException>(() => Kooshi.Benchmark.Shared.LlmSettings.Read(
            key => key.EndsWith(":ApiKey") ? "fixture-key" : null, "DirectLlm"));
    }

    [Fact]
    public async Task DirectRealClientSendsUserTextAndReceivesModelSelectedBusinessTool()
    {
        var handler = new RecordingHandler(Response);
        var client = new Direct.OpenAiCompatibleLlmClient(new HttpClient(handler), Config("DirectLlm"));
        var state = new Direct.ExecutionState();
        state.Messages.Add(new("user", "Please book an Analytics demo for Ada", DateTimeOffset.UtcNow));
        var decision = await client.InterpretAsync(state, default);
        Assert.Equal("create_demo_booking", decision.ToolName);
        Assert.Contains("Ada Lovelace", decision.Arguments);
        Assert.Equal(31, decision.InputTokens);
        Assert.Equal(17, decision.OutputTokens);
        Assert.Contains("Please book an Analytics demo", handler.RequestBody);
        Assert.Contains("create_demo_booking", handler.RequestBody);
        using (var request = JsonDocument.Parse(handler.RequestBody))
        {
            Assert.False(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
            Assert.Equal("auto", request.RootElement.GetProperty("tool_choice").GetString());
        }
        Assert.Single(state.NativeToolDiagnostics);
        Assert.Equal(new[] { "create_demo_booking" }, state.NativeToolDiagnostics[0].ToolNames);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
    }

    [Fact]
    public async Task WorkflowAndProcessRealClientsUseProviderGeneratedArguments()
    {
        const string extraction = """{"choices":[{"message":{"content":"Updated","tool_calls":[{"function":{"name":"update_booking","arguments":"{\"email\":\"ada@example.org\"}"}}]}}],"usage":{"prompt_tokens":12,"completion_tokens":5}}""";
        var workflowHandler = new RecordingHandler(extraction);
        var workflowState = new Workflow.ExecutionState();
        workflowState.Messages.Add(new("user", "Her email is ada@example.org", DateTimeOffset.UtcNow));
        var workflow = await new Workflow.OpenAiCompatibleLlmClient(new HttpClient(workflowHandler), Config("WorkflowAgent"))
            .InterpretAsync(workflowState, default);
        Assert.Equal("update_booking", workflow.ToolName);
        Assert.Contains("ada@example.org", workflow.Arguments);
        Assert.Contains("Her email is", workflowHandler.RequestBody);

        var processHandler = new RecordingHandler(extraction);
        var processState = new Process.ExecutionState();
        processState.Messages.Add(new("user", "Her email is ada@example.org", DateTimeOffset.UtcNow));
        var process = await new Process.OpenAiCompatibleLlmClient(new HttpClient(processHandler), Config("ProcessBound"))
            .InterpretAsync(processState, default);
        Assert.Equal("update_booking", process.ToolName);
        Assert.Contains("ada@example.org", process.Arguments);
        Assert.Contains("Her email is", processHandler.RequestBody);
    }

    [Fact]
    public async Task ProviderFailureDoesNotReturnSimulatedArguments()
    {
        var handler = new RecordingHandler("{}", HttpStatusCode.BadGateway);
        var state = new Direct.ExecutionState();
        state.Messages.Add(new("user", "email: ada@example.org", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new Direct.OpenAiCompatibleLlmClient(new HttpClient(handler), Config("DirectLlm")).InterpretAsync(state, default));
    }

    [Fact]
    public async Task MultipleToolCallsPreserveProviderUsageInParseFailure()
    {
        const string response = """{"choices":[{"message":{"tool_calls":[{"function":{"name":"update_booking","arguments":"{}"}},{"function":{"name":"create_demo_booking","arguments":"{}"}}]}}],"usage":{"prompt_tokens":123,"completion_tokens":45}}""";
        var state = new Direct.ExecutionState();
        state.Messages.Add(new("user", "Book a demo", DateTimeOffset.UtcNow));
        var ex = await Assert.ThrowsAsync<LlmResponseParseException>(() =>
            new Direct.OpenAiCompatibleLlmClient(new HttpClient(new RecordingHandler(response)), Config("DirectLlm"))
                .InterpretAsync(state, default));
        Assert.Equal(123, ex.InputTokens);
        Assert.Equal(45, ex.OutputTokens);
        Assert.Equal("multiple_tool_calls", ex.Diagnostic?.FailureClassification);
        Assert.Equal(new[] { "update_booking", "create_demo_booking" }, state.NativeToolDiagnostics[0].ToolNames);
    }

    [Fact]
    public async Task MultipleCallsFailExecutionWithoutBookingAndPreserveUsage()
    {
        const string response = """{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"function":{"name":"update_booking","arguments":"{\"email\":\"private@example.org\"}"}},{"function":{"name":"create_demo_booking","arguments":"not-json"}}]}}],"usage":{"prompt_tokens":123,"completion_tokens":45}}""";
        var config = ServiceConfig("DirectLlm");
        var store = new Direct.ExecutionStore(config);
        var service = new Direct.ExecutionService(store,
            new Direct.OpenAiCompatibleLlmClient(new HttpClient(new RecordingHandler(response)), config),
            new Direct.SyntheticBookingToolExecutor(store, config), NullLogger<Direct.ExecutionService>.Instance);
        var state = await service.CreateAsync(new("demo-booking", "Book a demo"), default);
        Assert.Equal("failed", state.Status);
        Assert.Equal(123, state.InputTokens);
        Assert.Equal(45, state.OutputTokens);
        Assert.Empty(state.BusinessEffects);
        Assert.Empty(state.ModelToolCalls);
        var diagnostic = Assert.Single(state.NativeToolDiagnostics);
        Assert.Equal("multiple_tool_calls", diagnostic.FailureClassification);
        Assert.False(diagnostic.ArgumentsValidJson);
        Assert.Equal(new[] { "email" }, diagnostic.ArgumentFieldNames[0]);
        Assert.DoesNotContain("private@example.org", JsonSerializer.Serialize(diagnostic));
    }

    [Fact]
    public async Task MockedRealProviderDrivesAllThreeServicesWithoutCommittingBeforeConfirmation()
    {
        var fields = JsonSerializer.Serialize(new { customerName = "Ada Lovelace", email = "ada@example.org",
            product = "Analytics", preferredDate = "2030-11-01", timeZone = "UTC" });
        static string Fixture(string tool, string args) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "Review the details", tool_calls = new[] { new { function = new { name = tool, arguments = args } } } } } },
            usage = new { prompt_tokens = 19, completion_tokens = 9 }
        });
        var input = new CreateExecutionRequest("demo-booking", "Please arrange Ada's Analytics demo for November 1, 2030 in UTC; email ada@example.org.");

        var directConfig = ServiceConfig("DirectLlm");
        var directStore = new Direct.ExecutionStore(directConfig);
        var direct = new Direct.ExecutionService(directStore,
            new Direct.OpenAiCompatibleLlmClient(new HttpClient(new RecordingHandler(Fixture("create_demo_booking", fields))), directConfig),
            new Direct.SyntheticBookingToolExecutor(directStore, directConfig), NullLogger<Direct.ExecutionService>.Instance);
        var directCreated = await direct.CreateAsync(input, default);
        Assert.Equal("ready", directCreated.Status);
        Assert.Single(directCreated.ModelToolCalls);
        Assert.Empty(directCreated.BusinessEffects);
        Assert.Equal(19, directCreated.InputTokens);
        Assert.Single((await direct.ConfirmAsync(directCreated.Id, default))!.BusinessEffects);

        var workflowConfig = ServiceConfig("WorkflowAgent");
        var workflowStore = new Workflow.ExecutionStore(workflowConfig);
        var workflow = new Workflow.WorkflowService(workflowStore,
            new Workflow.OpenAiCompatibleLlmClient(new HttpClient(new RecordingHandler(Fixture("update_booking", fields))), workflowConfig),
            new Workflow.SyntheticBookingToolExecutor(workflowStore, workflowConfig), workflowConfig, NullLogger<Workflow.WorkflowService>.Instance);
        var workflowCreated = await workflow.CreateAsync(input, default);
        Assert.Equal("ready", workflowCreated.Status);
        Assert.Empty(workflowCreated.BusinessEffects);
        Assert.Equal(19, workflowCreated.InputTokens);
        Assert.Single((await workflow.ConfirmAsync(workflowCreated.Id, default))!.BusinessEffects);

        var processConfig = ServiceConfig("ProcessBound");
        var processStore = new Process.ExecutionStore(processConfig);
        var process = new Process.WorkflowService(processStore,
            new Process.OpenAiCompatibleLlmClient(new HttpClient(new RecordingHandler(Fixture("update_booking", fields))), processConfig),
            new Process.SyntheticBookingToolExecutor(processStore, processConfig),
            new Process.ProcessRegistry(processConfig), NullLogger<Process.WorkflowService>.Instance);
        var processCreated = await process.CreateAsync(input, default);
        Assert.Equal("ready", processCreated.Status);
        Assert.Empty(processCreated.BusinessEffects);
        Assert.Equal(19, processCreated.InputTokens);
        Assert.Single((await process.ConfirmAsync(processCreated.Id, default))!.BusinessEffects);
    }

    [Fact]
    public async Task MockedRealProviderCanUpdateThenProposeBooking()
    {
        var fields = JsonSerializer.Serialize(new { customerName = "Ada Lovelace", email = "ada@example.org",
            product = "Analytics", preferredDate = "2030-11-01", timeZone = "UTC" });
        static string Fixture(string tool, string args) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "Review the details", tool_calls = new[] { new { function = new { name = tool, arguments = args } } } } } },
            usage = new { prompt_tokens = 19, completion_tokens = 9 }
        });
        var config = ServiceConfig("DirectLlm");
        var store = new Direct.ExecutionStore(config);
        var handler = new SequenceHandler(Fixture("update_booking", fields), Fixture("create_demo_booking", fields));
        var service = new Direct.ExecutionService(store,
            new Direct.OpenAiCompatibleLlmClient(new HttpClient(handler), config),
            new Direct.SyntheticBookingToolExecutor(store, config), NullLogger<Direct.ExecutionService>.Instance);
        var created = await service.CreateAsync(new("demo-booking", "Arrange Ada's demo"), default);
        Assert.Equal("ready", created.Status);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(new[] { "update_booking", "create_demo_booking" }, created.ModelToolCalls.Select(x => x.Name));
        Assert.Empty(created.BusinessEffects);
        var confirmed = (await service.ConfirmAsync(created.Id, default))!;
        Assert.Equal("succeeded", confirmed.Status);
        Assert.Single(confirmed.BusinessEffects);
        Assert.Equal(2, confirmed.NativeToolDiagnostics.Count);
    }

    private static IConfiguration Config(string prefix) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"{prefix}:ApiKey"] = "fixture-key", [$"{prefix}:Endpoint"] = "https://fixture.invalid/chat/completions",
        [$"{prefix}:Model"] = "fixture-model", [$"{prefix}:Temperature"] = "0", [$"{prefix}:MaxOutputTokens"] = "100"
    }).Build();

    private static IConfiguration ServiceConfig(string prefix) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"{prefix}:ApiKey"] = "fixture-key", [$"{prefix}:Endpoint"] = "https://fixture.invalid/chat/completions",
        [$"{prefix}:Model"] = "fixture-model", [$"{prefix}:DatabasePath"] = Path.Combine(Path.GetTempPath(), $"kooshi-real-fixture-{Guid.NewGuid()}.db"),
        ["ProcessBound:RegistryPath"] = Path.Combine(AppContext.BaseDirectory, "processes.json")
    }).Build();

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization!.ToString();
            return new HttpResponseMessage(status) { Content = new StringContent(response) };
        }
    }

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[RequestCount++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        }
    }
}
