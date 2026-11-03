# Audio timeline

VoiceBench 1.1 adds an Audio workspace and a compact Workbench timeline dock. Both
use the same chapter arrangement and playback engine. The library contains imported
media and exact narration takes; source files and scripts remain unchanged by mix edits.

## Start a mix

1. Open a project and choose Audio. Select a chapter or the compilation.
2. Import music/effects, or map a recorded narration file to an existing segment.
3. Add a compatible track and place an asset. Select clips to edit timing, gain,
   pan/balance, fades, looping, anchors, and volume envelopes.
4. Approve narration takes. Imported recordings require confirmation that the attached
   transcript matches the recording. Align words using an explicitly selected provider
   before cutting spoken content; importing never sends audio to a provider.
   Alignment requests have a 64 MiB local buffer limit; longer recordings remain
   mixable and can be divided into shorter segment recordings for alignment.
5. Review any missing/stale narration, omissions and replacement proposals, then export.

Chapter mixes are joined in included chapter order with configured pauses. Compilation
music/effects may span those regions; chapter anchors follow chapter reordering. Solo
and monitor volume affect listening only; persisted mute affects export.
The compilation's chapter strip opens the chapter where narration is edited. Pending
narration cards show spoken estimates and remain unplaced until audio is available.

## Safe edits and history

- Drag/trim gestures and atomic edit batches produce one saved undo action. Escape
  cancels a gesture. Audio splitting retains both parts and does not split the script.
- Lift removes a clip reference and leaves a gap. Close gap is explicit and refuses
  to move locked or unsafe material. Original audio remains available in the library.
- Narration cuts use reliable aligned word boundaries. Missing/partial timing can
  leave areas uncuttable; VoiceBench does not invent uncertain timings.
- Replacement takes are reviewed. Original clips stay pinned until acceptance.
- Mix undo/redo moves the same saved history cursor only when its next action is an
  audio edit. For other saved actions use project history; editor undo stays separate.
- Follow script uses a read-only playback surface. Pausing returns to the unchanged
  live draft, caret and undo history. Narration edits disable stale word-follow;
  cue-only edits preserve it.

## MCP and CLI

Read `voicebench_mix_get` before editing and supply `expectedRevision`. Root timeline
geometry is signed 48 kHz frame counts; move/trim/split commands also accept seconds.
`voicebench_mix_edit` accepts typed edits as one atomic batch. Individually named track,
clip, marker, envelope, omission and replacement tools expose the same operations.

Imports, preparation and exports return durable jobs. Inspect with the existing
get/wait/cancel/retry tools. `voicebench_assets_waveform` returns bounded peak pages.
Resources include compilation/individual chapter mixes, assets and transport state.
Bridge protocol 2 rejects older incompatible companions; restart Codex after updating
to refresh its tool catalog. The existing stdio execution alias is unchanged.

```powershell
dotnet run --project src/VoiceBench.Cli -- master C:\Narration\Project --mix --name Final --compilation --mp3 --stems
```

Omit `--mix` to retain narration-only mastering. Mixed output defaults to 48 kHz stereo,
24-bit WAV, -16 LUFS with true peak no higher than -1 dBTP. Optional stems contain
processed track audio before final mastering and share the mix's time offsets. An
intentional silent mix has no measurable LUFS and is reported explicitly.

Final scripts contain only the retained approved narration snapshot ranges in audible
order. Production guidance, clip/cue sheets and reports preserve take/source provenance.
Guidance uses current cue-only edits while the narration remains current; uncertain
anchors stay explicitly unplaced in exports.
Failed or cancelled exports cannot replace an earlier completed version.

## Runtime verification

Automated provider tests use mocks. The installed test below uses local Windows SAPI
speech and generated tones; no provider requests or paid generation are made. It leaves
the isolated fixture on disk and restores the previously open operator project.

```powershell
.\scripts\Test-AudioTimeline.ps1 -TestProjectRoot C:\Temp\VoiceBench-Audio-Test
```

Use a new empty location and save operator drafts first. Add `-KeepFixtureOpen` for UI
inspection; restore afterward with the same path and `-RestoreOnly`.
Add `-Performance` to populate a cached two-hour, 32-track, 2,000-clip fixture and
measure mix load, a one-hour seek, sampled playback underruns, and app working set.
It does not export or play two hours of audio. `-Trace` prints command names only.

Operator checklist:

- Import a real recording, music and effects; check the expanded and docked layouts.
- Move/split/trim, adjust fades and ducking, cancel a drag, and undo/redo with a dirty
  script draft. Confirm original files and editor formatting remain intact.
- Align narration, inspect uncertain gaps, follow across segments, and review a new take.
- Check source-change blockers and intentional omissions before approving/exporting.
- Export chapter and compilation mixes, MP3, stems and retained scripts; reopen them.
- Restart, reconnect Codex, inspect persistence, and switch output devices.
- Check keyboard-only editing, high contrast, and Windows scaling at 100–200%.

The smoke skill is not used. Recording, plugins, pitch/time stretching, spectral editing,
video and captions are outside this release.
