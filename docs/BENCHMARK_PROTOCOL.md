# Benchmark protocol — Phase 6

## Research questions

How do direct LLM tool calling, a workflow agent, and process bound governed execution differ in completion, latency, recovery, duplicate effects, and policy outcomes under equivalent booking requests? Numerical differences are descriptive until an inferential analysis is planned and performed.

## Design

The independent variable is architecture. Seven scripted conditions are stored as JSON in `scenarios/`: successful booking, invalid fields and correction, cancellation, transient downstream failure and retry, repeated confirmation, HTTP reconnection, and a disallowed process request. The default plan has 30 repetitions of each architecture and scenario, or 630 sequential runs. A recorded seed shuffles architecture order within each scenario and repetition. The user fields, confirmation action, fault condition, and timeout are identical within each scenario. The runner launches a fresh API process and SQLite database per run. It sets `SyntheticBooking__Scenario` before process startup; the shared service fails the first attempt of a transient run and succeeds on an eligible retry. No fault response is fabricated by the runner.

The dependent variables are independently evaluated scenario success, API status, elapsed wall time, interaction turns, LLM request and token counts, tool attempts, committed effects, duplicate effects, unauthorized attempts and commits, confirmation violations, retries, and recovery. Missing metrics remain null. The runner retains the complete final API JSON as evidence and writes run level JSON and CSV, summary JSON and CSV, failures JSON, and metadata. Each run has an independent execution ID and isolated database. Experiments use unique directories and do not overwrite old results.

## Success criteria and evidence

Success requires the expected final state, the expected count of persisted `businessEffects`, and matching persisted booking fields for completed bookings. Invalid field recovery also requires an initial `collecting` state with no effect. Transient recovery requires a recorded retry. A missing status or effects array is unverifiable. An HTTP error is an infrastructure failure, not architecture failure. The disallowed process is submitted with the same `scenarioId` to all APIs. DirectLlm and WorkflowAgent reject it with HTTP 400; ProcessBound stores a failed execution. These are different API responses with the same no effect criterion. The APIs do not expose arbitrary business tool invocation over HTTP, so this scenario measures a blocked disallowed process request, not adversarial tool invocation. Repeated confirmation uses the same execution ID. It does not test repeated creation of a fresh execution; ProcessBound alone exposes an idempotency key on creation. The reconnection test is a second HTTP GET on the same ID, not a process restart.

## Metric interpretation

The successful DirectLlm simulated smoke path has two tool attempts: `update_booking` during interpretation and `create_demo_booking` after confirmation. The former updates collected fields; only the latter can commit a booking. WorkflowAgent and ProcessBound may report a different number of attempts because their interpretation boundaries differ. Compare the persisted `toolAttempts` names and outcomes alongside totals. `committedBusinessEffects` counts effect records. A duplicate tool response is not a duplicate committed effect. Token values are null in simulated mode. Some APIs return null for duplicate or policy counters because observation is unavailable; these are not zeros.

## Analysis plan

The summary groups by architecture and scenario. Each group reports total, successful, failed, and unverifiable run counts; success rate uses all attempted runs as denominator. Latency reports mean, median, population standard deviation, and nearest rank 95th percentile. Means of turns, requests, tokens, and attempts use only observed values. Cancellation and retry outcomes are reported within their respective scenarios. This implementation produces descriptive statistics only, with no significance claim or unsupported conclusion.

## Modes and threats to validity

`simulated` validates infrastructure and deterministic parsing; it is not real model performance. `real_llm` requires explicit CLI selection and credentials. The runner checks equivalent model, endpoint, temperature, and output token limit across selected APIs. Provider variability, network conditions, and prompt and tool API differences remain threats. The scenario date is fixed at 2030-11-01; update the definitions before that date passes and archive the exact definitions with a publication experiment. Process startup time is included in wall latency, so the current latency field measures end to end experimental run time rather than pure API execution time. The API's `metrics.durationMilliseconds` is retained in raw evidence. Locally, API processes use `dotnet run --no-build`, so build the solution before executing. In Docker, the benchmark image launches published API DLLs with the same per-run process and database isolation. Results produced after process interruption are marked incomplete in metadata.

The default reconnection condition performs a second HTTP GET. With `--restart`, the runner terminates and relaunches the API process with the same SQLite file and execution ID before continuing; metadata records this mode. `--direct-url`, `--workflow-url`, and `--process-url` set localhost HTTP origins for runner-managed API processes. The benchmark does not currently support connecting to externally managed APIs, independent SQL inspection of booking rows, or statistical confidence intervals. These limits prevent a claim of complete scientific validation. Do not treat a successful simulated smoke run as evidence of architecture superiority.

