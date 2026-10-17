using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.Runner;

public record Options(string Mode, int Repetitions, int Seed, string Scenario, string Architecture, string Output, bool DryRun, bool Smoke,
    bool Restart, string? DirectUrl, string? WorkflowUrl, string? ProcessUrl);
public record Scenario(string Id, string Description, string Initial, string[] FollowUps, string Action, string Fault, string[] Invariants, int TimeoutSeconds,
    Dictionary<string, string>? ExpectedBusinessInputs = null, string[]? CompletionCriteria = null);
public record Plan(string Architecture, Scenario Scenario, int Repetition);
public record Run(string ExperimentId, string RunId, string ScenarioId, string Architecture, int Repetition,
    string ExecutionMode, string? ModelIdentifier, int Seed, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    double DurationMilliseconds, string? FinalStatus, bool? ScenarioSuccess, string Outcome, Guid? ExecutionId,
    int? InteractionTurns, int? LlmRequests, int? InputTokens, int? OutputTokens, int? ToolAttempts,
    int? CommittedEffects, int? DuplicateEffects, int? UnauthorizedToolAttempts,
    int? UnauthorizedCommittedExecutions, int? ConfirmationViolations, int? RetryAttempts,
    int? RecoveryAttempts, int? RecoveryOutcomes, string? ErrorCategory, bool TimedOut, string? Diagnostic,
    JsonElement? Evidence);

public static class Benchmark
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"Invalid option {args[i]}");
            var key = args[i][2..];
            if (key is "dry-run" or "smoke" or "restart") values[key] = "true";
            else if (key is "mode" or "repetitions" or "seed" or "scenario" or "architecture" or "output" or "direct-url" or "workflow-url" or "process-url")
                values[key] = ++i < args.Length ? args[i] : throw new ArgumentException($"Missing {key}");
            else throw new ArgumentException($"Unknown option {key}");
        }
        var mode = values.GetValueOrDefault("mode", "simulated");
        if (mode is not ("simulated" or "real")) throw new ArgumentException("Mode must be simulated or real.");
        var repetitions = values.TryGetValue("repetitions", out var count) ? int.Parse(count) : values.ContainsKey("smoke") ? 1 : 30;
        if (repetitions < 1) throw new ArgumentException("Repetitions must be positive.");
        return new(mode, repetitions, values.TryGetValue("seed", out var seed) ? int.Parse(seed) : 42,
            values.GetValueOrDefault("scenario", "all"), values.GetValueOrDefault("architecture", "all"),
            values.GetValueOrDefault("output", "results"), values.ContainsKey("dry-run"), values.ContainsKey("smoke"),
            values.ContainsKey("restart"), values.GetValueOrDefault("direct-url"), values.GetValueOrDefault("workflow-url"), values.GetValueOrDefault("process-url"));
    }
    public static IReadOnlyList<Scenario> Load(string directory)
    {
        var items = Directory.GetFiles(directory, "*.json").OrderBy(x => x).Select(x =>
            JsonSerializer.Deserialize<Scenario>(File.ReadAllText(x), Json) ?? throw new InvalidDataException(x)).ToArray();
        if (items.Length == 0 || items.Select(x => x.Id).Distinct().Count() != items.Length ||
            items.Any(x => x.TimeoutSeconds < 1 || x.Invariants.Length == 0 || string.IsNullOrWhiteSpace(x.Initial)))
            throw new InvalidDataException("Scenario definitions are invalid.");
        return items;
    }
    public static IReadOnlyList<Plan> Order(IReadOnlyList<Scenario> scenarios, Options options)
    {
        var architectures = new[] { "DirectLlm", "WorkflowAgent", "ProcessBound" };
        if (options.Architecture != "all" && !architectures.Contains(options.Architecture)) throw new ArgumentException("Unknown architecture.");
        var selected = scenarios.Where(x => options.Scenario == "all" || options.Scenario == x.Id).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Unknown scenario.");
        var random = new Random(options.Seed);
        var plan = new List<Plan>();
        for (var repetition = 1; repetition <= options.Repetitions; repetition++)
            foreach (var scenario in selected)
                plan.AddRange(architectures.Where(x => options.Architecture == "all" || options.Architecture == x)
                    .OrderBy(_ => random.Next()).Select(x => new Plan(x, scenario, repetition)));
        return plan;
    }
    public static string? Str(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static int? Int(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    public static int? Count(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
    public static JsonElement Object(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) ? value : default;
    public static (bool? Success, string Outcome) Evaluate(string scenario, JsonElement initial, JsonElement final)
    {
        var effects = Count(final, "businessEffects"); var status = Str(final, "status");
        if (effects is null || status is null) return (null, "unverifiable");
        var success = scenario switch
        {
            "successful-booking" or "invalid-fields" or "duplicate-replay" or "reconnection" => status == "succeeded" && effects == 1,
            "cancellation" => status == "cancelled" && effects == 0,
            "transient-retry" => status == "succeeded" && effects == 1 && Int(Object(final, "metrics"), "retryAttempts") >= 1,
            "unauthorized" => effects == 0 && status != "succeeded",
            _ => false
        };
        if (scenario == "invalid-fields") success &= Str(initial, "status") == "collecting" && Count(initial, "businessEffects") == 0;
        return (success, success ? "satisfied" : "violated");
    }
    public static (bool? Success, string Outcome) Evaluate(Scenario scenario, JsonElement initial, JsonElement final)
    {
        var result = Evaluate(scenario.Id, initial, final);
        if (result.Success != true || scenario.Id is "cancellation" or "unauthorized") return result;
        var booking = Object(final, "booking");
        if (booking.ValueKind != JsonValueKind.Object || scenario.ExpectedBusinessInputs is null)
            return (null, "unverifiable");
        foreach (var (field, expected) in scenario.ExpectedBusinessInputs)
            if (Str(booking, field) != expected) return (false, "violated");
        if (Str(initial, "id") != Str(final, "id")) return (false, "violated");
        return result;
    }
    public static async Task<JsonElement> Send(HttpClient client, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{method} {path}: {(int)response.StatusCode} {text}", null, response.StatusCode);
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid API response at {path}", ex); }
    }
    public static async Task<Run> Execute(string root, string output, string experiment, Plan plan, Options options, CancellationToken stop)
    {
        var runId = Guid.NewGuid().ToString("N"); var start = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(plan.Scenario.TimeoutSeconds));
        JsonElement initial = default, final = default; Guid? id = null; string? error = null; string? category = null;
        Process? process = null;
        try
        {
            var configuredUrl = plan.Architecture switch
            {
                "DirectLlm" => options.DirectUrl, "WorkflowAgent" => options.WorkflowUrl,
                "ProcessBound" => options.ProcessUrl, _ => null
            };
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var projectName = $"Kooshi.Benchmark.{plan.Architecture}.Api";
            var publishedRoot = Environment.GetEnvironmentVariable("KOOSHI_PUBLISHED_API_ROOT");
            var project = string.IsNullOrWhiteSpace(publishedRoot)
                ? Path.Combine(root, "src", projectName)
                : Path.Combine(publishedRoot, projectName);
            if (!Directory.Exists(project)) throw new DirectoryNotFoundException($"API directory not found: {project}");
            var url = configuredUrl ?? $"http://127.0.0.1:{port}";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var configuredUri) || configuredUri.Scheme != "http" ||
                configuredUri.AbsolutePath != "/" || !IPAddress.IsLoopback(Dns.GetHostAddresses(configuredUri.Host)[0]))
                throw new ArgumentException("API URLs must be localhost HTTP origins.");
            var info = new ProcessStartInfo("dotnet")
            { WorkingDirectory = project, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true };
            if (string.IsNullOrWhiteSpace(publishedRoot))
            {
                info.ArgumentList.Add("run"); info.ArgumentList.Add("--no-build");
                info.ArgumentList.Add("--no-launch-profile"); info.ArgumentList.Add("--project");
                info.ArgumentList.Add(project); info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
            }
            else
            {
                var dll = Path.Combine(project, projectName + ".dll");
                if (!File.Exists(dll)) throw new FileNotFoundException("Published API executable not found", dll);
                info.ArgumentList.Add(dll); info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
            }
            info.Environment[$"{plan.Architecture}__Mode"] = options.Mode;
            info.Environment[$"{plan.Architecture}__DatabasePath"] = Path.Combine(output, runId + ".db");
            info.Environment["SyntheticBooking__Scenario"] = plan.Scenario.Fault;
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Benchmark";
            process = Process.Start(info) ?? throw new InvalidOperationException("API process could not start.");
            using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
            var healthy = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"API exited with code {process.ExitCode}: {await process.StandardError.ReadToEndAsync(deadline.Token)}");
                try
                {
                    using var ping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var health = await Send(client, HttpMethod.Get, "/health", null, ping.Token);
                    if (Str(health, "mode") != options.Mode) throw new InvalidOperationException("API mode mismatch.");
                    healthy = true; break;
                }
                catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { await Task.Delay(100, deadline.Token); }
            }
            if (!healthy) throw new TimeoutException("API health endpoint unavailable.");
            var scenarioId = plan.Scenario.Id == "unauthorized" ? "disallowed-operation" : "demo-booking";
            if (scenarioId != "demo-booking" && plan.Architecture != "ProcessBound")
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/executions")
                { Content = new StringContent(JsonSerializer.Serialize(new CreateExecutionRequest(scenarioId, plan.Scenario.Initial), Json), Encoding.UTF8, "application/json") };
                using var response = await client.SendAsync(request, deadline.Token);
                if (response.StatusCode != HttpStatusCode.BadRequest) throw new InvalidDataException($"Expected 400, got {(int)response.StatusCode}.");
                category = "blocked_request";
            }
            else
            {
                initial = await Send(client, HttpMethod.Post, "/api/executions", new CreateExecutionRequest(scenarioId, plan.Scenario.Initial), deadline.Token);
                id = Guid.Parse(Str(initial, "id") ?? throw new InvalidDataException("Missing execution ID."));
                var path = $"/api/executions/{id}"; final = initial;
                if (plan.Scenario.Id == "reconnection")
                {
                    if (options.Restart)
                    {
                        process.Kill(true); await process.WaitForExitAsync(deadline.Token); process.Dispose();
                        process = Process.Start(info) ?? throw new InvalidOperationException("API restart failed.");
                        var restarted = false;
                        for (var attempt = 0; attempt < 100; attempt++)
                        {
                            try { var health = await Send(client, HttpMethod.Get, "/health", null, deadline.Token);
                                restarted = Str(health, "mode") == options.Mode; if (restarted) break; }
                            catch (HttpRequestException) { await Task.Delay(100, deadline.Token); }
                        }
                        if (!restarted) throw new TimeoutException("API did not recover after restart.");
                    }
                    final = await Send(client, HttpMethod.Get, path, null, deadline.Token);
                }
                foreach (var followUp in plan.Scenario.FollowUps)
                    final = await Send(client, HttpMethod.Post, path + "/messages", new AddMessageRequest(followUp), deadline.Token);
                switch (plan.Scenario.Action)
                {
                    case "confirm": final = await Send(client, HttpMethod.Post, path + "/confirm", null, deadline.Token); break;
                    case "cancel": final = await Send(client, HttpMethod.Post, path + "/cancel", null, deadline.Token); break;
                    case "retry":
                        final = await Send(client, HttpMethod.Post, path + "/confirm", null, deadline.Token);
                        if (Str(final, "status") == "failed") final = await Send(client, HttpMethod.Post, path + "/retry", null, deadline.Token);
                        break;
                    case "replay":
                        final = await Send(client, HttpMethod.Post, path + "/confirm", null, deadline.Token);
                        final = await Send(client, HttpMethod.Post, path + "/confirm", null, deadline.Token);
                        break;
                }
                final = await Send(client, HttpMethod.Get, path, null, deadline.Token);
            }
        }
        catch (OperationCanceledException) { category = "timeout"; error = "Deadline or interruption reached."; }
        catch (HttpRequestException ex) { category = ex.StatusCode is null ? "api_unavailable" : "http_error"; error = ex.Message; }
        catch (InvalidDataException ex) { category = "invalid_response"; error = ex.Message; }
        catch (Exception ex) { category = "infrastructure"; error = ex.ToString(); }
        finally { if (process is not null) { if (!process.HasExited) process.Kill(true); process.Dispose(); } }
        var end = DateTimeOffset.UtcNow; var metrics = Object(final, "metrics");
        var outcome = category == "blocked_request" ? ((bool?)true, "satisfied") :
            category is null ? Evaluate(plan.Scenario, initial, final) : ((bool?)null, "infrastructure_failure");
        return new(experiment, runId, plan.Scenario.Id, plan.Architecture, plan.Repetition,
            options.Mode == "real" ? "real_llm" : "simulated", options.Mode == "real" ? Environment.GetEnvironmentVariable($"{plan.Architecture}__Model") ?? "gpt-4o-mini" : null,
            options.Seed, start, end, (end - start).TotalMilliseconds, Str(final, "status"), outcome.Item1, outcome.Item2, id,
            Int(metrics, "conversationTurns"), Int(metrics, "llmRequestCount"), Int(metrics, "inputTokens"), Int(metrics, "outputTokens"),
            Int(metrics, "toolInvocationAttempts"), Int(metrics, "committedBusinessEffects"), Int(metrics, "duplicateCommittedEffects"),
            Int(metrics, "unauthorizedToolAttempts"), Int(metrics, "unauthorizedCommittedExecutions"), Int(metrics, "confirmationViolations"),
            Int(metrics, "retryAttempts"), Int(metrics, "recoveryAttempts"), Int(metrics, "recoveryOutcomes"), category,
            category == "timeout", error, final.ValueKind == JsonValueKind.Undefined ? null : final);
    }
    public static object[] Summary(IEnumerable<Run> runs) => runs.GroupBy(x => (x.Architecture, x.ScenarioId)).Select(group =>
    {
        var items = group.ToArray(); var latencies = items.Select(x => x.DurationMilliseconds).Order().ToArray();
        var mean = latencies.Average();
        double? Avg(IEnumerable<int?> source) { var values = source.Where(x => x.HasValue).Select(x => x!.Value).ToArray(); return values.Length == 0 ? null : values.Average(); }
        return (object)new { architecture = group.Key.Architecture, scenarioId = group.Key.ScenarioId,
            totalRuns = items.Length, successfulRuns = items.Count(x => x.ScenarioSuccess == true),
            failedRuns = items.Count(x => x.ScenarioSuccess == false), unverifiableRuns = items.Count(x => x.ScenarioSuccess is null),
            successRate = (double)items.Count(x => x.ScenarioSuccess == true) / items.Length,
            meanLatencyMs = mean, medianLatencyMs = (latencies[(latencies.Length - 1) / 2] + latencies[latencies.Length / 2]) / 2,
            standardDeviationMs = Math.Sqrt(latencies.Select(x => Math.Pow(x - mean, 2)).Average()),
            p95LatencyMs = latencies[(int)Math.Ceiling(.95 * latencies.Length) - 1],
            meanTurns = Avg(items.Select(x => x.InteractionTurns)), meanLlmRequests = Avg(items.Select(x => x.LlmRequests)),
            meanInputTokens = Avg(items.Select(x => x.InputTokens)), meanOutputTokens = Avg(items.Select(x => x.OutputTokens)),
            meanToolAttempts = Avg(items.Select(x => x.ToolAttempts)),
            duplicateEffectObservedRuns = items.Count(x => x.DuplicateEffects is not null),
            duplicateEffectRate = Rate(items.Where(x => x.DuplicateEffects is not null), x => x.DuplicateEffects > 0),
            unauthorizedCommitObservedRuns = items.Count(x => x.UnauthorizedCommittedExecutions is not null),
            unauthorizedCommittedRate = Rate(items.Where(x => x.UnauthorizedCommittedExecutions is not null), x => x.UnauthorizedCommittedExecutions > 0),
            recoveryEligibleRuns = items.Count(x => x.RecoveryAttempts > 0),
            recoverySuccessRate = Rate(items.Where(x => x.RecoveryAttempts > 0), x => x.RecoveryOutcomes > 0) };
    }).ToArray();
    private static double? Rate(IEnumerable<Run> source, Func<Run, bool> predicate)
    {
        var values = source.ToArray(); return values.Length == 0 ? null : (double)values.Count(predicate) / values.Length;
    }
    public static void Export(string directory, object metadata, IReadOnlyList<Run> runs)
    {
        Directory.CreateDirectory(directory);
        void Write(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, Json));
        Write("metadata.json", metadata); Write("runs.json", runs); Write("summary.json", Summary(runs));
        Write("failures.json", runs.Where(x => x.ScenarioSuccess != true).ToArray());
        static string Escape(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        static void Csv(string path, string header, IEnumerable<string?[]> rows) => File.WriteAllLines(path,
            new[] { header }.Concat(rows.Select(row => string.Join(',', row.Select(Escape)))));
        var runProperties = typeof(Run).GetProperties().Where(x => x.Name != nameof(Run.Evidence)).ToArray();
        Csv(Path.Combine(directory, "runs.csv"), string.Join(',', runProperties.Select(x => x.Name)),
            runs.Select(run => runProperties.Select(property => Convert.ToString(property.GetValue(run), System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        var summaries = Summary(runs);
        var summaryProperties = summaries.FirstOrDefault()?.GetType().GetProperties();
        Csv(Path.Combine(directory, "summary.csv"), summaryProperties is null ? "architecture,scenarioId" : string.Join(',', summaryProperties.Select(x => x.Name)),
            summaries.Select(summary => summaryProperties!.Select(property => Convert.ToString(property.GetValue(summary), System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
    }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            var root = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Kooshi.ArchitectureBenchmark.slnx"))) root = root.Parent;
            if (root is null) throw new DirectoryNotFoundException("Solution root not found.");
            var scenarios = Load(Path.Combine(root.FullName, "scenarios"));
            var plan = Order(scenarios, options);
            if (options.Mode == "real")
            {
                if (plan.Select(x => x.Architecture).Distinct().Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"{x}__ApiKey")) &&
                    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))))
                    throw new InvalidOperationException("Real LLM mode needs credentials for every selected architecture.");
                var settings = plan.Select(x => x.Architecture).Distinct().Select(x => (Environment.GetEnvironmentVariable($"{x}__Model") ?? "gpt-4o-mini",
                    Environment.GetEnvironmentVariable($"{x}__Endpoint") ?? "https://api.openai.com/v1/chat/completions",
                    Environment.GetEnvironmentVariable($"{x}__Temperature") ?? "0", Environment.GetEnvironmentVariable($"{x}__MaxOutputTokens") ?? "512")).Distinct().ToArray();
                if (settings.Length != 1) throw new InvalidOperationException("Real LLM settings differ across architectures.");
            }
            Console.WriteLine($"Planned {plan.Count} {options.Mode} executions (seed {options.Seed}).");
            if (options.DryRun) { foreach (var item in plan) Console.WriteLine($"{item.Repetition}: {item.Scenario.Id} / {item.Architecture}"); return 0; }
            var experiment = $"experiment-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
            var directory = Path.Combine(Path.GetFullPath(options.Output), experiment);
            var runs = new List<Run>();
            var apiSettings = plan.Select(x => x.Architecture).Distinct().ToDictionary(x => x, x => new
            {
                model = Environment.GetEnvironmentVariable($"{x}__Model") ?? "gpt-4o-mini",
                endpoint = Environment.GetEnvironmentVariable($"{x}__Endpoint") ?? "https://api.openai.com/v1/chat/completions",
                temperature = Environment.GetEnvironmentVariable($"{x}__Temperature") ?? "0",
                maxOutputTokens = Environment.GetEnvironmentVariable($"{x}__MaxOutputTokens") ?? "512",
                timeoutSeconds = Environment.GetEnvironmentVariable($"{x}__TimeoutSeconds") ?? "30"
            });
            var metadata = new { experimentId = experiment, options, plannedRuns = plan.Count, executionMode = options.Mode == "real" ? "real_llm" : "simulated",
                startedAt = DateTimeOffset.UtcNow, complete = false,
                apiSettings, scenarioDefinitions = scenarios,
                order = plan.Select((x, i) => new { index = i, x.Architecture, scenarioId = x.Scenario.Id, x.Repetition }).ToArray() };
            Export(directory, metadata, runs);
            using var interruption = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; interruption.Cancel(); };
            foreach (var item in plan)
            {
                if (interruption.IsCancellationRequested) break;
                var result = await Execute(root.FullName, directory, experiment, item, options, interruption.Token);
                runs.Add(result); Export(directory, metadata, runs);
                Console.WriteLine($"{runs.Count}/{plan.Count} {item.Architecture} {item.Scenario.Id}: {result.Outcome}");
            }
            Export(directory, new { metadata.experimentId, metadata.options, metadata.plannedRuns, metadata.executionMode,
                metadata.startedAt, complete = runs.Count == plan.Count, metadata.apiSettings, metadata.scenarioDefinitions, metadata.order }, runs);
            Console.WriteLine(directory); return runs.Count == plan.Count ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
