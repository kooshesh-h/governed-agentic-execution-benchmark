# Phase 8B.2: DirectLlm tool-call compatibility

The original incomplete Phase 8B pilot stopped after 17 of 21 planned executions (15 satisfied, two provider/LLM failures, four unattempted). Its artifacts and success criteria are unchanged. Do not combine selective retries with those 15 successes. Any later complete feasibility pilot needs a new experiment directory and full 21-execution plan.

The DirectLlm Chat Completions request exposes `update_booking` and `create_demo_booking`, with `tool_choice: "auto"`. The original request omitted `parallel_tool_calls`; OpenAI documents its default as true. Both calls may therefore appear in one response, while the DirectLlm parser intentionally accepts at most one. The two observed failures match this incompatibility. This phase introduces `DirectLlm:ParallelToolCalls=false` by default after the incomplete pilot. It is sent explicitly on every DirectLlm request and may be overridden with `DirectLlm__ParallelToolCalls=true` for compatibility research. `auto` remains in place: the model chooses the tool and generates its arguments. The application makes a second model request after a complete `update_booking`; it does not synthesize a `create_demo_booking` proposal. Confirmation remains a separate required user action.

Each DirectLlm execution now includes `nativeToolDiagnostics` in its state and runner evidence. Per response, it records call count, recognized tool names in order, finish reason, JSON validity, recognized argument field names, HTTP status, available token usage, and a failure classification. Unknown tool and field names are replaced with fixed labels. Argument values, raw responses, credentials, and headers are excluded. Multiple calls remain a classified parser failure; available usage is added to execution totals. Existing result fields remain unchanged.

The separately labeled paid diagnostic test below is **prepared only**. It requires a pre-existing `OPENAI_API_KEY_FILE` pointing to the secure key file. Run from the solution root only after separate authorization, with verified current prices entered at the prompts. The estimate threshold checks accounting between runs and is not a provider billing cap.

```powershell
$env:LLM_ENDPOINT = 'https://api.openai.com/v1/chat/completions'
$env:LLM_MODEL = 'gpt-4.1-mini'
$env:LLM_TEMPERATURE = '0'
$env:LLM_MAX_OUTPUT_TOKENS = '512'
$env:LLM_TIMEOUT_SECONDS = '30'
$env:DirectLlm__ParallelToolCalls = 'false'
$inputPrice = Read-Host 'Verified input USD per million tokens'
$outputPrice = Read-Host 'Verified output USD per million tokens'
$env:KOOSHI_REAL_LLM_AUTHORIZED = 'yes'
dotnet run --project src/Kooshi.Benchmark.Runner -c Release --no-build -- --mode real --smoke --seed 42 --pilot-config pilot/phase-8b-v1.json --scenario invalid-fields --architecture DirectLlm --restart --output results/diagnostic-8b2-direct-invalid-fields --max-llm-requests 4 --max-estimated-usd 0.01 --input-usd-per-million $inputPrice --output-usd-per-million $outputPrice
```

The runner creates a new `experiment-...` directory containing `metadata.json`, `runs.json`, `failures.json`, `summary.json`, `request-count.txt`, CSV exports, and SQLite evidence. Inspect `evidence.nativeToolDiagnostics` in `runs.json` for the structured diagnostics. This is a diagnostic compatibility test, separate from both the original incomplete pilot and any future new complete pilot.
