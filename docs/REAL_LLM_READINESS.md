# Phase 8A readiness assessment

**Later evidence update:** Real-LLM Phase 8B v1 and v2 feasibility experiments were subsequently run. V1 started 17/21 scheduled executions (15 satisfied); v2 started 19/21 (18 satisfied, one observed violation) and stopped when provider usage accounting was unavailable. See [curated evidence](../publication-evidence/README.md). The Phase 8A statements below describe the earlier readiness checkpoint, not the current execution history. Confirmatory comparison remains pending.

## Phase 8B preparation update

At this Phase 8A checkpoint, the versioned 21-execution natural-language pilot had been prepared in `pilot/phase-8b-v1.json`; operating instructions are in [the pilot runbook](REAL_LLM_PILOT_RUNBOOK.md). A dry run validates real-mode settings and prints a user-priced cost estimate without contacting a provider. The shared transport reserves from a per-experiment request counter before every provider request, including after API restart. Metadata records pilot, variant, original scenario, and prompt hashes. No paid call had been made **at this checkpoint**. The estimated-budget threshold is not a guaranteed spending cap.

Status after Phase 8A.1: **ready for a small, separately authorized real-LLM feasibility pilot in an environment permitting local loopback sockets; not ready for confirmatory scientific comparison**. At that time no paid provider call had been made. All 21 simulated architecture/scenario runs passed locally with restart enabled, and all 54 xUnit tests passed with loopback access.

## Trace and responsibility

All three `Program.cs` files select `OpenAiCompatibleLlmClient` only when their architecture-prefixed `Mode` is `real`. Each real client sends the user conversation, a system instruction, tool schema, model, temperature and output cap through `OpenAiCompatibleTransport`. DirectLlm now exposes both `update_booking` and `create_demo_booking` model tools. The latter is a validated, persisted proposal with no side effect until the API receives confirmation. WorkflowAgent's application controls workflow transitions and chooses the booking tool. ProcessBound additionally gates execution through its process registry and IEC. All three catch provider failures into failed state; there is no simulated fallback. Request counters increment before calls. Tokens remain unavailable when provider usage is absent. Per-request and downstream elapsed time is now aggregated in API metrics.

## Checklist

| Component | Assessment |
| --- | --- |
| Real transport and mode separation | Mocked provider fixtures drive all three services through real-mode clients, including model-generated arguments and confirmation; provider failure has no simulated fallback. Actual provider compatibility remains untested. |
| Seven scenario categories | Present and equivalent per architecture; primary texts are mostly field/value lists. A machine-readable candidate natural-language variant set is separate and not yet selected for the primary experiment. |
| Provider configuration | Model, endpoint, temperature, output cap, timeout exposed; runner checks equality including timeout. Provider-specific parameters, version pinning, and response schema compatibility need validation. |
| Prompt and schema provenance | Runner records each API binary SHA-256 and optional `KOOSHI_SOURCE_REVISION`/`GITHUB_SHA` with full scenario definitions. Set the source revision explicitly for a pilot; source-level prompt/schema hashes are still desirable for publication. |
| Independent outcome | Runner reads booking rows and persisted execution state from each isolated SQLite file, checking confirmation and retry evidence. A denied HTTP create has no execution ID; HTTP 400 and an empty booking table are observed. |
| Latency | Separate launch, readiness, scenario, total, accumulated LLM request and downstream tool times are now exported. |
| Safety gates | Explicit mode, credentials, authorization environment flag, configured request and estimated-budget thresholds. Checks occur between runs; one run can exceed either threshold. This is not a billing limit. |
| CI | Explicit simulated mode and empty credentials; no paid workflow. Local 21-run simulated validation passed. |

## Phase 8A.1 technical diagnosis and security

The earlier xUnit failure was caused by restricted loopback access: VSTest's host log recorded `SocketException (10013)` when connecting to its controller. Running the suite with loopback access passed 54/54 tests. The earlier API readiness failure occurred in that restricted context with the old `dotnet run --no-build` child launch; the runner now launches built DLLs directly, and all three health checks and 21 simulated runs passed with loopback access. An API process launched during this assessment held the standard Release DLL; it was stopped, and an ordinary Release build then passed. An older DirectLlm `dotnet run --no-build` process still holds the standard Debug DLL and was left running because it predates this work. An isolated Debug solution build passed with `-p:OutputPath=bin/Phase8A1Debug/`; the ordinary Debug output path will remain blocked while that process is running.

The runner's new `Microsoft.Data.Sqlite` reference originally brought in `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 through the 2.1.11 SQLitePCLRaw bundle, triggering GHSA-2m69-gcr7-jv3q. Pinning `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 aligns the runner with the APIs and resolves the native library to 2.1.12. `dotnet list ... package --vulnerable --include-transitive` reports no vulnerable packages from the configured sources.

## Confounders and blockers

DirectLlm now has a distinct tool schema and prompt because its model proposes the business operation. The baselines still share equivalent booking information, validation and confirmation requirements. ProcessBound's disallowed-process scenario tests process selection, not adversarial model tool invocation. Provider compatibility, network behavior, model version, prompt context growth, and usage reporting remain unverified. Synthetic fault counters reset on restart. Booking state and effect use separate SQLite writes. The original scenario texts are a weak language-understanding test.

Before confirmatory Phase 8B, select and preregister the natural-language variant set, capture prompt/schema hashes and exact provider/model version, validate actual provider tool-call compatibility in a separately authorized pilot, and establish sample size from pilot variance. The current request and budget thresholds are checked between executions and cannot cap spending within one execution or provider-side retries; pilot authorization must accept that limit or require a provider-side cap. Maintain separate adversarial tool-invocation testing and do not infer superiority from simulated runs.

