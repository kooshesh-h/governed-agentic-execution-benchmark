using Kooshi.Benchmark.DirectLlm.Api;
using Kooshi.Benchmark.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ExecutionStore>();
builder.Services.AddSingleton<ISyntheticBookingService, SyntheticBookingService>();
builder.Services.AddSingleton<IBusinessToolExecutor, SyntheticBookingToolExecutor>();
builder.Services.AddHttpClient<OpenAiCompatibleLlmClient>(client => client.Timeout = TimeSpan.FromSeconds(Math.Max(1, builder.Configuration.GetValue("DirectLlm:TimeoutSeconds", 30))));
builder.Services.AddSingleton<SimulatedLlmClient>();
builder.Services.AddScoped<ILlmClient>(provider =>
    string.Equals(builder.Configuration["DirectLlm:Mode"], "real", StringComparison.OrdinalIgnoreCase)
        ? provider.GetRequiredService<OpenAiCompatibleLlmClient>()
        : provider.GetRequiredService<SimulatedLlmClient>());
builder.Services.AddScoped<ExecutionService>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", mode = builder.Configuration["DirectLlm:Mode"] ?? "simulated" }));

var executions = app.MapGroup("/api/executions");
executions.MapPost("/", async (CreateExecutionRequest request, ExecutionService service, CancellationToken ct) =>
    request.ScenarioId != "demo-booking" || string.IsNullOrWhiteSpace(request.Input)
        ? Results.BadRequest(new { error = "scenarioId must be demo-booking and input must be nonempty" })
        : Results.Created("/api/executions", await service.CreateAsync(request, ct)));
executions.MapGet("/{id:guid}", async (Guid id, ExecutionService service, CancellationToken ct) =>
    await service.GetAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/messages", async (Guid id, AddMessageRequest request, ExecutionService service, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(request.Message) ? Results.BadRequest(new { error = "message is required" }) :
    await service.MessageAsync(id, request, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/confirm", async (Guid id, ExecutionService service, CancellationToken ct) =>
    await service.ConfirmAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/cancel", async (Guid id, ExecutionService service, CancellationToken ct) =>
    await service.CancelAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/retry", async (Guid id, ExecutionService service, CancellationToken ct) =>
    await service.RetryAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());

app.Run();

public partial class Program { }


