using Kooshi.Benchmark.Shared;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kooshi.Benchmark.ProcessBound.Api;

public sealed record LlmDecision(string Message, string? ToolName, string? Arguments, int? InputTokens = null, int? OutputTokens = null);
public interface ILlmClient
{
    Task<LlmDecision> InterpretAsync(ExecutionState state, CancellationToken ct);
}

public sealed class SimulatedLlmClient : ILlmClient
{
    public Task<LlmDecision> InterpretAsync(ExecutionState state, CancellationToken ct)
    {
        var input = state.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var fields = new Dictionary<string, string>();
        foreach (Match match in Regex.Matches(input, @"(?i)\b(customerName|name|email|product|preferredDate|date|timeZone)\s*[:=]\s*([^,;\r\n]+)"))
        {
            var key = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "name" => "customerName", "date" => "preferredDate", var x => x
            };
            fields[key] = match.Groups[2].Value.Trim();
        }
        if (fields.Count > 0)
            return Task.FromResult(new LlmDecision("I recorded the booking details.", "update_booking", JsonSerializer.Serialize(fields)));
        return Task.FromResult(new LlmDecision("Please provide customerName, email, product, preferredDate (YYYY-MM-DD), and timeZone. You may send them as field: value pairs.", null, null));
    }
}

public sealed class OpenAiCompatibleLlmClient(HttpClient http, IConfiguration configuration) : ILlmClient
{
    public async Task<LlmDecision> InterpretAsync(ExecutionState state, CancellationToken ct)
    {
        var settings = LlmSettings.Read(key => configuration[key], "ProcessBound");
        var messages = new List<object>
        {
            new { role = "system", content = "You assist with demo bookings. Use update_booking to extract or correct only details provided by the user. Required fields: customerName, email, product, preferredDate (YYYY-MM-DD), timeZone (IANA/Windows identifier). Never claim a booking was committed. Confirmation and actual execution are performed by the API only after explicit user confirmation. Do not request other tools." }
        };
        messages.AddRange(state.Messages.Select(m => (object)new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content }));
        var payload = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            max_completion_tokens = settings.MaxOutputTokens,
            messages,
            tools = new object[] { new { type = "function", function = new { name = "update_booking", description = "Provide booking fields found in the user's request", parameters = new { type = "object", properties = new { customerName = new { type = "string" }, email = new { type = "string" }, product = new { type = "string" }, preferredDate = new { type = "string" }, timeZone = new { type = "string" } }, additionalProperties = false } } } },
            tool_choice = "auto"
        };
        var result = await OpenAiCompatibleTransport.SendAsync(http, settings.Endpoint, settings.ApiKey, payload, ct);
        return new LlmDecision(result.Message, result.ToolName, result.Arguments, result.InputTokens, result.OutputTokens);
    }
}




