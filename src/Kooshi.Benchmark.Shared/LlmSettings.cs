using System.Globalization;

namespace Kooshi.Benchmark.Shared;

public sealed record LlmSettings(string Model, string Endpoint, string ApiKey,
    double Temperature, int MaxOutputTokens, int TimeoutSeconds)
{
    public static LlmSettings Read(Func<string, string?> get, string prefix)
    {
        var key = get(prefix + ":ApiKey") ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            var keyFile = Environment.GetEnvironmentVariable("OPENAI_API_KEY_FILE");
            if (!string.IsNullOrWhiteSpace(keyFile))
            {
                if (!File.Exists(keyFile)) throw new InvalidOperationException("OPENAI_API_KEY_FILE does not exist.");
                key = File.ReadAllText(keyFile).Trim();
            }
        }
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Real LLM mode requires {prefix}:ApiKey, OPENAI_API_KEY, or OPENAI_API_KEY_FILE.");
        var model = get(prefix + ":Model");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Real LLM mode requires an explicit {prefix}:Model.");
        return new(model,
            get(prefix + ":Endpoint") ?? "https://api.openai.com/v1/chat/completions", key,
            double.TryParse(get(prefix + ":Temperature"), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : 0,
            int.TryParse(get(prefix + ":MaxOutputTokens"), out var maxOutputTokens) ? maxOutputTokens : 512,
            int.TryParse(get(prefix + ":TimeoutSeconds"), out var timeoutSeconds) ? Math.Max(1, timeoutSeconds) : 30);
    }
}
