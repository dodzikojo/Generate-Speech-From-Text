VoiceBench needs one revision-aware command layer before agents can safely edit the live studio. This adds shared authoring/voice/writing commands, durable requests and jobs, project leases, and short atomic manifest updates so provider completion preserves concurrent edits.

Slice 1 of 4. Base is the separately secured voice/director checkpoint on `codex/synchronized-word-follow-playback`. Later slices add transport, desktop wiring and packaging.

Validation: 28 Application tests, render-completion preservation regression, and final integrated Release build with 292 passing tests. Provider tests use mocks. No live paid generation.
