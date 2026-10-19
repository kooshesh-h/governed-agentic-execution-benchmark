# Curated Phase 8B feasibility evidence

This directory contains publication copies of two **separate**, incomplete, one-repetition real-LLM experiments. It does not replace the preserved original directories under ignored local `results/`. No result was edited to change an outcome. No runs from v1 and v2 are pooled.

| Package | Original experiment | Scheduled | Started | Satisfied | Observed non-satisfaction | Unstarted |
| --- | --- | ---: | ---: | ---: | --- | ---: |
| [8B v1](phase-8b-v1/) | `results/pilot-8b-full/experiment-20261018T183218169-6e37646f78b44ea09923ed6e43875420` | 21 | 17 | 15 | 2 provider/LLM failures | 4 |
| [8B v2](phase-8b-v2/) | `results/pilot-8b-v2-full/experiment-20261018T205644493-05f29907638948aca968599b872222ef` | 21 | 19 | 18 | 1 observed `DirectLlm` successful-booking violation | 2 |

For each package:

- `metadata-public.json` preserves the original experiment ID, source revision and provenance, source-tree/configuration/protocol/variant/scenario/prompt/binary hashes, frozen protocol, model settings, seeded order, planned and started counts, request count, and the run IDs with unavailable usage.
- `runs-public.json` preserves each started run's identity, scenario, architecture, outcome, success, final status, usage and cost accounting, business-effect measures, and timings. It omits the free-text `diagnostic`, raw API `evidence`, and execution ID.
- `summary-original.json` is a byte-for-byte copy of the runner's original descriptive aggregate for **started** runs. Its success-rate denominator is attempted runs, not all scheduled runs.
- `manifest.json` records SHA-256 hashes of every original JSON, CSV, request-count, and SQLite file. The original SQLite databases are not published here. It also lists the publication-copy omissions.

**Scheduled-workload completion** is satisfied executions divided by all 21 scheduled executions. A scheduled execution that did not achieve its end-to-end criterion, including one stopped before start, is unsuccessful for this workload metric. Report started counts and observed outcomes beside that rate. An unstarted execution is not an observed architecture-level violation. In v2, `ProcessBound` was 7/7, `WorkflowAgent` 6/7, and `DirectLlm` 5/7 on the scheduled-workload denominator. The last two scheduled runs were `WorkflowAgent` and `DirectLlm` unauthorized cases. One started v2 run lacked provider usage fields, so the runner's between-run accounting gate stopped further execution. The request-count and estimated-cost thresholds were not reached.

The original files may contain provider-derived text and local execution records. The publication copies use an allowlist of fields and were prepared without changing original artifacts. The omitted raw fields and databases limit trace-level independent analysis; access to originals, if ever offered, needs a separate privacy review. The package permits an independent reader to recompute every count reported in the README and compare original-file hashes with the preserved local artifacts.
