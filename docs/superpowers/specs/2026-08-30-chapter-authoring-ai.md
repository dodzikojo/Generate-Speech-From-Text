# VoiceBench chapter authoring, AI writing, and aligned assembly

## Product contract

A VoiceBench project contains ordered chapters, chapters contain ordered render-sized
segments, and segments contain immutable audio takes. Markdown/text in `02-scripts` remains
canonical. The manifest stores stable IDs, order, settings, hashes, status, references, writing
revision history, and render metadata without duplicating canonical script text.

The Workbench is the authoring surface. Its chapter rail is a keyboard-navigable `TreeView`;
the center is the canonical editor; the right-hand tab set contains Writing Assistant and Voice
Director. At narrow widths the chapter tree becomes a picker and the director moves below the
editor. Empty projects offer New chapter, Draft with AI, and Import scripts inside the app.

AI authoring is outline-first. The operator selects an exact provider/model and explicitly
selects local references. VoiceBench sends only the active brief/text and those references,
sets remote response storage off, enables no web tools, and uses structured JSON output.
Proposals never mutate files silently: the operator reviews original/proposal text and word/
duration changes before acceptance. Accepted text creates a restorable local revision and
invalidates takes whose source hash is no longer current.

OpenAI writing models are `gpt-5.6-terra`, `gpt-5.6-luna`, and `gpt-5.6-sol`; Gemini writing
uses `gemini-3.7-flash`. Writing and speech providers are separate contracts, and VoiceBench
never changes provider or model automatically.

The Assembly page persists chapter inclusion/order, reports every approval blocker, and builds
chapter or compilation masters. An approved take qualifies only when its clean-source hash
matches the current canonical source. Each build writes a versioned WAV, readable Markdown and
text transcripts from clean take snapshots, submitted-input audit, CSV/Markdown cue sheets, and
mastering reports. Compilation inserts 500 ms between segments and 1,000 ms between chapters by
default.

## Schema 2

- `ChapterDocument`: stable ID, title, status, compilation inclusion, brief, references, and
  segment order.
- Project: chapter order, writing profile/defaults, registered references, and chapter pause.
- Segment: chapter ID and accepted writing revision history.
- Render: immutable clean-source and exact-submission paths.
- Mastering: explicit Chapter or Compilation scope.

Schema-1 projects migrate atomically. Existing segments are wrapped in an “Existing narration”
chapter without moving scripts or audio. Empty projects remain empty.

## Out of scope

No cloud sync, collaboration, web research, AI CLI, captions, word-level timing, voice cloning,
or DAW timeline is introduced.
