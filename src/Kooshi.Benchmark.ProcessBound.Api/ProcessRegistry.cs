using System.Text.Json;
using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.ProcessBound.Api;

public sealed record FieldDefinition(string Name, string Type, bool Required, string? Validation = null);
public sealed record ProcessDefinition(string Id, string Version, string Description, List<FieldDefinition> Fields,
    List<string> Preconditions, List<string> Authorization, bool RequiresConfirmation, List<string> AllowedTools,
    string SideEffect, int RetryLimit, string Recovery);

public sealed class ProcessRegistry
{
    private readonly Dictionary<string, ProcessDefinition> _definitions;
    public ProcessRegistry(IConfiguration configuration, ToolRegistry? tools = null)
    {
        var path = configuration["ProcessBound:RegistryPath"] ?? Path.Combine(AppContext.BaseDirectory, "processes.json");
        var definitions = JsonSerializer.Deserialize<List<ProcessDefinition>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        _definitions = definitions.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        if (_definitions.Count != definitions.Count) throw new InvalidOperationException("Duplicate process IDs in registry.");
        foreach (var p in definitions)
            if (string.IsNullOrWhiteSpace(p.Version) || p.Fields.Count == 0 || p.AllowedTools.Count == 0)
                throw new InvalidOperationException($"Incomplete process definition: {p.Id}");
        foreach (var p in definitions.Where(p => p.Id.Equals("demo-booking", StringComparison.OrdinalIgnoreCase)))
            if (p.Fields.Count != BookingRules.Fields.Count ||
                p.Fields.Where((field, index) =>
                    field.Name != BookingRules.Fields[index].Name || field.Type != BookingRules.Fields[index].Type ||
                    field.Required != BookingRules.Fields[index].Required || field.Validation != BookingRules.Fields[index].Validation).Any())
                throw new InvalidOperationException("demo-booking process fields differ from the shared booking schema.");
        tools ??= new ToolRegistry();
        foreach (var p in definitions)
            foreach (var tool in p.AllowedTools)
                if (tools.Resolve(tool) is null)
                    throw new InvalidOperationException($"Process {p.Id} references unregistered tool {tool}.");
    }
    public ProcessDefinition? Resolve(string id) => _definitions.GetValueOrDefault(id);
    public IReadOnlyCollection<ProcessDefinition> All => _definitions.Values;
}

// P and A are the resolved process identity/version and typed field values. Pre, Auth, Conf,
// T, S and Rec are immutable policy snapshots. Prov links decisions to the execution audit.
public sealed record IntentExecutionContract(
    string ProcessId, string ProcessVersion, Dictionary<string, string> Arguments,
    List<string> Preconditions, List<string> Authorization, bool ConfirmationRequired,
    List<string> AllowedTools, string SideEffect, int RetryLimit, string Recovery,
    Guid ExecutionId, DateTimeOffset ConstructedAt);

public static class ContractFactory
{
    public static IntentExecutionContract Create(ProcessDefinition process, Guid executionId) =>
        new(process.Id, process.Version, new(StringComparer.OrdinalIgnoreCase),
            [.. process.Preconditions], [.. process.Authorization], process.RequiresConfirmation,
            [.. process.AllowedTools], process.SideEffect, process.RetryLimit, process.Recovery,
            executionId, DateTimeOffset.UtcNow);
}
