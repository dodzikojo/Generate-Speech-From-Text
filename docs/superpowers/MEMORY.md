# Active program index

- Take Review playback repair, 2026-11-03: implemented on
  `codex/take-review-playback` (based on `codex/audio-timeline`). Installed signed
  1.1.0.7; startup, native **Listen to take**, pause, word-follow and selected-take
  seeking verified with the operator's existing Take 23. Script, directions,
  caret and saved state remained unchanged. Full Release build and all 395 tests
  pass; MSIX signature verified. No smoke skill or paid provider requests.
  The dock defaults to Selected take; new renders select their resulting take and
  reveal Take Review. Playback routing tests cover switching targets and playing
  unapproved/unplaced takes. The 1.1.0.6 startup regression was fixed by wiring the
  playback selector only after XAML initialization. Next: review/integrate this
  repair into the audio timeline branch; master remains unchanged.

- VoiceBench audio timeline, 2026-11-01: implemented on codex/audio-timeline; installed
  1.1.0.5, signed package verified, 389 automated tests pass. Integration PR review
  and focused operator checks remain; no master merge is authorized by this slice.
  See [ratified specification](specs/2026-11-01-audio-timeline.md) and
  [execution ledger](plans/2026-11-01-audio-timeline.md) and
  [operator guide](../audio-timeline.md). All six integration slices are merged;
  [final PR #14](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/14) is open
  against master. Next: review/merge that PR and run the operator checklist. No smoke
  skill or paid provider generation.

- VoiceBench local MCP, 2026-11-01: implementation, installed-runtime verification,
  and master integration complete; operator live-provider checklist remains. See
  [execution ledger](plans/2026-11-01-local-mcp-control.md) and
  [local MCP instructions](../local-mcp.md). The four stacked MCP
  PRs [3](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/3),
  [4](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/4),
  [5](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/5),
  [6](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/6) are merged, and all
  local/origin branch tips have been consolidated into master. Next, exercise one
  real provider render/approval/master through Codex. Installed1.0.0.5, 117 tools,
  292 passing tests, actual Codex diagnostics verified. No smoke skill.
