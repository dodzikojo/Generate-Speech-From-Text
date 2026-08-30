# VoiceBench narration workstation design

Status: Ratified
Date: 2026-08-26
Owner: Dodzi / IssueLab

## Product intent

VoiceBench replaces the dormant single-file Python generator with a local Windows narration
workstation. The operating loop is script → voice selection (with optional audition) → render →
review/pickup → approval →
master. The product stays generic and folder-first so training-video narration can travel with
the work without coupling this repository to ClashWise.

## Experience direction

The visual direction is **ClashWise Studio**: cinematic restraint adapted to long editing
sessions. The base is `#0E1014`, panels `#16181D`, dividers `#23262E`, and reading text
`#EDEFF3`. Cyan (`#35C7E8`) is the active signal, orange (`#F49B52`) marks pickups, green
(`#58C58B`) marks approval, and red (`#EF6A6A`) is reserved for failures.

Bahnschrift provides compact studio headings; Segoe UI Variable is used for scripts and
controls; Cascadia Mono presents durations, hashes, models, and levels. Surfaces are square-ish
with small radii, dense but breathable 8-pixel spacing, clear outlines, and visible focus.

The signature element is the take rail. It is not a decorative list: it is the persistent
workflow model. Every segment shows Draft, Ready, Rendering, Rendered, Approved, Stale, or
Pickup Required, plus take count and duration. The selected segment feeds the script editor,
voice director, and transport without opening modal screens.

Self-critique: the three-column workbench can become cramped at 100% scaling. The chapter rail,
right-side writing/voice director, and take review can each be collapsed manually; responsive
breakpoints also move the director below the editor and replace the rail with a compact picker.
Color never carries status alone; iconography and labels remain.
Animation is limited to progress feedback and respects reduced-motion settings.

## Project contract

Creating a project produces:

```text
<Project>/
  voicebench.project.json
  01-source/
  02-scripts/
    .trash/
  03-render-copies/
  04-generated-audio/auditions/
  04-generated-audio/segments/
  05-masters/
  06-exports/transcripts/
  06-exports/cue-sheets/
  07-reports/
```

`VoiceBenchProjectDocument` schema version 1 stores project identity, the audio standard,
stable segment order, presets, lexicon, statuses, relative paths, hashes, and render history.
It does not duplicate script text. All manifest saves use a same-directory temporary file,
flush to disk, then replace the destination. Import accepts `.txt` and `.md`, splitting on
level-two headings or an isolated `---`; otherwise it creates one segment.

A watcher detects external script edits. Clean editors reload automatically. Dirty editors
receive Reload, Keep current, and Compare choices. Saving over an unacknowledged external edit
is prohibited. A changed source hash makes approved output stale and blocks mastering unless
the new source is rendered or explicitly re-approved.

## Rendering

`ISpeechProvider` exposes capabilities and one synthesis operation. Initial models are
`gemini-3.1-flash-tts-preview`, `gemini-2.5-flash-preview-tts`,
`gemini-2.5-pro-preview-tts`, and `gpt-4o-mini-tts`. A project default can be selected directly;
A/B auditioning is optional. VoiceBench never silently switches provider, model, or voice.

The canonical transcript remains clean. Render copies contain directions, deterministic
lexicon substitutions, and the exact submitted text. Segments estimated above four minutes
receive a split warning. Providers enforce their own advertised hard limits before HTTP.

The queue runs one job at a time by default and journals state. On startup, abandoned
InProgress jobs become Interrupted and require an explicit retry. HTTP 408, 429, and 5xx are
retried at most three times using Retry-After where supplied plus jitter. Authentication,
validation, policy, and cancellation failures are not retried.

Keys live in Windows Credential Manager. The CLI checks environment variables first. Secrets,
raw authorization headers, and key-bearing URLs are excluded from manifests, arguments,
reports, and logs.

## Audio and exports

The managed toolchain downloads the pinned BtbN FFmpeg 9.0.1 win64 LGPL ZIP, validates its
published SHA-256, rejects unsafe ZIP paths, and installs beneath
`%LOCALAPPDATA%\IssueLab\VoiceBench\Tools`. The operator can select an existing executable.

Every provider response is an immutable `*.raw.wav`. Processing creates versioned WAV files.
Approved segments are converted to mono 48 kHz PCM, analyzed and normalized in two passes to
approximately −16 LUFS integrated with true peak no higher than −1 dBTP, separated by the
configured pause, concatenated in manifest order, then probed again.

Mastering exports a WAV, ordered Markdown and plain-text transcripts, CSV and Markdown cue
sheets, and a JSON/Markdown report. Caption timing and waveform editing are outside v1.

## Accessibility and failure behavior

The app is keyboard-first, has visible focus, supports high contrast and 100–200% scaling,
respects reduced motion, allows cancellation, and presents actionable empty/error states.
Long operations do not block the UI thread. Failures retain intermediates needed for diagnosis
and never destroy an earlier take or master.

## Non-goals

No cross-platform client, cloud sync, accounts, team collaboration, automatic updates, voice
cloning, full DAW timeline, caption timing, direct ClashWise integration, or legacy CLI parity.
