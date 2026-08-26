# VoiceBench execution plan

Status: Active
Date: 2026-08-26

## Global constraints

- Develop on `codex/` branches; each slice must remain reviewable.
- Use .NET 10 and Windows App SDK 1.8.11 (`1.8.260804001`).
- Keep Core provider/UI agnostic and share application services between App and CLI.
- Project paths are relative; scripts are canonical; raw audio is immutable.
- Never log credentials or silently change provider/model/voice.
- Tests use fakes for live providers and external processes.
- Before claiming a slice: Release build, applicable tests, `git diff --check`, and runtime
  inspection. Before delivery: package the MSIX and smoke the packaged app.

## Slice 1 — foundation

Create solution/configuration, repo guidance, design docs, packaged WinUI shell, Core contracts,
test projects, CLI composition root, and preserved legacy/sample material. Confirm `doctor`
reports missing external prerequisites clearly.

Acceptance: solution restores; non-UI projects build; contract tests pass; App XAML resources
parse; the packaged project is recognized once the WinUI workload is installed.

## Slice 2 — project workflow

Implement project creation, atomic manifest repository, schema migration seam, import/splitting,
stable filenames, hashing/stale transitions, editor conflict state, relocatable paths, and
watcher integration. Wire Projects and Workbench views.

Acceptance: fixture relocation works; atomic-save failure retains the old manifest; imports
split deterministically; dirty external edits cannot be overwritten silently; stale output
blocks mastering.

## Slice 3 — providers and rendering

Implement Credential Manager, provider registry, Gemini/OpenAI adapters, validation, exact
render copies, deterministic lexicon substitution, bounded retry, journaled single-worker
queue/recovery, voice auditions, take versioning, cancellation, and mocked HTTP tests.

Acceptance: success/auth/policy/rate/transient/cancellation/malformed cases are deterministic;
there is no implicit fallback; recovered work is Interrupted; every response gets a new path.

## Slice 4 — mastering and package smoke

Implement pinned FFmpeg download/checksum/safe extraction, executable selection, process runner,
two-pass loudness workflow, concatenation, ffprobe verification, transcripts, cue sheets,
reports, CLI master command, Settings and master surfaces, packaging, and visual QA at 100/150%.

Acceptance: fixture master is mono 48 kHz, ordered, within LUFS tolerance, and ≤ −1 dBTP;
failure retains intermediates; MSIX builds and launches.

## Final live smoke

Requires operator-entered Gemini and OpenAI keys. Audition both, choose a preset, render two
segments, approve, master, restart, reopen, and verify playback/history. Record live-provider
results without recording credentials or submitted secret material.
