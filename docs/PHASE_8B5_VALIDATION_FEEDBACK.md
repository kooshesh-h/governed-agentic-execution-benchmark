# Phase 8B.5: DirectLlm validation feedback (8B-v2)

This configuration follows the incomplete Phase 8B pilot and the 8B.2–8B.4 diagnostics. Feasibility testing found that the application validated a complete `update_booking`, but the next model decision received no explicit validation result. The change supplies that missing signal. It does not revise any historical result or scenario assertion. Do not combine selected successes from earlier experiments with a new pilot.

## Feedback contract

After each `update_booking` execution, the application calls `BookingRules.Validate` on the collected booking. A successful update with no validation errors yields exactly:

> Booking fields were updated successfully. All five required fields are present and validation succeeded. No booking has been proposed or executed. Decide the appropriate next action.

An incomplete or invalid update yields `Booking fields were updated. Validation failed: ` followed by the actual `BookingRules` errors and `No booking has been proposed or executed. Decide the appropriate next action.` A rejected update reports the tool error and the current `BookingRules` errors. This feedback is persisted. When the provider supplied a tool-call ID, the next Chat Completions request contains the original assistant function call and a matching `tool` result. When no ID exists (for example an older fixture), the application uses a labeled system message rather than inventing a provider call or unmatched tool result. The API response is not added as a fabricated assistant message.

`tool_choice: auto`, `parallel_tool_calls: false`, one accepted tool call per response, model-generated arguments, and the existing maximum of two decisions on a complete update remain. Only a valid model-selected `create_demo_booking` establishes readiness. Confirmation remains explicit, and execution is guarded by the confirmed proposal and exactly-once service.

## Provenance and version

- Source base revision: `2eb890183f959369cb8b599f5cf46d1b636565b8`. The current working tree is intentionally uncommitted; the runner records its dirty state and source-tree hash at execution.
- Protocol: [`pilot/phase-8b-v2.json`](../pilot/phase-8b-v2.json), SHA-256 `285AAE33B3BFB0710E1CBCB8BFF0CBD3D3BD1936975ECDB9D2DD053499BEB16B`.
- DirectLlm prompt text SHA-256: `AEF15586F748FF08CA91ED3B009366C82A1342A71DC90492CDF9E4DD2E8DA2BC` (UTF-8 source substring from `You assist` through `Current collected booking: `).
- DirectLlm tool schema source SHA-256: `C00B9857DCCC692953E42BEFC7B52A5957C53FF6E6C3491B7EDA8372DA043C90` (UTF-8 source substring from `tools = new object[]` through the character preceding `tool_choice = `). This hashes the schema definition in source; the runner also records source and binary hashes.
- Natural-language variant SHA-256: `B23E72B5BEF2EE17DD456C427E9AC6C0E5C8E512151B6D545CD2F6649715D290`.
- Scenario SHA-256: cancellation `B5E790D721932B720FE2105E51E4A743F039302F1563BB8807549C4F6FB8B480`; duplicate-replay `01E662E720091E3DE6D0CB4D18C3E987285E18AFD4411B7F12FAE1346C82D7AA`; invalid-fields `4732CF5305DEC6BE77093991E8AD358F936F1150E5DFD48AA83188DA5BA3D412`; reconnection `F00B1A6670389DE3574268580612FFB16D4CCA97A661B886C1EE2DD2775FA168`; successful-booking `09B73D5257B3F9BC86D0AE1D2D9B21B888F565C314BB572A8BB731CC326725A9`; transient-retry `8F97DB966F9872DC3651957EE63C2C4E8BC4E877343798C933E585F98F8A2DE8`; unauthorized `076FDD997804A85E7C55F837D0E66E3206999C8892470CCB3AD659223CDC6F7D`.
- Model configuration for future real runs: one user-selected model and endpoint shared across architectures, temperature 0, maximum completion tokens 512, timeout 30 seconds, no transport retries, `DirectLlm__ParallelToolCalls=false`. Record the exact selected model, endpoint, prices, source state, and binary hashes in each runner experiment metadata.

Relative to 8B-v1, this protocol has the same seed, seven scenarios, three architectures, variant file, request estimates, and business assertions. The DirectLlm context now includes actual application validation feedback. The 8B.2 explicit parallel-call setting is retained.

## Prepared future experiments (not executed)

Use distinct output roots. Each invocation creates its own `experiment-...` subdirectory and captures provenance. Set an exact model, credential source, current prices, and budget before a real run; review a `--dry-run` first. No real request is authorized by this document.

```powershell
$env:KOOSHI_API_CONFIGURATION = 'Release'
$env:LLM_MODEL = Read-Host 'Exact model identifier'
$env:LLM_ENDPOINT = 'https://api.openai.com/v1/chat/completions'
$env:LLM_TEMPERATURE = '0'
$env:LLM_MAX_OUTPUT_TOKENS = '512'
$env:LLM_TIMEOUT_SECONDS = '30'
$env:DirectLlm__ParallelToolCalls = 'false'
$inputPrice = Read-Host 'Verified input USD per million tokens'
$outputPrice = Read-Host 'Verified output USD per million tokens'
$budget = Read-Host 'Estimated budget threshold in USD'
$common = @('--mode','real','--smoke','--seed','42','--pilot-config','pilot/phase-8b-v2.json','--restart','--max-estimated-usd',"$budget",'--input-usd-per-million',"$inputPrice",'--output-usd-per-million',"$outputPrice")
# Diagnostic: one DirectLlm / invalid-fields execution.
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- @common --scenario invalid-fields --architecture DirectLlm --max-llm-requests 4 --output results/diagnostic-8b5-v2 --dry-run
# Complete new pilot: 21 fresh executions, without filtering or reusing prior results.
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- @common --max-llm-requests 84 --output results/pilot-8b-v2 --dry-run
```

The real commands require a credential and `KOOSHI_REAL_LLM_AUTHORIZED=yes`; only remove `--dry-run` after separate authorization. The diagnostic and full pilot must each use a new experiment directory. Real results remain unobserved for this configuration.
