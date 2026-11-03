# Active program index

- Rail row actions, uninterrupted playback and drag reorder, 2026-11-03: implemented on
  `codex/rail-actions` (based on master after PR #15). Playback is no longer stopped when a
  render or any other save refreshes the project: a playing take stays selected and loaded
  (new takes wait in the take list), the editor keeps its text and caret, and segments that
  aren't open render from their saved voice without being loaded into the editor (batch and
  agent `render.start`). Hovering a rail row shows Play/Pause, Approve, Generate and More;
  rules in `Services/RailRowActions.cs`. Approvals asked for during a render queue and run
  after the current take. Dragging rail rows now saves the order (`Services/RailReorder.cs`);
  cross-chapter and nested drops are refused. Installed signed 1.2.0.9; Release build clean,
  520 tests pass. Runtime smoke on a throwaway project with imported tone takes (no provider
  requests): a take kept playing while a new take landed on its segment and while it was
  approved itself (the processed file loads once playback stops), hover play/approve and
  the More menu, drag reorder of segments and chapters, and refused drops. An independent
  review's findings are fixed: the deferred take-bar refresh now also runs after chapter
  playback, alignment status refreshes while a take plays (no stale "Cancel alignment"), the
  shortcut checks the take bar really holds the take's audio, and waiting approvals are
  dropped when another project opens. Residual risk: chapter/segment moves load and save
  the project without the update gate, as Move up/down already did. Not run live:
  the approval queue during a real render, and batch background renders (both cost provider
  requests). Operator guide: [workbench](../workbench.md).

- Batch generation and Workbench extras, 2026-11-03: merged to master in PR #15
  (implemented on `codex/batch-generate`, based on `codex/ui-overhaul`). Generate several segments with
  the project voice from rail ticks (TreeView multi-select mode), "from here to chapter
  end" or a whole chapter, behind a confirmation checklist (approved segments start
  unticked). Sequential tracked `render.start` jobs with stop-after-take/cancel and
  tested stop rules. Also: Voice tab **Render & play**; segment **Export text** (copy or
  save full script or narration only); chapter approved-audio totals in the rail and
  Assembly; the status toast moved to an opaque, auto-dismissing card over the script.
  Operator guide: [workbench](../workbench.md). Installed signed 1.2.0.7; Release build
  clean, 486 tests pass. Runtime smoke covered selection mode, the confirmation dialogs,
  clipboard and file export, toast dismissal and the totals, plus one live operator-
  approved batch (ORCA segment 008: one Gemini take with the project voice, word follow
  97%). An independent review's cancellation and request-count findings are fixed.
  Render & play has not been run live (it costs a provider request).

- Desktop UI overhaul 1.2, 2026-11-03: merged to master in PR #15 (implemented on
  `codex/ui-overhaul`, based on `codex/take-review-playback`). TitleBar project-folder menu; Workbench with the
  script at full height, inspector tabs (Voice/Takes/Write), a one-row take bar, and
  an opt-in timeline dock. Show in folder/open/copy path for takes, scripts, projects,
  masters, auditions and FFmpeg. Projects card grid with filter and remove. Assembly
  built-masters list (and a fix for duplicated chapter cards). Chapter playback of
  approved takes. Operator guide: [workbench](../workbench.md). Installed signed
  1.2.0.5; Release build is clean, all 436 tests pass. An independent review's five
  findings (chapter advance saving drafts, missing-take skip, refresh/selection
  guard, skip-race versions, clipboard errors) are fixed and re-smoked. Runtime smoke covered every
  section at three window sizes, take reveal (Explorer selects the file), chapter
  auto-advance and completion, and MCP `workspace.get`/`panels.set timelineDock`. No
  provider requests. Next: review/PR this branch after the take-review repair lands.

- Take Review playback repair, 2026-11-03: merged to master in PR #15 (implemented on
  `codex/take-review-playback`, based on `codex/audio-timeline`). Installed signed
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
  [final PR #14](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/14) was merged
  to master on 2026-11-03. The operator checklist remains. No smoke
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
