using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kooshi.Benchmark.Shared;

public sealed record LlmTransportResult(string Message, string? ToolName, string? Arguments,
    int? InputTokens, int? OutputTokens);

public static class OpenAiCompatibleTransport
{
    public static async Task<LlmTransportResult> SendAsync(HttpClient http, string endpoint, string apiKey,
        object payload, CancellationToken ct)
    {
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
