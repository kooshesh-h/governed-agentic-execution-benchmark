using Kooshi.Benchmark.Shared;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kooshi.Benchmark.DirectLlm.Api;

public sealed record LlmDecision(string Message, string? ToolName, string? Arguments, int? InputTokens = null, int? OutputTokens = null,
    string? ToolCallId = null);
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
        {
            var candidate = new Booking { CustomerName = state.Booking.CustomerName, Email = state.Booking.Email,
                Product = state.Booking.Product, PreferredDate = state.Booking.PreferredDate, TimeZone = state.Booking.TimeZone };
            BookingRules.Merge(candidate, JsonSerializer.SerializeToElement(fields));
            if (BookingRules.Validate(candidate).Count == 0)
                return Task.FromResult(new LlmDecision("I propose a demo booking for review.", "create_demo_booking",
                    JsonSerializer.Serialize(candidate)));
            return Task.FromResult(new LlmDecision("I recorded the booking details.", "update_booking", JsonSerializer.Serialize(fields)));
        }
        return Task.FromResult(new LlmDecision("Please provide customerName, email, product, preferredDate (YYYY-MM-DD), and timeZone. You may send them as field: value pairs.", null, null));
    }
}

public sealed class OpenAiCompatibleLlmClient(HttpClient http, IConfiguration configuration) : ILlmClient
{
    public async Task<LlmDecision> InterpretAsync(ExecutionState state, CancellationToken ct)
    {
        var settings = LlmSettings.Read(key => configuration[key], "DirectLlm");
        var messages = new List<object>
        {
            new { role = "system", content = "You assist with demo bookings. Required fields: customerName, email, product, preferredDate (YYYY-MM-DD), timeZone (IANA/Windows identifier). Use update_booking for incomplete or invalid details. Once all details are supplied, propose create_demo_booking with all five fields. A proposal does not execute anything; the API validates it and waits for explicit user confirmation. Never claim a booking was committed. Do not request other tools. Current collected booking: " + JsonSerializer.Serialize(state.Booking) }
        };
        foreach (var m in state.Messages)
        {
            if (m.Role == "assistant_tool_call")
                messages.Add(new { role = "assistant", content = (string?)null,
                    tool_calls = new[] { new { id = m.ToolCallId, type = "function", function = new { name = m.ToolName, arguments = m.Arguments } } } });
            else if (m.Role == "tool")
                messages.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content });
            else if (m.Role == "application")
                messages.Add(new { role = "system", content = "Application validation result: " + m.Content });
            else messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
        }
        var payload = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            max_completion_tokens = settings.MaxOutputTokens,
            messages,
            tools = new object[] {
                new { type = "function", function = new { name = "update_booking", description = "Record incomplete or corrected booking fields without executing a business operation", parameters = new { type = "object", properties = new { customerName = new { type = "string" }, email = new { type = "string" }, product = new { type = "string" }, preferredDate = new { type = "string" }, timeZone = new { type = "string" } }, additionalProperties = false } } },
                new { type = "function", function = new { name = "create_demo_booking", description = "Propose a demo booking for later human confirmation; this call itself has no side effect", parameters = new { type = "object", properties = new { customerName = new { type = "string" }, email = new { type = "string" }, product = new { type = "string" }, preferredDate = new { type = "string" }, timeZone = new { type = "string" } }, required = new[] { "customerName", "email", "product", "preferredDate", "timeZone" }, additionalProperties = false } } }
            },
            tool_choice = "auto",
            // Introduced after the incomplete Phase 8B feasibility pilot. The model still selects the tool and arguments.
            parallel_tool_calls = bool.TryParse(configuration["DirectLlm:ParallelToolCalls"], out var enabled) && enabled
        };
        var result = await OpenAiCompatibleTransport.SendAsync(http, settings.Endpoint, settings.ApiKey, payload, ct,
            diagnostic => state.NativeToolDiagnostics.Add(diagnostic));
        return new LlmDecision(result.Message, result.ToolName, result.Arguments, result.InputTokens, result.OutputTokens, result.ToolCallId);
    }
}


