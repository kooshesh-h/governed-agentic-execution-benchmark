using System.Text.Json;
using System.Net;
using Kooshi.Benchmark.Runner;
using RunnerBenchmark = Kooshi.Benchmark.Runner.Benchmark;

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

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

}

