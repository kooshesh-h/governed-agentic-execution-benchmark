# Benchmark fairness audit — Phase 5

**Phase 8A.1 correction:** DirectLlm now exposes native `create_demo_booking` to the model as a *proposal*. The model supplies all five arguments; the API validates and records the proposal without a side effect. A separate confirmation executes that exact proposal. `modelToolCalls` and downstream `toolAttempts` are distinct. WorkflowAgent and ProcessBound still use the model for extraction and application-owned booking selection. Earlier statements below describe the Phase-5 baseline and should be read with this correction.

## Inventory before consolidation

The three APIs carried byte-equivalent `Booking` models, required-field validation, and JSON merge logic. Their `SyntheticBookingToolExecutor` classes repeated validation, a Boolean downstream failure switch, and calls to each architecture's SQLite `CommitBookingAsync`. Each SQLite store independently implements the same execution-ID booking uniqueness rule. The OpenAI-compatible clients repeated HTTP request construction, response parsing, usage parsing, and provider error handling, with architecture-prefixed configuration. All APIs already exposed the same six execution routes and health route. WorkflowAgent and ProcessBound shared transition and recovery patterns; ProcessBound additionally applied registry and IEC policy. DirectLlm's retry and cancellation rules differed from the state-machine APIs. ProcessBound alone accepted `Idempotency-Key` on execution creation. Existing metrics fields differed: DirectLlm had no LLM request counter or recovery counter; WorkflowAgent and ProcessBound carried counters that were not exposed as one contract. Tool attempts and committed effects were already separate arrays, but an uncertain failed response could not be represented as a committed effect.

## Architectures and equivalence

| Condition | Equivalent setup | Architectural difference |
| --- | --- | --- |
| Booking fields | Five canonical fields in Shared: customerName, email, product, preferredDate, timeZone | ProcessBound reads the definition from `processes.json` and rejects field drift at startup. |
| Validation | Shared required, email, date, and system time-zone rules; trim on extraction | ProcessBound additionally maps errors to IEC missing and invalid fields. |
| Synthetic downstream | One `ISyntheticBookingService` implementation through API-specific policy adapters and SQLite stores | Authorization and stage checks stay in each API. |
| Model | Same defaults and options: model `gpt-4o-mini`, endpoint, API key, temperature 0, max output tokens 512, timeout 30 seconds, simulated or real mode | Each architecture retains its own prompt and decision logic. Current prompt text is the same, but execution ownership differs. |
| Tool availability | `update_booking` and `create_demo_booking` exist in each API | DirectLlm accepts model-directed field updates. WorkflowAgent limits model output to interpretation while the application controls execution. ProcessBound also checks registered process, IEC tool list, stage, and confirmed argument fingerprint. |
| Confirmation | All require an explicit confirm route before booking | ProcessBound binds confirmation to argument hash. |
| Retry | Explicit retry after confirmed failure | WorkflowAgent uses configured retry limit 3; ProcessBound uses registry retry limit 3 and reauthorizes; DirectLlm currently has no limit. |
| Idempotency | SQLite bookings unique by execution ID in every architecture | ProcessBound alone supports `Idempotency-Key` on create. Repeated create without that key creates separate executions in all APIs. |
| Persistence and recovery | SQLite state and booking tables | WorkflowAgent and ProcessBound reconcile an interrupted Executing state on read. DirectLlm does not have that persisted stage. |
| API | All expose health and create/get/message/confirm/cancel/retry routes | ProcessBound may return a persisted failed state for an unknown process; its create route recognizes the idempotency header. |

## Fault injection

Set `SyntheticBooking__Scenario` to `success`, `invalid`, `transient`, `permanent`, `timeout`, `delayed` (or `delayed-response`), or `uncertain`. `transient` fails the first downstream attempt per execution and succeeds on retry. `uncertain` commits on the first attempt and reports a lost response, then returns a duplicate on retry. `SyntheticBooking__DelayMilliseconds` sets delay duration from 0 to 60000 milliseconds (default 100). The API-specific `*__SimulateDownstreamFailure=true` setting remains as a compatibility alias for `permanent`. The shared service is singleton per API process; tests instantiate a fresh service for isolation. Fault counters reset on service restart. The SQLite unique execution key makes duplicate commits observable without a second effect. A timeout scenario does not commit in the current simulator; it represents an unknown external outcome, so no absence-of-effect claim should be inferred from the response.

## Metrics

Each execution exposes a `metrics` object under a common contract. `toolInvocationAttempts` counts recorded tool attempts, including rejected ones. `committedBusinessEffects` counts recorded effect IDs, not successful responses. `durationMilliseconds` is null until completion; missing token counts remain null. `llmRequestCount` increments before an attempted interpretation, including provider failures. Conversation turns count user messages. Retry and recovery counts derive from recorded events. Unauthorized tool attempts derive from recorded unauthorized attempt outcomes. A duplicate response is not a duplicate committed effect. `duplicateCommittedEffects`, unauthorized committed executions, and confirmation violations are null where the corresponding observation is unavailable. Error categories use failed tool outcomes; failures without a corresponding failed tool outcome are categorized as `unclassified_failure`. Metrics reflect persisted events and effects and must not be used as performance results without an external runner.

## Confounders and limitations

The simulated parser does not model LLM reasoning and should be used only for deterministic tests. Real provider responses, network timing, prompt compatibility, and token usage depend on the selected provider and model. The current APIs store execution state and booking effect in separate SQLite writes. A crash between those writes can leave an effect that is recoverable by WorkflowAgent or ProcessBound but not fully reflected immediately by DirectLlm. The in-process semaphore does not coordinate multiple API replicas. ProcessBound has additional policy work by design; this is part of the architecture under study, not a reason to omit its cost. DirectLlm's model currently selects `update_booking`; the separate confirm endpoint invokes the booking tool after the model-led collection step. The two baselines should not be weakened or fitted to favor ProcessBound. The future benchmark must use identical model, prompts where appropriate, input sets, fault schedule, hardware, and measurement method, while reporting the architectural differences above.

No benchmark results are claimed in Phase 5.

