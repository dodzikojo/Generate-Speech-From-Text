# Audio timeline execution ledger

Status: Active. Integration branch: codex/audio-timeline. Baseline: e94c4a5.
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
- Implementation verification and outstanding limitations will be recorded here.
