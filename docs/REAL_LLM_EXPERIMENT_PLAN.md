# Real LLM experiment plan (historical proposal)

This proposal preceded the separately authorized Phase 8B v1 and v2 paid feasibility runs. Their incomplete, distinct observations are documented in [curated evidence](../publication-evidence/README.md). No further paid execution is authorized by this document; the 630-run confirmatory study remains a plan.

## Design

Keep the seven primary conditions and 3 × 7 × 30 = 630 planned executions as a planning baseline. The seven JSON inputs are identical across architectures. They are mostly structured field/value text; before behavioral claims, create a versioned natural-language variant set with the same five expected booking fields, confirmation action and fault sequence. Pair each variant across all architectures. Do not quietly fold variants into the 630-run primary count. Use a recorded random seed and rotate architecture order within scenario/repetition blocks. Each run gets a fresh child process and database. Record host, OS, SDK, container digest, git commit, scenario hashes, prompt/tool-schema hashes, UTC times, provider/model/version, endpoint, generation controls, timeout and retry policy. Never record credentials.

The primary questions are whether architecture changes independently verified completion/policy outcomes and scenario execution latency. Secondary outcomes are turns, request/token counts, argument collection, business tool attempts, duplicate effects and recovery. Definitions and analysis are in [measurement validity](MEASUREMENT_VALIDITY.md) and [statistical plan](STATISTICAL_ANALYSIS_PLAN.md).

## Proposed procedure

1. Review the remaining confirmatory-study blockers in [readiness](REAL_LLM_READINESS.md). Freeze code, prompt/schema, scenarios, provider settings, exclusions and analysis before confirmatory collection. The pilot should first verify actual provider support for the selected tool-call format.
2. Obtain explicit authorization for a small paid pilot: one paired execution per architecture for each of the seven scenarios (21 runs), with a separate quoted request allowance and user-supplied token prices. This is for feasibility and variance only, not a confirmatory result. An even smaller three-run success-only connectivity check may precede it if separately authorized.
3. Run with explicit `--mode real`, `KOOSHI_REAL_LLM_AUTHORIZED=yes`, API credentials, `--max-llm-requests`, `--max-estimated-usd`, `--input-usd-per-million`, and `--output-usd-per-million`. Start with `--dry-run`; inspect the planned order and settings. The CLI gates only between runs; provider-side spending can exceed estimates.
4. Audit pilot traces for actual tool arguments, missing usage, process state, database rows, provider errors, and latency boundaries. Revise sample size before full collection without using pilot outcomes to choose a favorable hypothesis.
5. For confirmatory runs, use the frozen paired input set and balanced order. Preserve each run immediately. On SIGINT or a threshold stop, mark metadata incomplete. Resume only under a documented new experiment ID and record the interruption; never silently replace failures.
6. Classify provider failures, API/infrastructure failures, invalid evidence and architecture failures separately. Keep every attempted run in the denominator and give an alternate complete-case view. Exclude only predeclared invalid setup (wrong model/configuration or corrupted scenario input), with an audit log. Stop for protocol drift, sustained provider outage, or safety threshold; do not stop on favorable performance.

## Cost worksheet

For each architecture/scenario, collect pilot mean and upper-range LLM request count and input/output tokens per execution. Multiply token totals by explicit user-supplied USD-per-million input/output prices, then by planned repetitions. Sum the 21 architecture/scenario cells for 630 executions. Produce a base, high-usage and provider-retry scenario; no provider price is assumed here. Without real token observations, a reliable numerical cost estimate is unavailable. A request-count limit and estimated-cost threshold are safeguards, not a guaranteed billing cap.

The unauthorized condition is a disallowed process request. A separate adversarial tool-invocation study would need the same model-visible attack text, model-call trace and attempted tool-name logging in each API, plus a common external observer of committed effects. No privileged ProcessBound-only test hook should be added. Do not claim prompt-injection resistance from the current condition.
