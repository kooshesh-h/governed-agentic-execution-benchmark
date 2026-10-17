# Kooshi Architecture Benchmark

Independent .NET 10 research prototype accompanying the revised Information Systems paper. It contains no proprietary Kooshi production code. Local generated benchmark results are excluded from version control.

For Docker build, API testing, isolated benchmark execution, and result interpretation, see [Reproducibility](docs/REPRODUCIBILITY.md). The [benchmark protocol](docs/BENCHMARK_PROTOCOL.md), [fairness and limitations](docs/BENCHMARK_FAIRNESS.md), and [draft artifact statement](docs/ARTIFACT_AVAILABILITY.md) describe the scientific scope. The repository is being prepared for public release; it is not presented here as already public. No license has yet been selected.

## Projects

| Project | Purpose |
| --- | --- |
| `src/Kooshi.Benchmark.DirectLlm.Api` | Direct LLM demo booking API (Phase 2) |
| `src/Kooshi.Benchmark.WorkflowAgent.Api` | Workflow agent demo booking API (Phase 3) |
| `src/Kooshi.Benchmark.ProcessBound.Api` | Process bound governed booking API (Phase 4) |
| `src/Kooshi.Benchmark.Shared` | Canonical booking, synthetic downstream, LLM transport, and metrics contracts |
| `src/Kooshi.Benchmark.Runner` | Isolated benchmark runner with JSON/CSV exports |
| `tests/Kooshi.Benchmark.Tests` | xUnit tests |

Each API references Shared. Runner and Tests reference Shared. The APIs have no references to each other.

## Local endpoints

| API | HTTP port |
| --- | ---: |
| DirectLlm | 5101 |
| WorkflowAgent | 5102 |
| ProcessBound | 5103 |

Run an API with `dotnet run --project src/Kooshi.Benchmark.DirectLlm.Api` (substitute the project name for the other architectures). Each exposes `GET /health` and the same reserved execution routes:

- `POST /api/executions`
- `GET /api/executions/{id}`
- `POST /api/executions/{id}/messages`
- `POST /api/executions/{id}/confirm`
- `POST /api/executions/{id}/cancel`
- `POST /api/executions/{id}/retry`

All three APIs implement the shared routes for `demo-booking`. Planned scenarios are listed in `scenarios/README.md`.

## Process bound governed execution

Run `dotnet run --project src/Kooshi.Benchmark.ProcessBound.Api` on port 5103. `processes.json` is the declarative registry; set `ProcessBound__RegistryPath` to load another registry. The `demo-booking` definition specifies the same five required fields and validation as the baseline APIs, version 1.0, a confirmation requirement, the `create_demo_booking` tool allowlist, a synthetic booking side effect, and a three retry limit. The registry resolves a requested `scenarioId`; an unknown ID yields a persisted failed execution and no executable contract.

The intent execution contract (IEC) is constructed from the resolved process and stored with the execution. `P` is process ID and version; `A` is the collected argument map; `Pre` is the required validation/precondition set; `Auth` is the authorization policy; `Conf` requires confirmation; `T` is the tool allowlist; `S` classifies the business effect; `Rec` specifies retry limit and reconciliation policy; `Prov` links the contract to an execution ID and construction time. The service enforces field validity and the tool allowlist before executing. Confirmation stores a SHA-256 fingerprint of the booking arguments; changing those arguments invalidates it. The tool adapter independently checks stage, confirmation fingerprint, validation, and allowlist before committing.

Create with `POST /api/executions` and `{ "scenarioId": "demo-booking", "input": "customerName: Ada Lovelace, email: ada@example.org, product: Analytics, preferredDate: 2026-11-01, timeZone: UTC" }` (replace the date with a future date). A ready response has `status: "ready"`, `fields`, `missingFields`, `invalidFields`, `validationErrors`, `contract`, and an empty `businessEffects` array. Send corrections to `POST /api/executions/{id}/messages` as `{ "message": "email: ada@example.org" }`, then call `POST /api/executions/{id}/confirm`. A successful response has `status: "succeeded"`, one `businessEffects` entry and an audit trail in `events`. `GET /api/executions/{id}` retrieves the persisted state; `cancel` and `retry` use the same route patterns as the other APIs.

Simulated interpretation is the default and deterministically parses comma separated field pairs. Set `ProcessBound__Mode=real`, `ProcessBound__Model`, optional `ProcessBound__Endpoint`, `ProcessBound__Temperature`, `ProcessBound__MaxOutputTokens`, and `ProcessBound__ApiKey` or `OPENAI_API_KEY` for an OpenAI-compatible chat completions service. Keep credentials in environment variables or user secrets. `ProcessBound__DatabasePath` selects SQLite storage; its default is `processbound.db`. `ProcessBound__SimulateDownstreamFailure=true` injects the same synthetic downstream failure as the baseline APIs.

Execution JSON, messages, decisions, attempts and audit events persist in SQLite. An optional `Idempotency-Key` header on create returns the original execution for an identical repeated request; reusing a key with different input returns HTTP 409. Booking commits have a unique execution ID constraint, so repeat confirm calls do not create a second synthetic booking. An interrupted `Executing` state is reconciled against the bookings table when retrieved. Attempts and committed effects are reported separately. The in-process semaphore protects concurrent calls in one API process; multiple API instances sharing a database are not coordinated. State and booking records are not committed in one transaction, and an external downstream service would need its own idempotency and reconciliation guarantees. Unknown downstream outcomes cannot be claimed as exactly once. The current extractor and booking adapter are still booking specific, so adding another process requires a matching interpreter and tool implementation. Registry policy snapshots are persisted but the registry is not yet signed or version pinned across deployment changes.

DirectLlm delegates field extraction to the LLM and validates before its booking adapter. WorkflowAgent adds explicit transition stages. ProcessBound additionally instantiates a registry derived IEC and checks its tool and confirmation policy at the execution boundary. All three already provide validation, confirmation, persistence and retry safeguards. No performance or security comparison is implied by this implementation. Phase 5 consolidation and its remaining limitations are documented in `docs/BENCHMARK_FAIRNESS.md`. Use identical model settings and fault injection when the future runner evaluates architectures; simulated mode is only for deterministic development and tests.

## Workflow agent demo booking

Run with `dotnet run --project src/Kooshi.Benchmark.WorkflowAgent.Api --urls http://localhost:5102`. The API uses the same request format and booking fields as DirectLlm. Its persisted state machine follows `Created → Interpreting → Validating → Collecting` for missing data, or `Validating → AwaitingConfirmation → Executing → Succeeded` for a complete booking. Interpretation or execution errors enter `Failed`; confirmed downstream failures can be retried up to `WorkflowAgent:RetryLimit` (default 3). Uncommitted executions can be cancelled. Invalid actions leave the state unchanged; impossible internal transitions throw.

The workflow controls validation, confirmation and tool execution. The model only extracts user supplied booking details. Simulated mode accepts comma separated field pairs for local development. Real mode uses an OpenAI compatible chat completions endpoint with `WorkflowAgent__Mode=real`, `WorkflowAgent__Model`, optional `WorkflowAgent__Endpoint`, `WorkflowAgent__Temperature`, `WorkflowAgent__MaxOutputTokens`, and `WorkflowAgent__ApiKey` or `OPENAI_API_KEY`. `WorkflowAgent__DatabasePath` selects SQLite storage, and `WorkflowAgent__SimulateDownstreamFailure=true` injects a booking failure. This preserves the same model options and downstream failure switch as DirectLlm. Never use simulated mode to report LLM benchmark performance.

Unlike DirectLlm, the workflow has named persisted stages and an allowlist of transitions. A request saved in `Executing` is reconciled on retrieval: a committed booking becomes `Succeeded`; otherwise it becomes `Failed` for an explicit retry. Booking commits are idempotent by execution ID. Concurrent operations are serialized within one process. Multi instance locking remains a limitation. The booking schema, validation, downstream behavior, and LLM transport are shared.

## Direct LLM demo booking

The canonical booking fields are `customerName`, `email`, `product`, `preferredDate` (`YYYY-MM-DD`, today or later), and `timeZone` (recognized system time zone ID). This schema is defined in Shared. The synthetic booking adapter accepts one booking per execution ID and commits it to SQLite with a distinct booking ID. The response contains `toolAttempts` and `businessEffects` separately; only the latter records committed bookings.

`POST /api/executions` accepts `{ "scenarioId": "demo-booking", "input": "..." }`. The response includes the execution ID and status. `POST /api/executions/{id}/messages` accepts `{ "message": "..." }` for corrections. `GET /api/executions/{id}` retrieves persisted state. `POST /api/executions/{id}/confirm` commits a ready request, `cancel` stops an uncommitted request, and `retry` repeats a failed confirmed booking. These action endpoints take no body. Status values are `collecting`, `ready`, `failed`, `succeeded`, and `cancelled`.

Run locally with `dotnet run --project src/Kooshi.Benchmark.DirectLlm.Api --urls http://localhost:5101`. Simulated mode is the default and parses comma-separated `field: value` pairs deterministically. It is for development and tests only; its behavior must never be reported as LLM benchmark performance. For example, create an execution with `customerName: Ada Lovelace, email: ada@example.org, product: Analytics, preferredDate: 2026-11-01, timeZone: UTC`, then call `confirm` using the returned ID. Use a future date when running this example later.

To use a real OpenAI-compatible chat completions endpoint, set `DirectLlm__Mode=real`, `DirectLlm__Model`, and optionally `DirectLlm__Endpoint`, `DirectLlm__Temperature`, and `DirectLlm__MaxOutputTokens`. Set `OPENAI_API_KEY` or `DirectLlm__ApiKey` via the environment or user secrets. Do not put credentials in appsettings. The client sends a native `update_booking` function definition and reads tool calls and token usage from the response. The API performs booking only after the separate confirm call. `DirectLlm__DatabasePath` selects the SQLite file. `DirectLlm__SimulateDownstreamFailure=true` injects a controlled booking failure for local testing.

The synthetic downstream service and canonical schema are in Shared. The current simulator is deliberately small: it recognizes field pairs and does not imitate model reasoning. A real provider may differ in its supported chat completions options. The API does not currently support distributed multi-instance locking or transactional persistence of both execution state and booking effects.

Build with `dotnet build Kooshi.ArchitectureBenchmark.slnx`.

## Phase 5 configuration and verification

The common downstream fault setting is `SyntheticBooking__Scenario`: `success` (default), `invalid`, `transient`, `permanent`, `timeout`, `delayed`, or `uncertain`. `SyntheticBooking__DelayMilliseconds` controls the delayed scenario (default 100 ms, clamped to 0–60000 ms). The older per-API `*__SimulateDownstreamFailure=true` setting maps to permanent failure. `transient` fails the first attempt for an execution; `uncertain` commits and reports a lost response. Use a separate SQLite database per API instance. For repeatable tests, create a new service instance and database. The booking store enforces one booking per execution ID.

Every API accepts its architecture prefix followed by `Mode`, `Model`, `Endpoint`, `ApiKey`, `Temperature`, `MaxOutputTokens`, `TimeoutSeconds`, and `DatabasePath` (for example, `DirectLlm__TimeoutSeconds=45`). The default mode is simulated. The real mode requires an API key from the prefixed environment variable or `OPENAI_API_KEY`. Do not put secrets in tracked JSON files. The default timeout is 30 seconds, default model is `gpt-4o-mini`, default temperature is 0, and default maximum output is 512 tokens. Use the same settings across architectures for comparative experiments.

Run `dotnet restore Kooshi.ArchitectureBenchmark.slnx`, `dotnet build Kooshi.ArchitectureBenchmark.slnx`, and `dotnet test Kooshi.ArchitectureBenchmark.slnx`. The package audit command is `dotnet list Kooshi.ArchitectureBenchmark.slnx package --vulnerable --include-transitive`. All APIs pin `Microsoft.Data.Sqlite` 10.0.0 and `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12, resolving `SQLitePCLRaw.lib.e_sqlite3` 2.1.12. Phase 5 does not include benchmark execution or Docker support.


## Phase 6 runner

Build first with `dotnet restore Kooshi.ArchitectureBenchmark.slnx` and `dotnet build Kooshi.ArchitectureBenchmark.slnx`. The runner starts each API on a temporary localhost port with a fresh SQLite database. Run `dotnet run --no-build --project src/Kooshi.Benchmark.Runner -- --dry-run` to validate scenario definitions and view the seeded 630-run plan without business effects. For a small simulated smoke test, run `dotnet run --no-build --project src/Kooshi.Benchmark.Runner -- --smoke --output results`. Use `--scenario successful-booking`, `--architecture DirectLlm`, `--repetitions 30`, and `--seed 42` as needed. The default mode is `simulated`; `--mode real` requires explicit selection and provider credentials. Configure equivalent model and inference settings for all three APIs.

Each experiment gets a unique `results/experiment-*` directory containing `metadata.json`, `runs.json`, `runs.csv`, `summary.json`, `summary.csv`, `failures.json`, and isolated SQLite files. The protocol, metric interpretation, and limitations are in [docs/BENCHMARK_PROTOCOL.md](docs/BENCHMARK_PROTOCOL.md). Simulated output is not real LLM performance. The default reconnection test uses a second HTTP request; add `--restart --scenario reconnection` to terminate and relaunch each API against the same SQLite database before continuing. Use `--direct-url`, `--workflow-url`, and `--process-url` to select localhost HTTP origins for runner-managed processes. The unauthorized condition tests a disallowed process request because no arbitrary tool invocation endpoint exists.
