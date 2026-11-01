Connect MCP to the visible WinUI studio: agent edits appear in the live editor and share revision checks, directions, undo/save/conflict handling, exact voice controls and playback. GUI audio/export operations now use the same durable queue and cancellation as MCP. CLI writes route to the desktop that owns the project.

Slice 3 of 4, stacked on `codex/local-mcp-bridge`. Includes singleton lifecycle, Local MCP Settings, explicit take approval, cue-safe auditions and bounded audio previews. GUI AI writing retains the existing shared writing service and operator review; MCP AI uses durable proposals.

Validation: 42 App tests, preview tests, final Release build and 292 passing tests. Installed runtime verified editor changes, conflicts, directions, presets, history, playback/seek and preview. No live paid generation.
