# Kooshi Architecture Benchmark

Standalone .NET 10 research prototype accompanying **From Human Intent to Governed Process Execution: A Process-Bound Architecture for Agentic Information Systems**, prepared for *Information Systems*. It compares three architectures on synthetic demo-booking operations. It is separate from the private commercial Kooshi platform and contains no proprietary production implementation. Architectural differences are hypotheses to test; the code alone does not establish that any architecture is superior. No license has yet been selected.

| Architecture | Execution control |
| --- | --- |
| Direct LLM Tool Calling (`DirectLlm`) | The model proposes booking fields through a tool call; API validation and separate confirmation precede the synthetic booking adapter. |
| Workflow-Based Agent (`WorkflowAgent`) | A persisted stage machine controls collection, validation, confirmation, and retry; the model interprets user input. |
| Process-Bound Governed Execution (`ProcessBound`) | A process registry creates a per-execution intent contract; the service and tool adapter enforce its field, confirmation, and tool policies. |

All three share a booking schema and synthetic downstream adapter. Their prompts and orchestration differ; see the [fairness rules](docs/BENCHMARK_FAIRNESS.md).

## Execution Modes

| Feature | Simulated mode | Real LLM mode |
| --- | --- | --- |
| OpenAI API key required | No | Yes, supplied by the researcher |
| Provider charges | None | Possible |
| Docker support | Yes | Yes, with the real-mode Compose override |
| Automated tests | Yes | Yes, with provider calls isolated or mocked |
| Research reproduction | Deterministic infrastructure validation | Provider-dependent experimental reproduction |
| Measures real LLM behavior | No | Yes |

Simulated mode checks scenario loading, business rules, fault injection, persistence, and the evaluation/export pipeline. Its deterministic field parser does **not** measure LLM accuracy, reliability, latency, or cost. **The authors' private API key is not included in this repository. Researchers must provide their own key to run real LLM experiments.**

## Quick Start: no API key required

Prerequisites: Git, Docker Engine/Desktop with Linux containers and Compose v2. These commands work in PowerShell, macOS Terminal, and a Linux shell. Run them from the directory containing `Kooshi.ArchitectureBenchmark.slnx`. Replace the clone URL with the repository URL from your access or publication record; the repository may not yet be public.

```sh
git clone <REPOSITORY-URL>
cd Kooshi.ArchitectureBenchmark
docker compose --profile benchmark build
docker compose up -d direct workflow process
docker compose ps
docker compose --profile benchmark run --rm --no-deps benchmark --mode simulated --dry-run --repetitions 1 --seed 42
docker compose --profile benchmark run --rm --no-deps benchmark --mode simulated --repetitions 1 --seed 42 --restart --output results
```

The API health endpoints are `http://localhost:5101/health`, `:5102/health`, and `:5103/health`; each should report `healthy` and `simulated`. A successful one-repetition command exits 0 and prints its `results/experiment-*` directory. In that directory, `metadata.json` should show `executionMode: simulated`, `complete: true`, and `plannedRuns: 21`; `runs.json` should contain 21 records with `scenarioSuccess: true`. Check `failures.json` for failures. Generated results are Git ignored. Stop manual services with `docker compose down`. The benchmark container starts its own isolated API processes; the manual services are useful for health inspection.

For offline automated tests, install the .NET 10 SDK and run:

```sh
dotnet restore Kooshi.ArchitectureBenchmark.slnx
dotnet build Kooshi.ArchitectureBenchmark.slnx --no-restore -c Release
dotnet test Kooshi.ArchitectureBenchmark.slnx --no-build --no-restore -c Release
```

No `.env`, `.secrets` directory, placeholder key, or provider account is needed. For a local runner after the Release build, set `KOOSHI_API_CONFIGURATION=Release` (PowerShell: `$env:KOOSHI_API_CONFIGURATION='Release'`; Bash: `export KOOSHI_API_CONFIGURATION=Release`), then run `dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- --mode simulated --smoke --seed 42 --restart --output results`.

## Running with Your Own OpenAI API Key

Obtain a key from your chosen OpenAI-compatible provider and verify that its model supports chat completions, tool calls, `tool_choice`, `max_completion_tokens`, and token usage fields. For OpenAI, create and manage keys in the [API platform](https://platform.openai.com/api-keys). Confirm provider access and pricing independently. Record the exact model identifier and input/output prices.

For Docker, create `.secrets/openai_api_key` locally. The file should contain only the key, without quotes. These examples accept input without displaying it:

```powershell
New-Item -ItemType Directory -Force .secrets | Out-Null
$key = Read-Host 'Your API key' -MaskInput
[System.IO.File]::WriteAllText((Join-Path (Get-Location) '.secrets/openai_api_key'), $key)
Remove-Variable key
```

```sh
mkdir -p .secrets
chmod 700 .secrets
read -rs -p 'Your API key: ' key; printf '\n'
printf '%s' "$key" > .secrets/openai_api_key
unset key
chmod 600 .secrets/openai_api_key
```

`docker-compose.real.yml` mounts the file as `/run/secrets/openai_api_key` in the benchmark container and sets `OPENAI_API_KEY_FILE=/run/secrets/openai_api_key`. The runner checks that it exists and contains a value. Child .NET APIs inherit the variable and read the file through `LlmSettings`. The ordinary Compose file has no secret dependency. Select the override only for real mode:

```sh
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark config --quiet
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark build benchmark
```

Compose secrets mount the file; they do **not** encrypt it at rest. `.secrets/` is Git ignored and excluded from Docker build context, so the key is neither committed nor embedded in images. Never pass it through Docker build arguments, tracked settings, command-line flags, logs, or exported results. Protect and rotate your credentials. Outside Docker, the APIs and runner also support `OPENAI_API_KEY` or architecture-specific `DirectLlm__ApiKey`, `WorkflowAgent__ApiKey`, and `ProcessBound__ApiKey` environment variables; `OPENAI_API_KEY_FILE` works there too. Do not put credentials in tracked `appsettings.json`.

### Configure and inspect without paid calls

For the frozen pilot, use `pilot/phase-8b-v1.json`. Set `LLM_MODEL` to the exact provider model. Optional `LLM_ENDPOINT`, `LLM_TEMPERATURE`, `LLM_MAX_OUTPUT_TOKENS`, and `LLM_TIMEOUT_SECONDS` apply equally to all architectures. Compose defaults to OpenAI's chat completions endpoint, `gpt-4o-mini`, temperature 0, 512 output tokens, and 30 seconds; confirm compatibility and deliberately select a model. This example uses PowerShell:

```powershell
$env:LLM_MODEL = Read-Host 'Exact model identifier'
$inputPrice = Read-Host 'Input USD per million tokens'
$outputPrice = Read-Host 'Output USD per million tokens'
$budgetUsd = Read-Host 'Estimated budget threshold in USD'
$common = @('--mode','real','--smoke','--seed','42','--pilot-config','pilot/phase-8b-v1.json','--restart','--output','results/pilot-8b','--max-llm-requests','84','--max-estimated-usd',"$budgetUsd",'--input-usd-per-million',"$inputPrice",'--output-usd-per-million',"$outputPrice")
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark run --rm --no-deps -e LLM_MODEL="$env:LLM_MODEL" benchmark @common --dry-run
```

For Bash, export `LLM_MODEL` so Compose applies it to all three architectures, and set `INPUT_PRICE`, `OUTPUT_PRICE`, and `BUDGET_USD` in your shell, then run:

```sh
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark run --rm --no-deps -e LLM_MODEL="$LLM_MODEL" benchmark --mode real --smoke --seed 42 --pilot-config pilot/phase-8b-v1.json --restart --output results/pilot-8b --max-llm-requests 84 --max-estimated-usd "$BUDGET_USD" --input-usd-per-million "$INPUT_PRICE" --output-usd-per-million "$OUTPUT_PRICE" --dry-run
```

`--dry-run` validates the plan, model, key presence, prices, thresholds, and timeout; it prints expected and high-usage cost estimates and makes no provider requests. Prices are **researcher supplied**, in USD per million tokens; consult current provider pricing. Pilot planning assumes two requests per execution at 750 input and 150 output tokens, with a high-use estimate of four at 2,500 and 512. These are assumptions, not measured use or guaranteed billing caps.

### Explicit paid execution

Real requests require `--mode real`, `KOOSHI_REAL_LLM_AUTHORIZED=yes`, a credential, model, positive `--max-llm-requests` and `--max-estimated-usd`, and nonnegative token prices. Review the dry run, budget, and provider account spending controls. Start with one successful-booking compatibility run; inspect it before deciding whether to run all 21 pilot executions. In PowerShell, with `$common` defined above:

```powershell
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark run --rm --no-deps -e LLM_MODEL="$env:LLM_MODEL" -e KOOSHI_REAL_LLM_AUTHORIZED=yes benchmark @common --scenario successful-booking --architecture DirectLlm
```

For Bash, append `-e KOOSHI_REAL_LLM_AUTHORIZED=yes` before `benchmark` in the preceding dry-run command, remove `--dry-run`, and append `--scenario successful-booking --architecture DirectLlm`. For the full pilot, omit the two filters after separately deciding to proceed. The runner checks request count before every provider request. It checks estimated budget **between** benchmark runs, so one run can pass the threshold. Provider charges may differ from estimates. Press Ctrl+C to cancel. Completed runs are exported immediately, including per-run `estimatedProviderCostUsd` when usage is available. Preserve partial experiment directories and inspect `request-count.txt`, `metadata.json`, `runs.json`, and `summary.json` before another run. Missing usage yields an unavailable cost estimate.

## Scenarios and pilot

Each JSON file in `scenarios/` defines inputs, actions, faults, invariants, and completion criteria. The runner applies it to each architecture and checks API state against persisted SQLite evidence.

| Scenario | Expected observable outcome |
| --- | --- |
| Successful booking | Confirmation commits one synthetic booking. |
| Invalid fields | Invalid email is corrected before confirmation; one booking commits. |
| Cancellation | Cancellation precedes confirmation; no booking commits. |
| Transient retry | First downstream attempt fails; retry succeeds with one booking. |
| Duplicate replay | Repeated confirmation does not create a duplicate booking. |
| Reconnection | Existing execution is retrieved and continued, optionally after API restart. |
| Unauthorized operation | A disallowed process request is rejected without a booking. This does not establish broad adversarial tool-call security. |

The separate [natural-language variants](scenarios/variants/natural-language-v1.json) replace initial and follow-up text in the Phase 8B pilot while retaining original actions, faults, and criteria. The same variant is applied across architectures; see the [variant explanation](scenarios/NATURAL_LANGUAGE_VARIANTS.md).

The frozen [Phase 8B configuration](pilot/phase-8b-v1.json) specifies three architectures, seven scenarios, one repetition, seed 42, and **21 planned executions**. Its dry run prints the seeded order. The runner records source revision (if `KOOSHI_SOURCE_REVISION` or `GITHUB_SHA` is supplied), binary hashes, scenario/pilot/variant hashes, prompt source hashes, model settings, and execution order. The pilot checks provider compatibility and estimates resources. One repetition cannot support confirmatory statistical conclusions. The [pilot runbook](docs/REAL_LLM_PILOT_RUNBOOK.md) gives the local .NET procedure.

The planned confirmatory design is **3 architectures × 7 scenarios × 30 repetitions = 630 executions**. This is a design, not a claim that 630 real LLM runs have occurred. Preview the simulated plan with `dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- --mode simulated --dry-run --repetitions 30 --seed 42` after building. Interpret real results under the [protocol](docs/BENCHMARK_PROTOCOL.md) and [statistical plan](docs/STATISTICAL_ANALYSIS_PLAN.md).

## Results and measurement

Each run creates an isolated `results/.../experiment-*` directory containing `metadata.json` (configuration, hashes, plan, completion state), `runs.json` and `runs.csv` (per-run records), `summary.json` and `summary.csv` (descriptive aggregates), `failures.json`, and one SQLite `.db` per execution. Real experiments also have `request-count.txt`. Results may contain provider responses and should be handled as research data.

Per-run fields include `outcome`, `scenarioSuccess`, final status, committed and duplicate effects, tool attempts, LLM requests, input/output tokens, process startup latency, application readiness latency, scenario execution latency, accumulated LLM request latency, downstream operation latency, and estimated provider cost. DirectLlm exposes a separate `toolProposals` counter; it can be unavailable for other architectures. Missing provider usage yields null token and cost values, not zero. Scenario success uses independent persisted state and booking rows, not only API responses. Startup and readiness are separate from user-facing scenario execution latency. See [measurement validity](docs/MEASUREMENT_VALIDITY.md).

## Reproducibility and CI

The repository exposes source, scenario definitions, architecture configuration, simulated experiments, procedure, scoring, and exports. Real LLM outputs may vary with model updates, nondeterminism, API behavior, network conditions, provider latency, and pricing changes. Record provider/model version, settings, source revision, working-tree state, and experiment metadata. Identical real outputs are not guaranteed.

Ordinary [GitHub Actions CI](.github/workflows/research-benchmark-ci.yml) restores/builds .NET, runs offline xUnit tests, builds Docker images, checks readiness, executes 21 simulated scenarios, verifies artifacts, and uploads results and diagnostics. It provides no API key and must not make paid requests. Inspect a specific workflow run before claiming it succeeded.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `docker` missing | Install/start Docker Engine or Desktop with Linux containers and Compose v2; use the .NET local path if needed. |
| Missing key or secret file | Simulated mode needs neither. For real Docker mode, create `.secrets/openai_api_key` and select both Compose files. Check path and nonempty content without printing the key. |
| Invalid credentials | Verify provider account, key scope, endpoint, and model. Do not paste the key into issue reports. |
| Health checks fail or ports conflict | Inspect `docker compose ps` and `docker compose logs direct workflow process`; free ports 5101–5103 or use the runner alone. |
| SQLite lock | Stop other processes using the database; allow the runner to allocate fresh per-run databases. |
| Provider timeout | Check network/endpoint and `LLM_TIMEOUT_SECONDS`; preserve partial results before retrying. |
| Request or budget threshold | Inspect `request-count.txt` and run exports; start a newly authorized experiment with deliberate limits. |
| Test-host loopback restriction | Run on a host permitting local TCP loopback; the runner starts child APIs on localhost. |
| Windows DLL lock during build | Stop running API/test processes, then rebuild. |

## Documentation index

- [Fairness rules](docs/BENCHMARK_FAIRNESS.md)
- [Experimental protocol](docs/BENCHMARK_PROTOCOL.md)
- [Reproducibility guide](docs/REPRODUCIBILITY.md)
- [Artifact availability statement](docs/ARTIFACT_AVAILABILITY.md)
- [Real LLM readiness](docs/REAL_LLM_READINESS.md)
- [Real LLM experiment plan](docs/REAL_LLM_EXPERIMENT_PLAN.md)
- [Phase 8B pilot runbook](docs/REAL_LLM_PILOT_RUNBOOK.md)
- [Measurement validity](docs/MEASUREMENT_VALIDITY.md)
- [Statistical analysis plan](docs/STATISTICAL_ANALYSIS_PLAN.md)
