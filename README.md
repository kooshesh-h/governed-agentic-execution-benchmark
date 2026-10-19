# Kooshi Architecture Benchmark

This standalone .NET 10 research prototype compares three ways to execute synthetic demo-booking requests. Its research question is: **under the same scenarios and model settings, how do direct model tool calling, workflow control, and process-bound governed execution differ in end-to-end success, policy outcomes, recovery, and resource use?** It is separate from the proprietary production Kooshi platform and contains no production service dependency.

The artifact supports **“From Human Intent to Governed Process Execution: A Process-Bound Architecture for Agentic Information Systems”**, by **Kevin Kooshesh and Tina Sahrifiraad**, submitted to Elsevier's *Information Systems* (manuscript **INFOSYS-D-26-01051**, **undergoing major revision**). The paper has not been accepted or published. No DOI has been assigned.

## Architectures and research contribution

| Architecture | Execution design | Business-action boundary |
| --- | --- | --- |
| `DirectLlm` | The model chooses tool calls and proposes booking fields. | API validation and separate explicit confirmation precede the synthetic booking action. |
| `WorkflowAgent` | A persisted application stage machine controls collection, validation, confirmation, and retry while the model interprets input. | Application stages authorize the action. |
| `ProcessBound` | A process registry creates an intent contract for each execution; the model interprets input within that process. | The service and adapter enforce allowed fields, stage, authorization, confirmation, tool policy, and the confirmed argument fingerprint. |

Process-bound execution makes the business process the authority for committing an effect rather than allowing unrestricted model-directed business actions. Its extra checks are the research intervention, not proof of superiority. The implementations share a booking schema, validation rules, and synthetic downstream service but retain separate orchestration and persistence. Prompts and tool semantics differ where the designs require them; see [fairness](docs/BENCHMARK_FAIRNESS.md) and [protocol](docs/BENCHMARK_PROTOCOL.md).

## Scenarios and success criteria

The runner applies each [scenario definition](scenarios/README.md) to each architecture and checks API state against persisted SQLite booking rows. A response alone does not establish success.

| Scenario | Required end-to-end outcome |
| --- | --- |
| `successful-booking` | Explicit confirmation commits exactly one matching booking. |
| `invalid-fields` | Invalid email is corrected before confirmation; one valid booking commits. |
| `cancellation` | Cancellation precedes confirmation; zero bookings commit. |
| `transient-retry` | The first downstream attempt fails; an explicit retry succeeds with one booking. |
| `duplicate-replay` | Repeated confirmation does not commit a duplicate. |
| `reconnection` | The existing execution is retrieved and completed; `--restart` also tests process recovery. |
| `unauthorized` | A disallowed process request is rejected without a booking. This is not a general adversarial tool-call test. |

`scenarioSuccess` means a **started** execution met its entire scenario-specific outcome. `metadata.complete` means all scheduled executions were **started and recorded**, regardless of success. For the **scheduled-workload completion metric**, successful completion means the required end-to-end outcome was achieved; every scheduled execution without that outcome counts as unsuccessful, including a runner-level stop. We report scheduled, started, satisfied, observed failures, and unstarted counts separately. An unstarted case is **not** an observed architecture-level violation. The runner's `summary.json` describes started runs only, so its rates have a different denominator.

## Methods and frozen protocols

The frozen [Phase 8B v1](pilot/phase-8b-v1.json) and [v2](pilot/phase-8b-v2.json) feasibility protocols each specify three architectures, seven scenarios, **one repetition**, seed **42**, and **21 scheduled executions**. Both use the same [natural-language variants](scenarios/variants/natural-language-v1.json), actions, faults, and criteria. V2 retains explicit `parallel_tool_calls: false` and supplies application validation feedback to the next `DirectLlm` model decision; see the [v2 change record](docs/PHASE_8B5_VALIDATION_FEEDBACK.md). Historical experiments remain separate. A 30-repetition, 630-execution confirmatory study is planned, not completed.

Real-mode runs use a researcher-selected OpenAI-compatible Chat Completions model and endpoint, temperature 0, a 512-token output cap, and a 30-second timeout under the pilot setup. All architectures must use matching provider settings. Record the exact model/version, prices, host environment, source revision and dirty state, binary and prompt-source hashes, protocol and scenario hashes, and seeded order. Metadata captures available provenance. The pilot estimates two requests per execution at 750 input and 150 output tokens per request; a high-use case assumes four requests at 2,500 input and 512 output tokens. These are planning assumptions, not measured use or billing caps. See [measurement validity](docs/MEASUREMENT_VALIDITY.md) and the [analysis plan](docs/STATISTICAL_ANALYSIS_PLAN.md).

**Simulated mode** uses a deterministic parser to validate scenario loading, business rules, fault injection, persistence, recovery, and exports. It does not measure LLM understanding, provider latency, or real cost. **Real mode** contacts a provider and can incur charges. Model updates, nondeterminism, API behavior, and network conditions mean identical real outputs are not guaranteed.

## Observed feasibility results

The counts below come from preserved, separate experiments. [Curated publication evidence](publication-evidence/README.md) contains per-run outcomes and usage, provenance, protocol hashes, and SHA-256 links to the original files. These one-repetition pilots support feasibility observations only; they do not support statistical significance or general architectural superiority.

| Protocol | Scheduled | Started | Satisfied | Observed non-satisfaction | Unstarted | Scheduled-workload completion |
| --- | ---: | ---: | ---: | --- | ---: | ---: |
| Phase 8B v1 | 21 | 17 | 15 | 2 provider/LLM failures | 4 | 15/21 (71.4%) |
| Phase 8B v2 | 21 | 19 | 18 | 1 `DirectLlm` successful-booking violation | 2 | 18/21 (85.7%) |

For v2, `ProcessBound` satisfied **7/7 scheduled (100%)**, `WorkflowAgent` **6/7 (85.7%)**, and `DirectLlm` **5/7 (71.4%)**. The two unstarted executions were the scheduled `WorkflowAgent` and `DirectLlm` unauthorized cases. After the nineteenth run, provider usage for one run was unavailable; the runner's between-run accounting gate stopped the remaining work. The observed violation was the started `DirectLlm` successful-booking case. V1 stopped after 17 starts with two observed provider/LLM failures and four unstarted cases. Do not pool selected runs from different protocols.

## Reproduce the simulated baseline

Prerequisites: Git and either **.NET SDK 10** or **Docker Engine/Desktop with Linux containers and Compose v2**. Obtain the approved publication snapshot and run commands from the directory containing `Kooshi.ArchitectureBenchmark.slnx`. Add the new public clone URL after the separate repository is created.

```sh
dotnet restore Kooshi.ArchitectureBenchmark.slnx
dotnet build Kooshi.ArchitectureBenchmark.slnx --no-restore -c Release
dotnet test Kooshi.ArchitectureBenchmark.slnx --no-build --no-restore -c Release
```

For a local simulated run, set `KOOSHI_API_CONFIGURATION=Release` (`$env:KOOSHI_API_CONFIGURATION='Release'` in PowerShell; `export KOOSHI_API_CONFIGURATION=Release` in Bash), then run:

```sh
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- --mode simulated --dry-run --repetitions 1 --seed 42
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- --mode simulated --repetitions 1 --seed 42 --restart --output results
```

The dry run lists 21 seeded executions. A successful complete simulated run produces 21 `scenarioSuccess: true` records, 21 isolated SQLite files, and `metadata.complete: true`. Timestamps, GUIDs, and timings need not match earlier runs. Generated `results/` files are Git ignored.

### Docker and Compose

The Dockerfile builds three manual API targets and an isolated benchmark target. From the repository root:

```sh
docker compose --profile benchmark build
docker compose up -d direct workflow process
docker compose ps
docker compose --profile benchmark run --rm --no-deps benchmark --mode simulated --dry-run --repetitions 1 --seed 42
docker compose --profile benchmark run --rm --no-deps benchmark --mode simulated --repetitions 1 --seed 42 --restart --output results
docker compose down
```

Manual `/health` endpoints are on localhost ports **5101**, **5102**, and **5103**. The benchmark starts its own child APIs and does not use those services. Docker was unavailable on the Phase 9 audit host; verify image builds and Compose execution before release. The [CI workflow](.github/workflows/research-benchmark-ci.yml) is configured to build and test, build images, run 21 simulated cases, and retain artifacts for 30 days; cite it as passing only after checking an actual successful run.

## Real LLM execution with your own credentials

Choose a model and endpoint supporting Chat Completions tools, `tool_choice`, `max_completion_tokens`, and token usage. Supply your own key; none is included in Git. For Docker, create a local `.secrets/openai_api_key` file containing only the key. The [real-mode Compose override](docker-compose.real.yml) mounts it at runtime; the file is not encrypted at rest. `.secrets/`, `.env`, and `results/` are Git ignored, and `.secrets/` is excluded from the Docker build context. Never put keys in tracked settings, command arguments, issue reports, or publication artifacts. Outside Docker, `OPENAI_API_KEY_FILE`, `OPENAI_API_KEY`, or architecture-specific `*__ApiKey` environment variables are supported.

Create the Docker secret without echoing its value. In PowerShell:

```powershell
New-Item -ItemType Directory -Force .secrets | Out-Null
$key = Read-Host 'API key' -MaskInput
[System.IO.File]::WriteAllText((Join-Path (Get-Location) '.secrets/openai_api_key'), $key)
Remove-Variable key
```

In Bash:

```sh
mkdir -p .secrets && chmod 700 .secrets
read -rs -p 'API key: ' key; printf '\n'
printf '%s' "$key" > .secrets/openai_api_key
unset key
chmod 600 .secrets/openai_api_key
```

Select a frozen protocol and record exact model and current prices in USD per million tokens. This PowerShell example for v2 **only validates a plan**; create the secret file first:

```powershell
$env:LLM_MODEL = Read-Host 'Exact model identifier'
$inputPrice = Read-Host 'Input USD per million tokens'
$outputPrice = Read-Host 'Output USD per million tokens'
$budgetUsd = Read-Host 'Estimated budget threshold in USD'
$common = @('--mode','real','--smoke','--seed','42','--pilot-config','pilot/phase-8b-v2.json','--restart','--output','results/pilot-8b-v2','--max-llm-requests','84','--max-estimated-usd',"$budgetUsd",'--input-usd-per-million',"$inputPrice",'--output-usd-per-million',"$outputPrice")
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark config --quiet
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark build benchmark
docker compose -f docker-compose.yml -f docker-compose.real.yml --profile benchmark run --rm --no-deps -e LLM_MODEL="$env:LLM_MODEL" benchmark @common --dry-run
```

The [pilot runbook](docs/REAL_LLM_PILOT_RUNBOOK.md) and [reproducibility guide](docs/REPRODUCIBILITY.md) give local setup. `--dry-run` makes no provider request. Actual execution also requires `KOOSHI_REAL_LLM_AUTHORIZED=yes`, a credential, a model, positive request and estimated-cost thresholds, and token prices. Review provider spending controls before deliberately removing `--dry-run`. The request gate checks before provider requests; cost and complete-usage gates check **between executions**, so neither guarantees a billing cap. Preserve partial experiments; do not silently resume them as complete samples.

## Results, provenance, and limits

Each experiment writes `metadata.json`, `runs.json`/`.csv`, `summary.json`/`.csv`, `failures.json`, and one SQLite `.db` per started run in `results/.../experiment-*`. Real mode also writes `request-count.txt`. Metadata contains configuration, order, source and binary hashes, scenario, variant, protocol, and prompt-source hashes. Per-run data includes outcome, independent success checks, effects, requests, token use, and separate startup, readiness, scenario, LLM, and downstream latencies. Missing provider usage remains null. `summary.json` describes **started** runs; inspect it alongside scheduled and started counts.

Raw local artifacts can contain provider-derived text. The [publication copies](publication-evidence/README.md) omit raw API evidence, diagnostics, and SQLite contents, list redactions, and retain original SHA-256 hashes. They support auditing the reported counts and provenance, but do not supply raw trace-level replication. The synthetic domain, incomplete one-repetition pilots, model variability, differing prompts, and uncertain provider charges limit conclusions. In-process locking does not coordinate replicas. The unauthorized scenario tests a disallowed process request, not general prompt-injection resistance.

**License decision pending owner approval.** No license is applied, so public access alone would not grant general reuse rights. The [proposed five-year availability statement](docs/ARTIFACT_AVAILABILITY.md) is also pending owner approval. The intended maintainer is Kevin Kooshesh and the proposed retention period is at least five years after manuscript publication, if it occurs. Neither release nor that commitment has been approved.

Until a publication record exists, cite: Kevin Kooshesh and Tina Sahrifiraad, “From Human Intent to Governed Process Execution: A Process-Bound Architecture for Agentic Information Systems,” submitted to *Information Systems*, manuscript INFOSYS-D-26-01051, under major revision; and cite this repository with the accessed commit SHA and date. Do not cite a nonexistent DOI or journal issue.
