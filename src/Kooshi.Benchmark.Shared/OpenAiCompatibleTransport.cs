using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kooshi.Benchmark.Shared;

public sealed record LlmTransportResult(string Message, string? ToolName, string? Arguments,
    int? InputTokens, int? OutputTokens);

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
        object payload, CancellationToken ct)
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
            throw new HttpRequestException($"LLM request failed with HTTP {(int)response.StatusCode}");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0].GetProperty("message");
        var content = choice.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString() ?? "" : "";
        string? tool = null, arguments = null;
        if (choice.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
        {
            if (calls.GetArrayLength() != 1) throw new JsonException("Expected exactly one model tool call.");
            var function = calls[0].GetProperty("function");
            tool = function.GetProperty("name").GetString();
            arguments = function.GetProperty("arguments").GetString();
        }
        int? inputTokens = null, outputTokens = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p)) inputTokens = p.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var c)) outputTokens = c.GetInt32();
        }
        return new(content, tool, arguments, inputTokens, outputTokens);
    }
}
