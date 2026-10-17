namespace Kooshi.Benchmark.ProcessBound.Api;

public sealed record ToolDefinition(string Name, WorkflowStage Stage, bool SideEffecting);

// Only server-owned adapters appear here. Model text and process JSON cannot add tools.
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.Ordinal)
    {
        ["create_demo_booking"] = new("create_demo_booking", WorkflowStage.Executing, true)
    };

    public ToolDefinition? Resolve(string name) => _tools.GetValueOrDefault(name);
}
