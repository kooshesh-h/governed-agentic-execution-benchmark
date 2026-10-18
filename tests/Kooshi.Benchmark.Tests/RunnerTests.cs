using System.Text.Json;
using System.Net;
using Kooshi.Benchmark.Runner;
using RunnerBenchmark = Kooshi.Benchmark.Runner.Benchmark;
using Microsoft.Data.Sqlite;
using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.Tests;

public class RunnerTests
{
    private static Scenario Sample() => new("successful-booking", "test", "valid", [], "confirm", "success", ["one effect"], 30);

    [Fact]
    public void CliParsesSmokeAndRejectsUnknownOptions()
    {
        var options = RunnerBenchmark.Parse(["--smoke", "--seed", "7", "--dry-run"]);
        Assert.Equal(1, options.Repetitions);
        Assert.Equal(7, options.Seed);
        Assert.True(options.DryRun);
        Assert.Throws<ArgumentException>(() => RunnerBenchmark.Parse(["--bogus"]));
    }

    [Fact]
    public void OrderIsDeterministicAndComplete()
    {
        var options = RunnerBenchmark.Parse(["--repetitions", "30", "--seed", "42"]);
        var first = RunnerBenchmark.Order([Sample()], options);
        var second = RunnerBenchmark.Order([Sample()], options);
        Assert.Equal(90, first.Count);
        Assert.Equal(first.Select(x => (x.Architecture, x.Scenario.Id, x.Repetition)), second.Select(x => (x.Architecture, x.Scenario.Id, x.Repetition)));
        Assert.Equal(30, first.Count(x => x.Architecture == "DirectLlm"));
    }

    [Fact]
    public void LoaderReadsSevenScenarioDefinitions()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "scenarios");
        var scenarios = RunnerBenchmark.Load(directory);
        Assert.Equal(7, scenarios.Count);
        Assert.Contains(scenarios, x => x.Id == "transient-retry" && x.Fault == "transient");
    }

    [Fact]
    public void NaturalLanguageVariantsPreserveScenarioActionsAndFaults()
    {
        var originals = RunnerBenchmark.Load(Path.Combine(AppContext.BaseDirectory, "scenarios")).ToDictionary(x => x.Id);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenarios", "variants", "natural-language-v1.json")));
        var variants = document.RootElement.GetProperty("variants").EnumerateArray().ToArray();
        Assert.Equal(7, variants.Length);
        foreach (var variant in variants)
        {
            var original = originals[variant.GetProperty("scenarioId").GetString()!];
            Assert.Equal(original.Action, variant.GetProperty("action").GetString());
            Assert.Equal(original.Fault, variant.GetProperty("fault").GetString());
            Assert.Equal(original.FollowUps.Length, variant.GetProperty("followUps").GetArrayLength());
        }
    }

    [Fact]
    public void PilotProtocolLoadsSevenPairedNaturalLanguageScenarios()
    {
        var scenarios = RunnerBenchmark.Load(Path.Combine(AppContext.BaseDirectory, "scenarios"));
        var pilotPath = Path.Combine(AppContext.BaseDirectory, "pilot", "phase-8b-v1.json");
        var (protocol, selected) = RunnerBenchmark.LoadPilot(AppContext.BaseDirectory, pilotPath, scenarios);
        Assert.Equal("8B-v1", protocol.Version);
        Assert.Equal(7, selected.Count);
        Assert.Equal("cancellation", selected[0].Id);
        Assert.Contains("Please", selected.Single(x => x.Id == "successful-booking").Initial);
        Assert.Equal(21, RunnerBenchmark.Order(selected, RunnerBenchmark.Parse(["--smoke", "--seed", "42"])).Count);
        Assert.Equal("customerName:", scenarios.Single(x => x.Id == "successful-booking").Initial[..13]);
    }

    [Fact]
    public void EvaluatorRequiresPersistedEffectAndValidationEvidence()
    {
        var ready = JsonSerializer.SerializeToElement(new { status = "ready", businessEffects = Array.Empty<object>() });
        var collecting = JsonSerializer.SerializeToElement(new { status = "collecting", businessEffects = Array.Empty<object>() });
        var succeeded = JsonSerializer.SerializeToElement(new { status = "succeeded", businessEffects = new[] { new { bookingId = "one" } } });
        Assert.True(RunnerBenchmark.Evaluate("successful-booking", ready, succeeded).Success);
        Assert.True(RunnerBenchmark.Evaluate("invalid-fields", collecting, succeeded).Success);
        Assert.False(RunnerBenchmark.Evaluate("invalid-fields", ready, succeeded).Success);
        Assert.Null(RunnerBenchmark.Evaluate("successful-booking", ready, default).Success);
    }

    [Fact]
    public void NullableMetricsRemainMissing()
    {
        var metrics = JsonSerializer.SerializeToElement(new { inputTokens = (int?)null });
        Assert.Null(RunnerBenchmark.Int(metrics, "inputTokens"));
        Assert.Null(RunnerBenchmark.Int(metrics, "notPresent"));
    }

    [Fact]
    public void CsvEscapesQuotesAndJsonExportsEmptyRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "benchmark-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunnerBenchmark.Export(directory, new { complete = false }, Array.Empty<Run>());
            Assert.True(File.Exists(Path.Combine(directory, "runs.json")));
            Assert.True(File.Exists(Path.Combine(directory, "summary.csv")));
            Assert.Equal("[]", File.ReadAllText(Path.Combine(directory, "runs.json")).Trim());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ApiAdapterReportsHttpAndInvalidJsonFailures()
    {
        using var client = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad") }))
        { BaseAddress = new Uri("http://localhost") };
        await Assert.ThrowsAsync<HttpRequestException>(() => RunnerBenchmark.Send(client, HttpMethod.Get, "/x", null, CancellationToken.None));
        using var invalid = new HttpClient(new StubHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") }))
        { BaseAddress = new Uri("http://localhost") };
        await Assert.ThrowsAsync<InvalidDataException>(() => RunnerBenchmark.Send(invalid, HttpMethod.Get, "/x", null, CancellationToken.None));
    }

    [Fact]
    public void SummaryKeepsUnavailableMetricsOutOfDenominators()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new Run("exp", "run", "cancellation", "DirectLlm", 1, "simulated", null, 42,
            now, now.AddMilliseconds(100), 100, "cancelled", true, "satisfied", Guid.NewGuid(),
            1, 1, null, null, 0, 0, null, 0, null, null, 0, 0, 0, null, false, null, null);
        var summary = JsonSerializer.SerializeToElement(RunnerBenchmark.Summary([run])[0], RunnerBenchmark.Json);
        Assert.Equal(1, summary.GetProperty("totalRuns").GetInt32());
        Assert.Equal(100, summary.GetProperty("p95LatencyMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("meanInputTokens").ValueKind);
        Assert.Equal(0, summary.GetProperty("duplicateEffectObservedRuns").GetInt32());
    }

    [Fact]
    public void CostRequiresObservedTokensAndExplicitPrices()
    {
        Assert.Null(RunnerBenchmark.EstimateUsd(null, 10, 2, 4));
        Assert.Null(RunnerBenchmark.EstimateUsd(100, 10, null, 4));
        Assert.Equal(0.00024, RunnerBenchmark.EstimateUsd(100, 10, 2, 4)!.Value, 8);
        var options = RunnerBenchmark.Parse(["--mode", "real", "--max-llm-requests", "20", "--max-estimated-usd", "1.5", "--input-usd-per-million", "2", "--output-usd-per-million", "4"]);
        Assert.Equal(20, options.MaxLlmRequests);
        Assert.Equal(1.5, options.MaxEstimatedUsd);
        var now = DateTimeOffset.UtcNow;
        var run = new Run("exp", "run", "successful-booking", "DirectLlm", 1, "real_llm", "fixture-model", 42,
            now, now, 1, "succeeded", true, "satisfied", Guid.NewGuid(),
            1, 20, 100, 10, 1, 1, null, 0, null, null, 0, 0, 0, null, false, null, null);
        Assert.False(RunnerBenchmark.CanContinueReal([run], options));
        Assert.False(RunnerBenchmark.CanContinueReal([run with { LlmRequests = null }], options));
        Assert.True(RunnerBenchmark.CanContinueReal([run with { LlmRequests = 1 }], options));
    }

    [Fact]
    public void SharedRequestCounterRejectsBeforeAnExtraProviderRequest()
    {
        var path = Path.Combine(Path.GetTempPath(), "llm-request-count-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "0");
            OpenAiCompatibleTransport.ReserveRequest(path, 2);
            OpenAiCompatibleTransport.ReserveRequest(path, 2);
            Assert.Throws<InvalidOperationException>(() => OpenAiCompatibleTransport.ReserveRequest(path, 2));
            Assert.Equal("2", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IndependentBookingCheckRejectsMisreportedSuccess()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var id = Guid.NewGuid();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE bookings(execution_id TEXT PRIMARY KEY, booking_id TEXT NOT NULL)";
                command.ExecuteNonQuery();
            }
            Assert.Equal(0, RunnerBenchmark.CountCommittedRows(path, id));
            Assert.False(RunnerBenchmark.VerifyCommittedRows("successful-booking", (true, "satisfied"), 0).Success);
            Assert.Null(RunnerBenchmark.VerifyCommittedRows("successful-booking", (true, "satisfied"), null).Success);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PersistedStateMustConfirmBeforeSuccessfulBooking()
    {
        var scenario = new Scenario("successful-booking", "test", "request", [], "confirm", "success", ["one effect"], 30,
            new Dictionary<string, string> { ["email"] = "ada@example.org" });
        var final = JsonSerializer.SerializeToElement(new { status = "succeeded", businessEffects = new[] { new { bookingId = "one" } } });
        var unconfirmed = JsonSerializer.SerializeToElement(new { status = "succeeded", businessEffects = new[] { new { bookingId = "one" } },
            confirmed = false, events = Array.Empty<object>(), booking = new { email = "ada@example.org" } });
        Assert.False(RunnerBenchmark.VerifyPersistedState(scenario, final, unconfirmed, (true, "satisfied")).Success);
        Assert.Null(RunnerBenchmark.VerifyPersistedState(scenario, final, null, (true, "satisfied")).Success);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

}

