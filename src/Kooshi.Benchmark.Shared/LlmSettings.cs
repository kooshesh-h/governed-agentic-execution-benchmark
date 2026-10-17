using System.Globalization;

namespace Kooshi.Benchmark.Shared;

public sealed record LlmSettings(string Model, string Endpoint, string ApiKey,
    double Temperature, int MaxOutputTokens, int TimeoutSeconds)
{
    public static LlmSettings Read(Func<string, string?> get, string prefix)
    {
        var key = get(prefix + ":ApiKey") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Real LLM mode requires {prefix}:ApiKey or OPENAI_API_KEY.");
        return new(get(prefix + ":Model") ?? "gpt-4o-mini",
            get(prefix + ":Endpoint") ?? "https://api.openai.com/v1/chat/completions", key,
            double.TryParse(get(prefix + ":Temperature"), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : 0,
            int.TryParse(get(prefix + ":MaxOutputTokens"), out var maxOutputTokens) ? maxOutputTokens : 512,
            int.TryParse(get(prefix + ":TimeoutSeconds"), out var timeoutSeconds) ? Math.Max(1, timeoutSeconds) : 30);
    }
}
