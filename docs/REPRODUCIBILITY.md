# Reproducing the research prototype

## Prerequisites and layout

The GitHub Actions workflow runs Docker on a GitHub-hosted Ubuntu runner; Docker is not required on the development computer. Local builds use .NET SDK 10. Docker Engine with Compose v2 and Linux container support is needed only for the optional direct Docker commands below. Run commands from the repository root, the directory containing `Kooshi.ArchitectureBenchmark.slnx`. `src/` contains the three APIs, shared contracts, and runner; `scenarios/` contains seven JSON conditions; `tests/` contains xUnit tests; `docs/` contains protocol and limitations; `results/` is local generated output.

## Build and automated tests

```powershell
dotnet restore Kooshi.ArchitectureBenchmark.slnx
dotnet build Kooshi.ArchitectureBenchmark.slnx --no-restore
dotnet test Kooshi.ArchitectureBenchmark.slnx --no-build
```

To run the local simulated benchmark with .NET 10, first build as above, then run `dotnet run --no-build --project src/Kooshi.Benchmark.Runner -- --mode simulated --repetitions 1 --seed 42 --restart --output results` in PowerShell. It starts a fresh API process and SQLite file for each run.

## GitHub Actions verification without local Docker

The repository can remain private. Once the workflow file is present on GitHub, open the repository's **Actions** tab, select **Research benchmark CI**, choose **Run workflow**, select the branch containing the Phase 7.1 changes, and press **Run workflow**. A push to `master` or `main`, or a pull request, also triggers it. The workflow uses Ubuntu and .NET 10, runs the complete xUnit suite, builds four Docker targets, validates Compose, checks all three `/health` endpoints, and performs 21 simulated runs with `--restart`. It passes an empty API key and a disabled loopback provider endpoint; no paid model calls are made. Each job has a 45-minute limit and individual step limits.

After completion, open the workflow run and download the `research-benchmark-ci-<run ID>` artifact. It contains `results/` with JSON/CSV exports and 21 SQLite files, plus `ci-logs/` with the xUnit TRX report, Docker build log, benchmark console log, API logs, and Compose status. Artifacts are retained for 30 days. On failure, inspect the failing step and these logs; diagnostics and cleanup run even after a failed step. GitHub Actions results cannot be claimed until this workflow has run successfully.

The uploaded experiment uses synthetic names and email addresses. The workflow supplies no credentials, and result metadata stores settings but not API keys. Keep private-repository access and artifact download permissions limited to the intended research team; review artifacts before any public release.

## Manual API testing

```powershell
docker compose up -d direct workflow process
docker compose ps
Invoke-RestMethod http://localhost:5101/health
Invoke-RestMethod http://localhost:5102/health
Invoke-RestMethod http://localhost:5103/health
docker compose down
```

These three services use separate named SQLite volumes and are for manual use. Their health checks request `/health`. The benchmark never connects to them.

## Isolated simulated experiments

The benchmark image carries all three published API executables and the scenario files. The runner starts a fresh child API process and creates a unique SQLite database under `results/<experiment>/` for every run. Child APIs bind only to ephemeral loopback ports inside the container. Results are mounted to the host `results/` directory. All commands below use simulated mode, which makes no provider calls.

```powershell
docker compose --profile benchmark run --rm benchmark --mode simulated --dry-run --repetitions 1
docker compose --profile benchmark run --rm benchmark --mode simulated --repetitions 1 --seed 42 --output results
docker compose --profile benchmark run --rm benchmark --mode simulated --repetitions 1 --scenario reconnection --architecture ProcessBound --restart --output results
```

The first full run plans 21 records (seven scenarios times three architectures). Check `runs.json` has 21 entries and `runs.csv` has 22 lines including its header. `metadata.json` must say `executionMode: simulated` and `complete: true`. Each experiment directory also has `summary.json`, `summary.csv`, and `failures.json`. Run records include outcome, scenario, architecture, metrics, and final API evidence. Summary statistics are descriptive; startup time contributes to wall latency. For 30 repetitions (630 planned runs), use `docker compose --profile benchmark run --rm benchmark --mode simulated --repetitions 30 --seed 42 --output results`. This larger experiment is planned and was not executed in packaging.

Choose one scenario with `--scenario successful-booking` or one architecture with `--architecture DirectLlm`, `WorkflowAgent`, or `ProcessBound`. `--dry-run` validates the plan without starting APIs. `--smoke` defaults to one repetition. Reuse the same `--seed` and exact scenario JSON to reproduce architecture ordering and deterministic simulated input behavior. Wall timings, GUIDs, and timestamps will differ. Compare scenario success and persisted booking fields rather than byte-identical result files.

To verify isolation, inspect the 21 distinct `runId` values and matching `<runId>.db` files in one output directory. Each database is created for one child process. The `--restart` option kills and relaunches that process against the same database during the reconnection scenario; without it, reconnection is a second HTTP GET only.

## Optional real model execution

Copy `.env.example` to `.env`, set `OPENAI_API_KEY`, endpoint, model, temperature, output token limit, and timeout. Explicitly select `--mode real` on the runner CLI. For example, after confirming provider charges and settings: `docker compose --profile benchmark run --rm benchmark --mode real --repetitions 1 --scenario successful-booking --output results`. Missing credentials cause a clear runner failure; real mode is never selected by default. The same settings are passed to all architectures. Consult `docs/BENCHMARK_FAIRNESS.md` and `docs/BENCHMARK_PROTOCOL.md` before interpreting any model results. Never commit `.env` or raw results containing provider responses.

## Limitations

Simulated interpretation is not real LLM performance; no real LLM behavior has yet been measured. Execution state and business effects can use separate writes. In-process locking does not coordinate replicas. The unauthorized scenario tests a disallowed process request rather than arbitrary adversarial tool invocation. The domain is synthetic demo booking. Prompts and orchestration semantics can differ among the three architectures. The benchmark reports descriptive statistics, and process startup contributes to wall latency. Container operation and cross-platform behavior require verification on a machine with Docker.
