# VoiceBench multitrack audio timeline

Status: Ratified by Dodzi, 2026-11-01. Target: 1.1.0.0.

The approved Expanded mix and Editor dock designs are two views of one shared mix,
selection, transport and transaction history. This specification supersedes the original
v1 exclusion of waveform editing. The approved conversation plan is the delivery contract.

## Product contract

- Add Audio navigation with library, virtualized arrangement, clip inspector and meters;
  replace Workbench playback with a resizable/collapsible dock. Preserve review, pickup,
  approval, generation and alignment. Reserve notice space; persist local layout choices.
- Multiple narration/music/effects tracks; add/name/order/lock/delete; multiselect/move,
  numeric placement, trim/split/duplicate/lift, explicit close-gap, fades/crossfades,
  clip/track gain, mono pan/stereo balance, volume envelopes, mute, monitor-only solo,
  and explicit non-stretch looping. Each gesture/batch is one undoable command; Escape
  cancels. Snapping covers intervals, edges, word boundaries and placed markers.
- Chapter mixes own narration. Compilation follows included active chapters and pauses,
  adding continuous music/effects overlays anchored to project time or chapter start.
  Automatic narration layout resolves untouched placeholders; manual edits never get
  silently rearranged. New segments must be placed or explicitly omitted.
- All source audio/scripts remain immutable. Clips pin exact take, artifact/hash and
  clean snapshot. Mix edits may reorder, omit or duplicate narration independently.
  Reliable aligned boundaries are mandatory for spoken cuts; uncertain speech cannot
  be cut. No overlapping spoken narration; verified silence/tails may overlap.
- Replacement takes require review. Cut transfer requires matching clean narration and
  supported boundaries. Stale clips remain playable but block follow/final assembly.
  Cue-only changes preserve currency. Final transcripts follow retained approved source
  word ranges in audible order. Removed/uncertain cue anchors remain unplaced.
- Imported recordings may be linked to existing segments and registered as takes.
  Approval explicitly confirms reference-transcript accuracy. Alignment provider/model
  is explicit; import/open never uploads audio. Alignment failure preserves playback.
- One transport handles mixes, isolated takes, auditions and exports. Word-follow maps
  through clip offsets, uses a separate read-only playback script surface and preserves
  the live draft/caret/focus/formatting/undo. Playback never saves or formats RichEdit.

## Engineering contract

- Schema 5 adds immutable asset provenance, chapter/compilation timelines and revision
  tokens, tracks/clips/retained words/envelopes/anchors, mix output standard, imported
  render origin, replacement proposals and frozen render plans. Core stays UI/provider
  independent. Use project leases/coordinator, guarded atomic batches, durable dedupe/jobs
  and shared UI/MCP commands. Operational/cache/transport updates are not authoring edits.
- Add VoiceBench.Audio for deterministic 48 kHz stereo float DSP. Pin NAudio.Core and
  NAudio.Wasapi 3.3.0 and Win2D 1.4.0. Use device-rendered WASAPI clock; share the same
  graph between playback/export. Decode/peaks are disk-backed and hash-versioned with
  bounded buffers; no decoding/file work on UI or audio callback. Duck defaults:
  8 dB, 50 ms attack, 300 ms release. Handle output/graphics device loss safely.
- Copy originals to project-relative managed source folders. Migration makes no file
  moves/provider calls/downloads and preserves a manifest backup. Migrate history on
  restore. Hash changes require explicit replacement; interrupted jobs never auto-repeat.
- Mixed defaults: stereo 48 kHz 24-bit WAV, -16 LUFS, -1 dBTP. MP3 and optional aligned
  processed pre-master stems. Flatten chapter graphs/overlays before final normalization.
  Version outputs and publish completion last; retain previous masters on failure.
  Export retained narration, production guidance, clip/cue sheets and provenance report.
- Preserve narration-only mastering and existing take/audition MCP behavior. Add typed
  mix/asset/edit/import/replacement/playback/export APIs/resources, bounded peaks and
  previews, revision conflicts, subscriptions, durable jobs/cancel/retry and CLI mix mode.
  Never expose credentials or silently change provider/model.

## Acceptance and exclusions

Migration/history/concurrency/rollback, source integrity/cache/relink, deterministic DSP,
clock/transport/device recovery, word cuts/partial alignment/overlap/replacements, retained
transcripts, compilation overlays/pauses/stems/loudness, real MCP stdio/recovery must be
tested. Inspect packaged narrow/medium/wide UI at 100-200%, keyboard/high contrast/focus.
Exercise two hours/32 tracks/2,000 clips with bounded memory and recorded measurements.
Release build/all tests/signed x64 MSIX/signature/update/Codex checks/diff check required.
Do not invoke the smoke skill or make paid provider requests for automated tests.

Out: microphone recording, VST/MIDI, pitch/time stretching, spectral/surround/video editing,
captions. Hand Dodzi a focused live-provider and runtime checklist.
