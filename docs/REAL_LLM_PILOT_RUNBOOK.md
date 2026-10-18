# Phase 8B real LLM feasibility pilot runbook

Status: prepared; **no paid request has been made**. This is a 21-execution feasibility pilot, not a confirmatory comparison. A person must separately authorize one paid execution and then the full pilot. Keep its output under `results/pilot-8b/`, away from future confirmatory results.

## Prerequisites and frozen protocol

Use Windows PowerShell from the solution directory. Install .NET 10, build the solution, and provide an OpenAI-compatible chat-completions endpoint and API key. The selected provider/model must support `tools`, `tool_choice`, `max_completion_tokens`, and usage fields `prompt_tokens` and `completion_tokens`. Actual compatibility remains unverified until the authorized pilot. All three architectures receive the same endpoint, model, temperature, output cap, and timeout; their prompts and orchestration remain distinct. Retry count is zero at the transport layer; the `transient-retry` scenario exercises the application retry action.

The frozen protocol is [`pilot/phase-8b-v1.json`](../pilot/phase-8b-v1.json). It uses seed 42, one repetition, and the seven original scenario IDs in this order: cancellation, duplicate-replay, invalid-fields, reconnection, successful-booking, transient-retry, unauthorized. Within each scenario, the runner randomizes the three architectures with seed 42; `--dry-run` displays the exact order. The exact initial and follow-up texts come from [`scenarios/variants/natural-language-v1.json`](../scenarios/variants/natural-language-v1.json), paired identically across architectures. The seven primary JSON scenario files are untouched. Original action, fault, timeout, invariants, expected booking fields, and completion criteria carry through to the pilot.

The request estimates are assumptions to review: 2 requests/execution at 750 input and 150 output tokens/request; high usage is 4 requests/execution at 2,500 input and 512 output tokens/request. For all 21 executions, that is 42 expected or 84 high-usage requests. These are planning estimates, not measurements. Choose a request limit that can accommodate the intended run, and choose a budget threshold above the displayed estimate only if comfortable with it.

## Configure and validate without paid requests

The following commands run from the solution root. Enter a **user-selected** model and **user-supplied** prices in USD per million tokens; do not substitute prices from this document. The key is read without echo and held only in this PowerShell process. Close the shell when finished. The example endpoint is a default, not a verified provider contract.

```powershell
dotnet build Kooshi.ArchitectureBenchmark.slnx -c Release
$env:KOOSHI_API_CONFIGURATION = 'Release'
$env:LLM_ENDPOINT = 'https://api.openai.com/v1/chat/completions'
$env:LLM_MODEL = Read-Host 'Exact model identifier'
$env:LLM_TEMPERATURE = '0'
$env:LLM_MAX_OUTPUT_TOKENS = '512'
$env:LLM_TIMEOUT_SECONDS = '30'
$env:OPENAI_API_KEY = Read-Host 'API key' -MaskInput
$inputPrice = Read-Host 'Input USD per million tokens'
$outputPrice = Read-Host 'Output USD per million tokens'
$requestLimit = 84
$budgetUsd = Read-Host 'Estimated budget threshold in USD'
$env:KOOSHI_SOURCE_REVISION = (git rev-parse HEAD).Trim()
$common = @('--mode','real','--smoke','--seed','42','--pilot-config','pilot/phase-8b-v1.json','--restart','--output','results/pilot-8b','--max-llm-requests',"$requestLimit",'--max-estimated-usd',"$budgetUsd",'--input-usd-per-million',"$inputPrice",'--output-usd-per-million',"$outputPrice")
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- @common --dry-run
```

The dry run validates scenario loading, variant semantics, selected architecture settings, credential presence, timeout, prices, limits, and the exact order. It prints expected and high-usage cost before execution and makes no provider requests. For the full pilot, calculate expected cost as `42 × (750 × inputPrice + 150 × outputPrice) / 1,000,000`; high usage is `84 × (2500 × inputPrice + 512 × outputPrice) / 1,000,000`. These estimates are **not guaranteed billing limits**. Unknown prompt growth, provider charging rules, failed requests, and absent usage data can change actual charges. Confirm prices and model identity with the chosen provider before authorizing any execution.

## Paid execution commands — separate approvals required

After explicit approval for **one** paid execution, set the authorization variable and select one architecture/scenario. The request limit and budget threshold are user choices. The transport checks the request count before every provider call, including calls after an API restart. The runner also stops between runs when request or token accounting is incomplete or thresholds have been reached. The budget threshold uses observed token counts and is **not** an absolute spending cap.

```powershell
$env:KOOSHI_REAL_LLM_AUTHORIZED = 'yes'
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- @common --scenario successful-booking --architecture DirectLlm
```

After **separate approval** for the full 21-execution pilot, review the dry-run estimate again and execute:

```powershell
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- @common
```

Do not run the 630-execution confirmatory experiment as part of this pilot. The CLI creates a distinct `experiment-...` directory for each command. The real mode requires `--mode real`, `KOOSHI_REAL_LLM_AUTHORIZED=yes`, credential presence, model, positive request and estimated-budget thresholds, prices, and timeout. No API changes to simulated mode are involved; ordinary GitHub Actions CI remains simulated-only.

## Cancellation, results, and recovery

Press **Ctrl+C** in the runner console to cancel between operations. Its cancellation token interrupts the current scenario; the current API process is killed by the runner. Review the newest output directory:

```powershell
$latest = Get-ChildItem 'results/pilot-8b' -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content (Join-Path $latest.FullName 'metadata.json')
Get-Content (Join-Path $latest.FullName 'runs.json')
Get-Content (Join-Path $latest.FullName 'failures.json')
Get-Content (Join-Path $latest.FullName 'summary.json')
Get-Content (Join-Path $latest.FullName 'request-count.txt')
```

Each experiment exports JSON and CSV runs/summary, a failure list, isolated SQLite files, source revision, API binary hashes, pilot and variant hashes, seven primary scenario hashes, prompt source hashes, model settings, and execution order. Record the exact provider/model version and Git worktree state externally at execution time; a commit hash alone cannot identify uncommitted changes. Do not merge pilot observations with confirmatory results. If interrupted or a provider error occurs, preserve the directory and start a new experiment after reviewing authorization and limits; do not silently resume partial data as a full 21-run sample.

## Reading results and limitations

`scenarioSuccess` is checked against API final state and independent SQLite booking rows and persisted execution state. `outcome=unverifiable` indicates missing evidence; `infrastructure_failure` indicates runner/API failure, and `provider_or_llm_failure` indicates an interpretation, provider, or response-schema failure rather than an architecture outcome. Inspect `errorCategory`, `diagnostic`, and API `evidence` for detail. `llmRequests`, `inputTokens`, `outputTokens`, `toolAttempts`, committed and duplicate effects, unauthorized attempts/commits, confirmation violations, scenario latency, accumulated LLM latency, and per-run `estimatedProviderCostUsd` appear in run exports when available. `toolProposals` is available for DirectLlm; WorkflowAgent and ProcessBound currently do not expose a separate proposal counter, so their value is null. `toolAttempts` records downstream attempts and should not be interpreted as proposal count. Missing provider usage yields null token and cost estimates.

One repetition cannot support a statistical superiority claim. Provider compatibility, actual tool-call behavior, pricing, model drift, and network errors remain pilot questions. The request counter prevents dispatch above the selected count in a single runner experiment; a request already dispatched may be billed even if it fails. The estimated budget is checked between runs, so one execution can exceed it. Provider-side account spending controls, if available, should be set separately.

## Approval checklist

- [ ] Select and record exact provider, model identifier/version, and input/output prices.
- [ ] Review the 21-run dry-run order and expected/high-usage estimates.
- [ ] Set request count and estimated-budget threshold deliberately.
- [ ] Approve one paid execution; inspect compatibility, usage, effects, and errors.
- [ ] Separately approve the full 21-execution pilot.
