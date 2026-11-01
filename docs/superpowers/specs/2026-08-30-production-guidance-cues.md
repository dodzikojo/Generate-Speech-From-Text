# VoiceBench production guidance cues

## Outcome

VoiceBench canonical Markdown may contain ordered production guidance alongside narration. Guidance remains visible and editable in the same file but is excluded from speech, narration timing, provider limits, clean transcripts, and audio currency.

## Authoring contract

A production cue is a complete source line using `@type: content`. Type names are lowercase kebab-case. `visual`, `action`, `screen`, and `note` receive built-in presentation; all other valid types use neutral custom presentation. `\@type:` escapes a line so it remains narration.

The parser returns the original authoring text, clean narration, ordered cues, and diagnostics. Each cue records its source line and the number of narration words preceding it. Empty cue content, invalid cue names, and cue-like malformed lines are diagnostics. Drafts may be saved with diagnostics, but rendering is blocked until they are resolved and the segment contains narration.

## State and rendering

Schema 3 adds a full-authoring hash while retaining `SourceSha256` as the clean-narration hash. Cue-only edits preserve approved takes. Narration edits stale them normally.

Every new render stores an immutable authoring snapshot, clean narration snapshot, and exact submitted-text snapshot. Only clean narration receives pronunciation substitutions or enters provider requests. Assembly compares approved narration hashes and may therefore use the latest production guidance without rerendering unchanged speech.

## Experience and exports

The Workbench uses a plain-text-backed WinUI `RichEditBox`. Cue lines retain their visible prefixes and receive restrained label, icon, and accent treatments; custom types use neutral styling. Commands insert built-in or custom cues, and word/duration statistics exclude them.

Production-script Markdown and ordered production-cue Markdown/CSV can be exported before approval and are also included with chapter and compilation masters. These exports do not claim inner-segment timestamps.

AI drafting has an explicit, remembered production-cue option. Narration revisions preserve cues; cue revisions preserve narration. The `voicebench-scriptwriter` Codex skill uses the same syntax and validation rules.

## Boundaries

This slice does not add RTF persistence, embedded media, captions, word-level timing, manual timecodes, arbitrary custom colors, or provider-specific directions to canonical scripts.
