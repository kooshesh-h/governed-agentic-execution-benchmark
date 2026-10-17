using Kooshi.Benchmark.ProcessBound.Api;
using Kooshi.Benchmark.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<ProcessRegistry>();
builder.Services.AddSingleton<ExecutionStore>();
builder.Services.AddSingleton<ISyntheticBookingService, SyntheticBookingService>();
builder.Services.AddSingleton<IBusinessToolExecutor, SyntheticBookingToolExecutor>();
builder.Services.AddHttpClient<OpenAiCompatibleLlmClient>(client => client.Timeout = TimeSpan.FromSeconds(Math.Max(1, builder.Configuration.GetValue("ProcessBound:TimeoutSeconds", 30))));
builder.Services.AddSingleton<SimulatedLlmClient>();
builder.Services.AddScoped<ILlmClient>(provider =>
    string.Equals(builder.Configuration["ProcessBound:Mode"], "real", StringComparison.OrdinalIgnoreCase)
        ? provider.GetRequiredService<OpenAiCompatibleLlmClient>()
        : provider.GetRequiredService<SimulatedLlmClient>());
builder.Services.AddScoped<WorkflowService>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", mode = builder.Configuration["ProcessBound:Mode"] ?? "simulated" }));
var executions = app.MapGroup("/api/executions");
executions.MapPost("/", async (CreateExecutionRequest request, HttpContext context, WorkflowService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.ScenarioId) || string.IsNullOrWhiteSpace(request.Input))
        return Results.BadRequest(new { error = "scenarioId and input must be nonempty" });
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (key.Length > 128) return Results.BadRequest(new { error = "Idempotency-Key is too long" });
    try { return Results.Created("/api/executions", await service.CreateAsync(request, ct, key)); }
    catch (IdempotencyConflictException) { return Results.Conflict(new { error = "Idempotency-Key was used for a different request" }); }
});
executions.MapGet("/{id:guid}", async (Guid id, WorkflowService service, CancellationToken ct) =>
    await service.GetAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/messages", async (Guid id, AddMessageRequest request, WorkflowService service, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(request.Message) ? Results.BadRequest(new { error = "message is required" }) :
    await service.MessageAsync(id, request, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/confirm", async (Guid id, WorkflowService service, CancellationToken ct) =>
    await service.ConfirmAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/cancel", async (Guid id, WorkflowService service, CancellationToken ct) =>
    await service.CancelAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
executions.MapPost("/{id:guid}/retry", async (Guid id, WorkflowService service, CancellationToken ct) =>
    await service.RetryAsync(id, ct) is { } state ? Results.Ok(state) : Results.NotFound());
app.Run();

public partial class Program { }


