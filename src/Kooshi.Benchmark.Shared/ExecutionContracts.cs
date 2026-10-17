namespace Kooshi.Benchmark.Shared;

public sealed record CreateExecutionRequest(string ScenarioId, string Input);
public sealed record AddMessageRequest(string Message);
public sealed record ExecutionResponse(Guid Id, string Status);

public interface IExecutionService
{
    Task<ExecutionResponse> CreateAsync(CreateExecutionRequest request, CancellationToken cancellationToken);
    Task<ExecutionResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ExecutionResponse?> AddMessageAsync(Guid id, AddMessageRequest request, CancellationToken cancellationToken);
    Task<ExecutionResponse?> ConfirmAsync(Guid id, CancellationToken cancellationToken);
    Task<ExecutionResponse?> CancelAsync(Guid id, CancellationToken cancellationToken);
    Task<ExecutionResponse?> RetryAsync(Guid id, CancellationToken cancellationToken);
}
