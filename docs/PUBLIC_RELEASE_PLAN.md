# Public release plan and provenance

This is a local release candidate. Publication, repository creation, and the license and artifact-availability commitments require owner approval.

## Provenance

The real-LLM v1 experiment records source revision `6af2362ddbe40288712f2e4182fd5f0fb13ff8f0`; v2 records `d0299d8a14417e51284554fa4a1b7658a83a9908`. The curated `publication-evidence/` metadata retains the original source-tree, configuration, protocol, scenario, variant, prompt-source, and binary hashes and the manifest retains hashes of original artifacts. These are historical scientific identifiers, not public Git history. Do not replace them with a publication commit ID or claim that the publication snapshot is the experimental commit. The public snapshot's future Initial Commit will be a different revision.

After the experiments, the publication candidate adds explanatory documentation, curated evidence, and the source-comment deletions documented in this plan. The comment deletions are nonfunctional: no statements, declarations, parameters, scenarios, or protocol files were changed for Phase 9.1. Earlier post-experiment source changes and their rationale are documented in the phase compatibility and validation feedback records. The curated evidence is a disclosure-preserving summary, not the original raw trace archive.

## Snapshot contents

Include `.github/workflows/`, `src/`, `tests/`, `scenarios/`, `pilot/`, `docs/`, `publication-evidence/`, the root README, solution and project files, Docker and Compose files, `.env.example`, `.editorconfig`, `.gitattributes`, `.gitignore`, `.dockerignore`, `Directory.Build.props`, and `setup-openai-api-key.ps1`. Keep the original private repository and its complete history internally.

Exclude `.git/`, development branches and tags, `.secrets/`, `.env` values, `results/`, local databases, generated `bin/` and `obj/`, `.vs/`, `.dotnet/`, `.templateengine/`, logs, and other ignored local outputs. Retain textual source revision identifiers in publication evidence for scientific provenance; they do not carry Git objects or enable cloning the private history.

## Publication procedure after approval

1. Decide the license and approve the artifact-availability statement. Confirm manuscript wording and evidence disclosure with the authors.
2. Review the separate local snapshot and its file manifest. Verify no `.git` directory, Git refs, credentials, raw results, or generated outputs exist. Verify protocol and scenario hashes against the preserved evidence.
3. Run Release build, offline tests, and the 21-case simulated benchmark from the snapshot. Check Docker and Compose on a suitable host and a clean CI run.
4. Create a **new** public repository. Initialize Git inside the reviewed snapshot, add only the allowlisted files, and create one clean `Initial Commit`. Do not push the private repository, mirror it, use a history filter, or transfer its remote.
5. Push only that new commit, make a versioned tag and research release on the new repository, then update the README clone URL and citation to the new public location. Recheck the final public file tree and release archive.

The private development repository remains private and unchanged for internal traceability. The one-commit publication repository gives the public a complete reproducible source snapshot without development commits. The preserved experiment revision and hashes provide the link back to internal records.
