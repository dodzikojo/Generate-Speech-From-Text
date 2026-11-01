# Audio timeline execution ledger

Status: Implemented; integration review and operator checklist remain.
Integration branch: codex/audio-timeline. Baseline: e94c4a5.
Ratified contract: ../specs/2026-11-01-audio-timeline.md.

## Global constraints

Preserve user projects/artifacts; no canonical script or raw audio rewriting. No smoke
skill. No paid requests in tests. SDK 10.0.402, Windows App SDK 1.8.11; new packages pinned.
Each slice has a scoped fresh agent/session brief, deterministic tests and reviewable PR
into the integration branch. Merge slices before the final master PR; never merge master
without further authorization. Shared checkout edits must have disjoint ownership.

## Slices and acceptance gates

1. Foundation: schema 5/contracts/migration/guarded commands/history/assets; migration,
   relocation, concurrency and rollback tests.
2. Engine: common DSP/FFmpeg caches/peaks/WASAPI/offline rendering; deterministic sample
   fixtures and real two-track playback/export before UI acceptance.
3. UI: expanded/docked timeline/library/inspector/gestures/keyboard/shared transport;
   packaged responsive inspection and one transaction per gesture.
4. Narration: imported takes/explicit alignment/word cuts/overlap/replacements/cues/
   read-only follow/transcript projection; source and editor-history tests.
5. Assembly/MCP: compilation overlays/mixed exports/stems/CLI/typed capabilities/jobs;
   real stdio end-to-end with local deterministic audio.
6. Release: performance/recovery/accessibility/documentation/signed packaging/install/
   Codex validation. Version 1.1.0.0.

## Verification ledger

- Starting working tree clean. Baseline app installed: 1.0.0.5; prior 292 tests passing.
- Approved mockup lives in the thread visualization directory; it is design-only.
- First integrated Release build: zero warnings/errors; first full suite 330 passing.
  Further narration edge cases and device/codec runtime fixtures are being verified.
- Fresh agent scopes: timeline_foundation (Core/persistence), timeline_engine
  (DSP/assets/WASAPI/export), timeline_desktop (native UI), timeline_review (independent
  transcript/hash/envelope review). Root owns shared Application/MCP/CLI and delivery.
- Independent review found and fixed partial-alignment transcript edge omission,
  split/trim envelope interpolation and processed-artifact registration. Protocol/tool
  argument mismatches were corrected before installed validation.
- Review fixes now preserve unaligned retained edge words, interpolation through split/
  trim, processed/raw artifact pins, historic alignments, and audible transcript order
  across verified silence overlaps. Explicit approval stays attached to each reviewed
  take; ApprovedRenderId selects the default and no longer de-approves older pinned
  takes. Narration changes still invalidate those approvals. Exact fade phase offsets
  preserve every rendered sample across splits/trims inside fades.
- Live clip/track parameter smoothing passed hardware testing: 22 gain/balance/mute
  epochs retained a continuous device clock, 250.4 ms mute response, zero underruns.
  Paused updates retain state/position. Structural/source changes pause and reload.

## Installed validation (2026-11-01)

- Release build: zero warnings/errors. All 389 automated tests pass (Core 84, Audio 16,
  App 56, Infrastructure 174, Application 30, MCP 29).
- Signed x64 MSIX verified with Windows SDK signtool /pa and updated in place from
  1.0.0.5 to 1.1. Builds after initial installation increment the fourth version field
  because Windows rejects changed contents with the same package identity. Current
  installed validation build: 1.1.0.5; app and packaged companion versions match.
- Packaging warning only: missing mspdbcmf.exe suppresses the optional symbol package.
- Actual installed stdio harness passed 24 checks covering closed-app
  launch, project/chapter creation, local SAPI recording import, explicit transcript
  confirmation/approval, music import/preparation/64 peaks, revision conflict, audio
  split, mix undo/redo preserving a dirty editor draft, cue-only save, play/pause/seek/
  loop/stop, eligibility, WAV+MP3+stems/transcript/completion marker, latest cue-only
  guidance, compilation chapter regions/anchored looping/end bounds, reopen persistence.
- Installed large fixture: 2-hour logical duration, 32 tracks, 2,000 clips; three bounded
  edit batches committed in 1,413 ms; playback graph load 122 ms; one-hour seek 70 ms;
  1.2-second playback sample had zero underruns. App working set was 489.5 MiB in the
  first final-build fixture and 647.9 MiB after repeated fixtures/navigation; these are
  process working sets, not measurements of retained cache bytes. Closed-app MCP attach
  took 2,433 ms; subsequent warm attach took 363 ms. Native
  vertical/horizontal navigation inspected. These are sampled cached-media measurements,
  not a two-hour listening/export soak or measurements with 2,000 distinct originals.
- Engine fixture: 5.9 ms graph load; six seeks plus 25.6 seconds of sampled rendering
  and read-ahead prime in 0.680 seconds; retained graph memory 0.82 MiB, allocations
  1.92 MiB, read-ahead ring 0.092 MiB. Real four-second two-track WAV/MP3/stems verified;
  measured -15.95 LUFS at default target, -19.95 LUFS with 44.1 kHz mono 16-bit settings.
- Actual Codex stdio tool call to the installed alias succeeded with the compilation
  revision. Catalog: 164 tools, 10 resources, 4 templates; bridge protocol 2.
- Packaged Audio and Workbench dock inspected at current desktop scaling, including
  narrow 960 px, medium 1,163 px, wide 2,291 px and maximized 3,072 px captures.
  Narrow ruler density and modal panel access corrected during inspection. Keyboard
  dismissal returns visible focus. Full 100–200%/high-contrast operator pass remains.
  Final installed chapter-region navigation, full-duration horizontal scrolling,
  preserved vertical viewport, and pending narration cards (0/8 spoken seconds) verified.
- No smoke skill, live provider requests, or paid generation were invoked. Provider
  alignment/word-follow/replacement paths have deterministic tests; the operator
  checklist covers a real provider run and output-device/sleep recovery.
- One imported-take approval returned a transient filesystem error; subsequent fresh
  runs passed that step. Independent read-only audit found snapshot/recovery activity
  but no retained exception path/HResult/stack or reproducible sharing race. No
  speculative filesystem changes were made; original/imported audio remained intact.
- Runtime fixes also preserve vertical scroll during horizontal pans, suppress old-
  project async errors, release global control subscriptions on unload, expose ordered
  compilation chapter regions/full duration, and show unplaced narration estimates.
  Typed MCP anchor/marker/envelope schemas are dereferenced before field filtering.

## Review and remaining acceptance

Six review slices target codex/audio-timeline: foundation #8, narration integrity #9,
engine/export #10, automation #11, desktop #12, and release validation #13. The final
integration PR targets master after those merges. The reordered source tree was
verified byte-for-byte against the installed/tested source before the review-ledger
update. Master stays unchanged until integration review. Runtime fixtures and installers
remain local under artifacts/ and are excluded from implementation PRs.
The guide and focused operator checklist live at docs/audio-timeline.md.
The saved ORCA operator project was restored after validation, with no unsaved draft or
active job. The isolated fixtures and all original/generated media remain on disk.
