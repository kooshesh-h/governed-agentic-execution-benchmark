# Phase 9.1 publication audit

Audit date: 2026-10-19. Scope: the 103 authored files in the local publication snapshot before this report, spanning C# source and tests, project and solution files, configuration, JSON, YAML, Docker, PowerShell, documentation, protocols, scenarios, and curated evidence. The snapshot manifest lists every inspected release file with a SHA-256 checksum. Generated outputs and private local material were separately inventoried by directory and excluded.

## Cleanup

No AI-development attribution boilerplate was found in release files. Technical uses of `assistant`, AI, LLM, and agent describe protocol behavior and remain. No false authorship statements were added. Historical result metadata and protocol hashes were not rewritten.

Five nonessential C# comment lines were removed: two from `SyntheticBookingService.cs` describing instance attempt counts, one from `DirectLlm.Api/Llm.cs` describing the pilot timing of `parallel_tool_calls`, and two from `DirectLlm.Api/ExecutionService.cs` restating the existing data-collection control flow. The source diff contains comment deletions only. No runtime statements, public APIs, tests, scenarios, frozen pilot protocols, or historical result files were changed for this phase.

Three C# comment lines remain intentionally: one in `ProcessBound.Api/ToolRegistry.cs` records the server-owned tool security boundary; two in `ProcessBound.Api/ProcessRegistry.cs` explain compact provenance and immutable policy snapshot fields. Generated `obj/` comments are excluded with generated files. Comments in `.gitignore`, Docker and research documentation remain because they explain tooling or scientific use.

## Security and privacy

The release snapshot contains no `.git` directory, refs, tags, remote configuration, development branches, `.secrets/`, raw `results/`, local databases, build products, or logs. Pattern scans found no API key, token, bearer credential, or private-key block in snapshot files. The local private credential file was identified by path only and excluded without reading its contents. The synthetic booking domain and isolated research projects show no production Kooshi dependency. These checks do not replace the owner's final rights and disclosure review.

Historical source revision IDs in curated evidence are scientific provenance identifiers. They do not expose Git objects or commit ancestry. Preserve them and the original SHA-256 values. The new public repository will have a different Initial Commit.

## Verification and release gates

Release build: passed, zero warnings and errors. Offline tests: 69 passed, zero failed or skipped, with local process and loopback access. Simulated dry run: 21 planned cases. The full simulated run with restart completed: 21 run records, 21 satisfied criteria, 21 isolated SQLite databases, and `metadata.complete: true`. Its generated output is ignored at `results/phase9-1-verification/`. JSON parsing: all 28 release JSON files valid. Snapshot manifest: all file checksums matched. Frozen `pilot/` and `scenarios/` files and historical evidence were left untouched.

Remaining gates: owner approval of the license and proposed artifact-availability commitment; a new public repository name and URL; Docker/Compose and clean CI verification on a suitable host; and author review of publication evidence and manuscript wording. Do not create a remote repository, commit, tag, push, or change visibility before approval.

## Inspected release files

The complete per-file list, including this report, is `SNAPSHOT_MANIFEST.csv` in the separate publication snapshot. Its checksums cover the exact local release candidate; regenerate it after any approved edits.
