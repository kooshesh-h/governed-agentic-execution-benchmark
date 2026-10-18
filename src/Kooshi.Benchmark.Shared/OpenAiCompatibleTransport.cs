using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kooshi.Benchmark.Shared;

public sealed record LlmTransportResult(string Message, string? ToolName, string? Arguments,
    int? InputTokens, int? OutputTokens, string? ToolCallId = null);
public sealed record NativeToolDiagnostic(int ToolCallCount, string[] ToolNames, string? FinishReason,
    bool? ArgumentsValidJson, string[][] ArgumentFieldNames, int ProviderStatus,
    int? InputTokens, int? OutputTokens, string? FailureClassification);

public sealed class LlmResponseParseException(string message, int? inputTokens, int? outputTokens,
    NativeToolDiagnostic? diagnostic = null) : JsonException(message)
{
    public int? InputTokens { get; } = inputTokens;
    public int? OutputTokens { get; } = outputTokens;
    public NativeToolDiagnostic? Diagnostic { get; } = diagnostic;
}

public static class OpenAiCompatibleTransport
{
    private static int requestCount;
    public static void ReserveRequest(string counterFile, int maximum)
    {
        if (maximum < 1) throw new InvalidOperationException("Real LLM request limit is invalid.");
        using var counter = new FileStream(counterFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var buffer = new byte[counter.Length];
        counter.ReadExactly(buffer);
        var consumed = int.Parse(Encoding.UTF8.GetString(buffer));
        if (consumed >= maximum) throw new InvalidOperationException("Real LLM request limit reached before provider request.");
        counter.Position = 0;
        counter.SetLength(0);
        counter.Write(Encoding.UTF8.GetBytes((consumed + 1).ToString()));
        counter.Flush(true);
    }
    public static async Task<LlmTransportResult> SendAsync(HttpClient http, string endpoint, string apiKey,
        object payload, CancellationToken ct, Action<NativeToolDiagnostic>? observe = null)
    {
        var limitText = Environment.GetEnvironmentVariable("KOOSHI_REMAINING_LLM_REQUESTS");
        if (limitText is not null)
        {
            if (!int.TryParse(limitText, out var limit) || limit < 1 || Interlocked.Increment(ref requestCount) > limit)
                throw new InvalidOperationException("Real LLM request limit reached before provider request.");
        }
        var counterFile = Environment.GetEnvironmentVariable("KOOSHI_REQUEST_COUNTER_FILE");
        if (counterFile is not null)
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("KOOSHI_MAX_LLM_REQUESTS"), out var maximum) || maximum < 1)
                throw new InvalidOperationException("Real LLM request limit is invalid.");
            ReserveRequest(counterFile, maximum);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            observe?.Invoke(new(0, [], null, null, [], (int)response.StatusCode, null, null, "provider_http_failure"));
            throw new HttpRequestException($"LLM request failed with HTTP {(int)response.StatusCode}");
        }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p)) inputTokens = p.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var c)) outputTokens = c.GetInt32();
        }
        var selected = root.GetProperty("choices")[0];
        var choice = selected.GetProperty("message");
        var finishReason = selected.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String ? finish.GetString() : null;
        var content = choice.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? "" : "";
        string? tool = null, arguments = null, toolCallId = null;
        if (choice.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
        {
            var names = new List<string>();
            var fields = new List<string[]>();
            var valid = true;
            foreach (var call in calls.EnumerateArray())
            {
                var functionInfo = call.TryGetProperty("function", out var f) ? f : default;
                var rawName = functionInfo.ValueKind == JsonValueKind.Object && functionInfo.TryGetProperty("name", out var n) ? n.GetString() : null;
                names.Add(rawName is "update_booking" or "create_demo_booking" ? rawName : "unrecognized_function");
                var rawArgs = functionInfo.ValueKind == JsonValueKind.Object && functionInfo.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                try
                {
                    using var parsed = JsonDocument.Parse(rawArgs ?? "");
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object) { valid = false; fields.Add([]); }
                    else fields.Add(parsed.RootElement.EnumerateObject().Select(p => p.Name is "customerName" or "email" or "product" or "preferredDate" or "timeZone" ? p.Name : "unrecognized_field").ToArray());
                }
                catch (JsonException) { valid = false; fields.Add([]); }
            }
            var diagnostic = new NativeToolDiagnostic(calls.GetArrayLength(), names.ToArray(), finishReason, valid,
                fields.ToArray(), (int)response.StatusCode, inputTokens, outputTokens,
                calls.GetArrayLength() == 1 ? null : "multiple_tool_calls");
            observe?.Invoke(diagnostic);
            if (calls.GetArrayLength() != 1) throw new LlmResponseParseException("Expected exactly one model tool call.", inputTokens, outputTokens, diagnostic);
            var function = calls[0].GetProperty("function");
            tool = function.GetProperty("name").GetString();
            arguments = function.GetProperty("arguments").GetString();
            toolCallId = calls[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        else observe?.Invoke(new(0, [], finishReason, null, [], (int)response.StatusCode, inputTokens, outputTokens, null));
        return new(content, tool, arguments, inputTokens, outputTokens, toolCallId);
    }
}
