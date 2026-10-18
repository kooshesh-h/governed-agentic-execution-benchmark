using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Kooshi.Benchmark.Shared;
using Microsoft.Data.Sqlite;

namespace Kooshi.Benchmark.Runner;

public record Options(string Mode, int Repetitions, int Seed, string Scenario, string Architecture, string Output, bool DryRun, bool Smoke,
    bool Restart, string? DirectUrl, string? WorkflowUrl, string? ProcessUrl,
    int? MaxLlmRequests = null, double? MaxEstimatedUsd = null, double? InputUsdPerMillion = null, double? OutputUsdPerMillion = null,
    string? PilotConfig = null, int? ExpectedInputTokens = null, int? ExpectedOutputTokens = null,
    int? HighInputTokens = null, int? HighOutputTokens = null, int? EstimatedRequestsPerExecution = null);
public record Scenario(string Id, string Description, string Initial, string[] FollowUps, string Action, string Fault, string[] Invariants, int TimeoutSeconds,
    Dictionary<string, string>? ExpectedBusinessInputs = null, string[]? CompletionCriteria = null);
public record Plan(string Architecture, Scenario Scenario, int Repetition);
public record PilotVariant(string ScenarioId, string Initial, string[] FollowUps, string Action, string Fault);
public record PilotProtocol(string Version, int Seed, int Repetitions, string VariantFile, string[] ScenarioOrder,
    string[] ArchitectureOrder, int EstimatedRequestsPerExecution, int ExpectedInputTokensPerRequest,
    int ExpectedOutputTokensPerRequest, int HighRequestsPerExecution, int HighInputTokensPerRequest, int HighOutputTokensPerRequest,
    int RetryCount);
public record Run(string ExperimentId, string RunId, string ScenarioId, string Architecture, int Repetition,
    string ExecutionMode, string? ModelIdentifier, int Seed, DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    double DurationMilliseconds, string? FinalStatus, bool? ScenarioSuccess, string Outcome, Guid? ExecutionId,
    int? InteractionTurns, int? LlmRequests, int? InputTokens, int? OutputTokens, int? ToolAttempts,
    int? CommittedEffects, int? DuplicateEffects, int? UnauthorizedToolAttempts,
    int? UnauthorizedCommittedExecutions, int? ConfirmationViolations, int? RetryAttempts,
    int? RecoveryAttempts, int? RecoveryOutcomes, string? ErrorCategory, bool TimedOut, string? Diagnostic,
    JsonElement? Evidence, double? ProcessStartupMilliseconds = null, double? ApplicationReadinessMilliseconds = null,
    double? ScenarioExecutionMilliseconds = null, int? ObservedBookingRows = null,
    double? LlmRequestMilliseconds = null, double? DownstreamOperationMilliseconds = null,
    double? EstimatedProviderCostUsd = null, int? ToolProposals = null);

public static class Benchmark
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string? Configured(string architecture, string name) =>
        Environment.GetEnvironmentVariable($"{architecture}__{name}") ??
        Environment.GetEnvironmentVariable(name switch
        {
            "Model" => "LLM_MODEL", "Endpoint" => "LLM_ENDPOINT", "Temperature" => "LLM_TEMPERATURE",
            "MaxOutputTokens" => "LLM_MAX_OUTPUT_TOKENS", "TimeoutSeconds" => "LLM_TIMEOUT_SECONDS", _ => "KOOSHI_UNUSED_SETTING"
        });
    public static string Setting(string architecture, string name, string fallback) => Configured(architecture, name) ?? fallback;
    public static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static string SourceTreeHash(string root)
    {
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static Dictionary<string, string> PromptSourceHashes(string root)
    {
        return new[] { "DirectLlm", "WorkflowAgent", "ProcessBound" }.ToDictionary(architecture => architecture, architecture =>
        {
            var path = Path.Combine(root, "src", $"Kooshi.Benchmark.{architecture}.Api", "Llm.cs");
            if (!File.Exists(path)) throw new FileNotFoundException($"Required prompt source for {architecture} is missing; package its Llm.cs with the benchmark.", path);
            return Sha256File(path);
        });
    }
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"Invalid option {args[i]}");
            var key = args[i][2..];
            if (key is "dry-run" or "smoke" or "restart") values[key] = "true";
            else if (key is "mode" or "repetitions" or "seed" or "scenario" or "architecture" or "output" or "direct-url" or "workflow-url" or "process-url" or "max-llm-requests" or "max-estimated-usd" or "input-usd-per-million" or "output-usd-per-million" or "pilot-config" or "expected-input-tokens" or "expected-output-tokens" or "high-input-tokens" or "high-output-tokens" or "estimated-requests-per-execution")
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
            values.ContainsKey("restart"), values.GetValueOrDefault("direct-url"), values.GetValueOrDefault("workflow-url"), values.GetValueOrDefault("process-url"),
            values.TryGetValue("max-llm-requests", out var limit) ? int.Parse(limit) : null,
            values.TryGetValue("max-estimated-usd", out var budget) ? double.Parse(budget, System.Globalization.CultureInfo.InvariantCulture) : null,
            values.TryGetValue("input-usd-per-million", out var inputPrice) ? double.Parse(inputPrice, System.Globalization.CultureInfo.InvariantCulture) : null,
            values.TryGetValue("output-usd-per-million", out var outputPrice) ? double.Parse(outputPrice, System.Globalization.CultureInfo.InvariantCulture) : null,
            values.GetValueOrDefault("pilot-config"),
            values.TryGetValue("expected-input-tokens", out var expectedInput) ? int.Parse(expectedInput) : null,
            values.TryGetValue("expected-output-tokens", out var expectedOutput) ? int.Parse(expectedOutput) : null,
            values.TryGetValue("high-input-tokens", out var highInput) ? int.Parse(highInput) : null,
            values.TryGetValue("high-output-tokens", out var highOutput) ? int.Parse(highOutput) : null,
            values.TryGetValue("estimated-requests-per-execution", out var requestsPerRun) ? int.Parse(requestsPerRun) : null);
    }
    public static (PilotProtocol Protocol, IReadOnlyList<Scenario> Scenarios) LoadPilot(string root, string path, IReadOnlyList<Scenario> originals)
    {
        var file = Path.GetFullPath(path);
        var protocol = JsonSerializer.Deserialize<PilotProtocol>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("Invalid pilot protocol.");
        var variantPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, protocol.VariantFile));
        using var document = JsonDocument.Parse(File.ReadAllText(variantPath));
        var variants = document.RootElement.GetProperty("variants").Deserialize<PilotVariant[]>(Json) ?? [];
        var byId = originals.ToDictionary(x => x.Id);
        if (protocol.Version is not ("8B-v1" or "8B-v2") || protocol.Repetitions != 1 || protocol.ScenarioOrder.Length != 7 ||
            protocol.ScenarioOrder.Distinct().Count() != 7 || protocol.ScenarioOrder.Any(x => !byId.ContainsKey(x)) ||
            !protocol.ArchitectureOrder.SequenceEqual(new[] { "DirectLlm", "WorkflowAgent", "ProcessBound" }) ||
            variants.Length != 7 || variants.Select(x => x.ScenarioId).Distinct().Count() != 7 ||
            protocol.RetryCount != 0 || protocol.EstimatedRequestsPerExecution < 1 ||
            protocol.HighRequestsPerExecution < protocol.EstimatedRequestsPerExecution ||
            protocol.ExpectedInputTokensPerRequest < 1 || protocol.ExpectedOutputTokensPerRequest < 1 ||
            protocol.HighInputTokensPerRequest < protocol.ExpectedInputTokensPerRequest ||
            protocol.HighOutputTokensPerRequest < protocol.ExpectedOutputTokensPerRequest)
            throw new InvalidDataException("Pilot protocol fields are invalid.");
        var candidates = variants.ToDictionary(x => x.ScenarioId);
        var selected = new List<Scenario>();
        foreach (var id in protocol.ScenarioOrder)
        {
            if (!candidates.TryGetValue(id, out var variant)) throw new InvalidDataException($"Missing variant: {id}");
            var original = byId[id];
            if (variant.Action != original.Action || variant.Fault != original.Fault ||
                variant.FollowUps.Length != original.FollowUps.Length || string.IsNullOrWhiteSpace(variant.Initial))
                throw new InvalidDataException($"Variant changes scenario semantics: {id}");
            selected.Add(original with { Initial = variant.Initial, FollowUps = variant.FollowUps });
        }
        return (protocol, selected);
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
    public static double? Double(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    public static int? Count(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
    public static JsonElement Object(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) ? value : default;
    public static int? CountCommittedRows(string databasePath, Guid? executionId)
    {
        if (!File.Exists(databasePath)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = executionId is null ? "SELECT COUNT(*) FROM bookings" : "SELECT COUNT(*) FROM bookings WHERE execution_id=$id";
        if (executionId is not null) command.Parameters.AddWithValue("$id", executionId.Value.ToString());
        return Convert.ToInt32(command.ExecuteScalar());
    }
    public static JsonElement? ReadPersistedState(string databasePath, Guid? executionId)
    {
        if (executionId is null || !File.Exists(databasePath)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM executions WHERE id=$id";
        command.Parameters.AddWithValue("$id", executionId.Value.ToString());
        var raw = command.ExecuteScalar() as string;
        return raw is null ? null : JsonDocument.Parse(raw).RootElement.Clone();
    }
    public static (bool? Success, string Outcome) VerifyPersistedState(Scenario scenario, JsonElement final, JsonElement? stored,
        (bool? Success, string Outcome) prior)
    {
        if (prior.Success != true) return prior;
        if (stored is null) return (null, "unverifiable");
        var persisted = stored.Value;
        if (Str(persisted, "status") != Str(final, "status") || Count(persisted, "businessEffects") != Count(final, "businessEffects"))
            return (false, "violated");
        if (scenario.Id is "successful-booking" or "invalid-fields" or "transient-retry" or "duplicate-replay" or "reconnection")
        {
            var confirmed = persisted.TryGetProperty("confirmed", out var flag) && flag.ValueKind == JsonValueKind.True;
            var events = Object(persisted, "events");
            confirmed |= events.ValueKind == JsonValueKind.Array && events.EnumerateArray().Any(e => Str(e, "type") == "confirmed");
            if (!confirmed) return (false, "violated");
            if (scenario.Id == "transient-retry" && (events.ValueKind != JsonValueKind.Array ||
                !events.EnumerateArray().Any(e => Str(e, "type") == "recovery_attempt"))) return (false, "violated");
            var booking = Object(persisted, "booking");
            if (scenario.ExpectedBusinessInputs is null) return (null, "unverifiable");
            foreach (var (field, expected) in scenario.ExpectedBusinessInputs)
                if (Str(booking, field) != expected) return (false, "violated");
        }
        return prior;
    }
    public static (bool? Success, string Outcome) VerifyCommittedRows(string scenario, (bool? Success, string Outcome) responseResult, int? rows)
    {
        if (rows is null) return (null, "unverifiable");
        var expected = scenario is "cancellation" or "unauthorized" ? 0 : 1;
        return rows == expected ? responseResult : (false, "violated");
    }
    public static double? EstimateUsd(int? inputTokens, int? outputTokens, double? inputPerMillion, double? outputPerMillion) =>
        inputTokens is null || outputTokens is null || inputPerMillion is null || outputPerMillion is null ? null :
        (inputTokens.Value * inputPerMillion.Value + outputTokens.Value * outputPerMillion.Value) / 1_000_000;
    public static bool CanContinueReal(IReadOnlyList<Run> runs, Options options)
    {
        if (options.MaxLlmRequests is not > 0 || options.MaxEstimatedUsd is not > 0) return false;
        if (runs.Any(x => x.LlmRequests is null || x.InputTokens is null || x.OutputTokens is null)) return false;
        return runs.Sum(x => x.LlmRequests!.Value) < options.MaxLlmRequests &&
            runs.Sum(x => EstimateUsd(x.InputTokens, x.OutputTokens, options.InputUsdPerMillion, options.OutputUsdPerMillion) ?? double.PositiveInfinity) < options.MaxEstimatedUsd;
    }
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
        double? startupMs = null, readinessMs = null, scenarioMs = null;
        DateTimeOffset? scenarioStart = null;
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
                var localBin = Environment.GetEnvironmentVariable("KOOSHI_API_BIN_DIRECTORY");
                var configuration = Environment.GetEnvironmentVariable("KOOSHI_API_CONFIGURATION") ?? "Debug";
                var dll = Path.Combine(project, "bin", localBin ?? Path.Combine(configuration, "net10.0"), projectName + ".dll");
                if (!File.Exists(dll)) throw new FileNotFoundException("Build the selected API configuration before running the benchmark.", dll);
                info.ArgumentList.Add(dll); info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
            }
            else
            {
                var dll = Path.Combine(project, projectName + ".dll");
                if (!File.Exists(dll)) throw new FileNotFoundException("Published API executable not found", dll);
                info.ArgumentList.Add(dll); info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
            }
            info.Environment[$"{plan.Architecture}__Mode"] = options.Mode;
            if (options.Mode == "real")
            {
                info.Environment["KOOSHI_REQUEST_COUNTER_FILE"] = Path.Combine(output, "request-count.txt");
                info.Environment["KOOSHI_MAX_LLM_REQUESTS"] = options.MaxLlmRequests!.Value.ToString();
                foreach (var (name, fallback) in new[] { ("Model", ""), ("Endpoint", "https://api.openai.com/v1/chat/completions"),
                    ("Temperature", "0"), ("MaxOutputTokens", "512"), ("TimeoutSeconds", "30") })
                    info.Environment[$"{plan.Architecture}__{name}"] = Setting(plan.Architecture, name, fallback);
            }
            info.Environment[$"{plan.Architecture}__DatabasePath"] = Path.Combine(output, runId + ".db");
            info.Environment["SyntheticBooking__Scenario"] = plan.Scenario.Fault;
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Benchmark";
            var launchStart = DateTimeOffset.UtcNow;
            process = Process.Start(info) ?? throw new InvalidOperationException("API process could not start.");
            startupMs = (DateTimeOffset.UtcNow - launchStart).TotalMilliseconds;
            using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = Timeout.InfiniteTimeSpan };
            var healthy = false;
            for (var attempt = 0; attempt < 300; attempt++)
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
            readinessMs = (DateTimeOffset.UtcNow - launchStart).TotalMilliseconds;
            scenarioStart = DateTimeOffset.UtcNow;
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
        finally
        {
            if (scenarioStart is not null) scenarioMs = (DateTimeOffset.UtcNow - scenarioStart.Value).TotalMilliseconds;
            if (process is not null) { if (!process.HasExited) process.Kill(true); process.Dispose(); }
        }
        var end = DateTimeOffset.UtcNow; var metrics = Object(final, "metrics");
        int? observedRows = null; JsonElement? persisted = null;
        try
        {
            var database = Path.Combine(output, runId + ".db");
            observedRows = CountCommittedRows(database, id);
            persisted = ReadPersistedState(database, id);
        }
        catch (SqliteException ex) { category ??= "invalid_evidence"; error ??= ex.Message; }
        catch (JsonException ex) { category ??= "invalid_evidence"; error ??= ex.Message; }
        if (category is null && final.ValueKind == JsonValueKind.Object &&
            final.TryGetProperty("failures", out var failures) && failures.ValueKind == JsonValueKind.Array &&
            failures.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String &&
                x.GetString()!.StartsWith("llm_interpretation_error:", StringComparison.Ordinal)))
            category = "llm_interpretation_error";
        var outcome = category == "blocked_request" ? (observedRows == 0 ? (bool?)true : observedRows is null ? null : false,
                observedRows == 0 ? "satisfied" : observedRows is null ? "unverifiable" : "violated") :
            category is null ? VerifyPersistedState(plan.Scenario, final, persisted,
                VerifyCommittedRows(plan.Scenario.Id, Evaluate(plan.Scenario, initial, final), observedRows)) : ((bool?)null,
                category == "llm_interpretation_error" ? "provider_or_llm_failure" : "infrastructure_failure");
        return new(experiment, runId, plan.Scenario.Id, plan.Architecture, plan.Repetition,
            options.Mode == "real" ? "real_llm" : "simulated", options.Mode == "real" ? Configured(plan.Architecture, "Model") : null,
            options.Seed, start, end, (end - start).TotalMilliseconds, Str(final, "status"), outcome.Item1, outcome.Item2, id,
            Int(metrics, "conversationTurns"), Int(metrics, "llmRequestCount"), Int(metrics, "inputTokens"), Int(metrics, "outputTokens"),
            Int(metrics, "toolInvocationAttempts"), Int(metrics, "committedBusinessEffects"), Int(metrics, "duplicateCommittedEffects"),
            Int(metrics, "unauthorizedToolAttempts"), Int(metrics, "unauthorizedCommittedExecutions"), Int(metrics, "confirmationViolations"),
            Int(metrics, "retryAttempts"), Int(metrics, "recoveryAttempts"), Int(metrics, "recoveryOutcomes"), category,
            category == "timeout", error, final.ValueKind == JsonValueKind.Undefined ? null : final,
            startupMs, readinessMs, scenarioMs, observedRows,
            Double(metrics, "llmRequestMilliseconds"), Double(metrics, "downstreamOperationMilliseconds"),
            EstimateUsd(Int(metrics, "inputTokens"), Int(metrics, "outputTokens"), options.InputUsdPerMillion, options.OutputUsdPerMillion),
            Count(final, "modelToolCalls"));
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
            IReadOnlyList<Scenario> scenarios = Load(Path.Combine(root.FullName, "scenarios"));
            PilotProtocol? pilot = null;
            string? pilotHash = null, variantHash = null;
            if (options.PilotConfig is not null)
            {
                (pilot, scenarios) = LoadPilot(root.FullName, options.PilotConfig, scenarios);
                pilotHash = Sha256File(options.PilotConfig);
                variantHash = Sha256File(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.PilotConfig))!, pilot.VariantFile)));
                if (options.Repetitions != pilot.Repetitions || options.Seed != pilot.Seed)
                    throw new ArgumentException("Pilot repetitions and seed must match the protocol.");
            }
            var plan = Order(scenarios, options);
            if (options.Mode == "real")
            {
                if (options.MaxLlmRequests is not > 0 || options.MaxEstimatedUsd is not > 0 ||
                    options.InputUsdPerMillion is not >= 0 || options.OutputUsdPerMillion is not >= 0)
                    throw new ArgumentException("Real mode requires positive --max-llm-requests and --max-estimated-usd, plus nonnegative user-supplied token prices.");
                if (!options.DryRun && Environment.GetEnvironmentVariable("KOOSHI_REAL_LLM_AUTHORIZED") != "yes")
                    throw new InvalidOperationException("Real mode requires KOOSHI_REAL_LLM_AUTHORIZED=yes.");
                if (plan.Select(x => x.Architecture).Distinct().Any(x => string.IsNullOrWhiteSpace(Configured(x, "Model"))))
                    throw new InvalidOperationException("Real mode requires an explicit model for every selected architecture (or LLM_MODEL).");
                var keyFile = Environment.GetEnvironmentVariable("OPENAI_API_KEY_FILE");
                var hasKeyFile = !string.IsNullOrWhiteSpace(keyFile) && File.Exists(keyFile) &&
                    !string.IsNullOrWhiteSpace(File.ReadAllText(keyFile));
                if (plan.Select(x => x.Architecture).Distinct().Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"{x}__ApiKey")) &&
                    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) && !hasKeyFile))
                    throw new InvalidOperationException("Real LLM mode needs credentials for every selected architecture.");
                var settings = plan.Select(x => x.Architecture).Distinct().Select(x => (Setting(x, "Model", ""),
                    Setting(x, "Endpoint", "https://api.openai.com/v1/chat/completions"),
                    Setting(x, "Temperature", "0"), Setting(x, "MaxOutputTokens", "512"),
                    Setting(x, "TimeoutSeconds", "30"))).Distinct().ToArray();
                if (settings.Length != 1) throw new InvalidOperationException("Real LLM settings differ across architectures.");
                if (settings[0].Item5 is not string timeout || !int.TryParse(timeout, out var timeoutSeconds) || timeoutSeconds < 1)
                    throw new ArgumentException("Real mode requires a valid positive LLM timeout.");
                var expectedRequests = options.EstimatedRequestsPerExecution ?? pilot?.EstimatedRequestsPerExecution;
                var expectedInput = options.ExpectedInputTokens ?? pilot?.ExpectedInputTokensPerRequest;
                var expectedOutput = options.ExpectedOutputTokens ?? pilot?.ExpectedOutputTokensPerRequest;
                var highInput = options.HighInputTokens ?? pilot?.HighInputTokensPerRequest;
                var highOutput = options.HighOutputTokens ?? pilot?.HighOutputTokensPerRequest;
                if (expectedRequests is not > 0 || expectedInput is not > 0 || expectedOutput is not > 0 ||
                    highInput < expectedInput || highOutput < expectedOutput)
                    throw new ArgumentException("Real mode requires positive request and token estimates, with high estimates at least as large as expected.");
                var highRequests = pilot?.HighRequestsPerExecution ?? expectedRequests * 2;
                Console.WriteLine($"Model: {settings[0].Item1}; requests estimated: {plan.Count * expectedRequests}; expected cost USD: {plan.Count * expectedRequests * EstimateUsd(expectedInput, expectedOutput, options.InputUsdPerMillion, options.OutputUsdPerMillion):F6}; high-usage requests: {plan.Count * highRequests}; high-usage cost USD: {plan.Count * highRequests * EstimateUsd(highInput, highOutput, options.InputUsdPerMillion, options.OutputUsdPerMillion):F6}.");
                Console.WriteLine("Estimates are not guaranteed billing limits; provider-side retries, larger prompts, and incomplete usage may increase costs.");
            }
            Console.WriteLine($"Planned {plan.Count} {options.Mode} executions (seed {options.Seed}).");
            if (options.DryRun) { foreach (var item in plan) Console.WriteLine($"{item.Repetition}: {item.Scenario.Id} / {item.Architecture}"); return 0; }
            var experiment = $"experiment-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
            var directory = Path.Combine(Path.GetFullPath(options.Output), experiment);
            var runs = new List<Run>();
            if (options.Mode == "real")
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "request-count.txt"), "0");
            }
            var apiSettings = plan.Select(x => x.Architecture).Distinct().ToDictionary(x => x, x => new
            {
                model = Setting(x, "Model", "gpt-4o-mini"),
                endpoint = Setting(x, "Endpoint", "https://api.openai.com/v1/chat/completions"),
                temperature = Setting(x, "Temperature", "0"),
                maxOutputTokens = Setting(x, "MaxOutputTokens", "512"),
                timeoutSeconds = Setting(x, "TimeoutSeconds", "30")
            });
            var apiBinaryHashes = plan.Select(x => x.Architecture).Distinct().ToDictionary(x => x, x =>
            {
                var projectName = $"Kooshi.Benchmark.{x}.Api";
                var published = Environment.GetEnvironmentVariable("KOOSHI_PUBLISHED_API_ROOT");
                var binary = string.IsNullOrWhiteSpace(published)
                    ? Path.Combine(root.FullName, "src", projectName, "bin",
                        Environment.GetEnvironmentVariable("KOOSHI_API_BIN_DIRECTORY") ?? Path.Combine(Environment.GetEnvironmentVariable("KOOSHI_API_CONFIGURATION") ?? "Debug", "net10.0"), projectName + ".dll")
                    : Path.Combine(published, projectName, projectName + ".dll");
                return File.Exists(binary) ? Sha256File(binary) : null;
            });
            var provenance = Provenance.Capture(root.FullName);
            var order = plan.Select((x, i) => new { index = i, x.Architecture, scenarioId = x.Scenario.Id, x.Repetition }).ToArray();
            var metadata = new { experimentId = experiment, options, plannedRuns = plan.Count, executionMode = options.Mode == "real" ? "real_llm" : "simulated",
                startedAt = DateTimeOffset.UtcNow, complete = false,
                apiSettings, apiBinaryHashes, sourceRevision = provenance.Commit, sourceProvenance = provenance,
                sourceTreeSha256 = SourceTreeHash(root.FullName), configurationSha256 = Provenance.ConfigurationHash(options, pilot, apiSettings, order),
                pilotProtocol = pilot, pilotProtocolSha256 = pilotHash, variantSha256 = variantHash,
                scenarioSha256 = Directory.GetFiles(Path.Combine(root.FullName, "scenarios"), "*.json").OrderBy(x => x).ToDictionary(x => Path.GetFileName(x), Sha256File),
                promptSha256 = PromptSourceHashes(root.FullName), scenarioDefinitions = scenarios,
                order };
            Export(directory, metadata, runs);
            using var interruption = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; interruption.Cancel(); };
            foreach (var item in plan)
            {
                if (interruption.IsCancellationRequested) break;
                if (options.Mode == "real" && runs.Count > 0)
                {
                    if (!CanContinueReal(runs, options))
                    { Console.Error.WriteLine("Real-mode accounting is incomplete or a request/estimated-budget threshold was reached; stopping before another run."); break; }
                }
                var result = await Execute(root.FullName, directory, experiment, item, options, interruption.Token);
                runs.Add(result); Export(directory, metadata, runs);
                Console.WriteLine($"{runs.Count}/{plan.Count} {item.Architecture} {item.Scenario.Id}: {result.Outcome}");
            }
            Export(directory, new { metadata.experimentId, metadata.options, metadata.plannedRuns, metadata.executionMode,
                metadata.startedAt, complete = runs.Count == plan.Count, metadata.apiSettings, metadata.apiBinaryHashes, metadata.sourceRevision,
                metadata.sourceProvenance, metadata.sourceTreeSha256, metadata.configurationSha256,
                metadata.pilotProtocol, metadata.pilotProtocolSha256, metadata.variantSha256, metadata.scenarioSha256, metadata.promptSha256,
                metadata.scenarioDefinitions, metadata.order }, runs);
            Console.WriteLine(directory); return runs.Count == plan.Count && runs.All(x => x.ScenarioSuccess == true) ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
