# VoiceBench Synchronized Word-Follow Playback

## Scope

VoiceBench may align a completed narration take and follow its spoken words in the canonical script during playback. Alignment is a secondary, non-destructive phase: the render and audio are persisted first, and an alignment failure never changes the take's playback, approval, or assembly eligibility.

OpenAI takes use OpenAI `whisper-1`; Gemini takes use Gemini `gemini-3.5-transcribe`. VoiceBench sends the generated audio and exact submitted narration only to the take's original provider. It never falls back to another provider or model.

## Durable model

Project schema 4 adds an immutable submitted-word map and versioned alignment history to every render. Each alignment artifact binds timestamps to the exact render, audio hash, narration hash, submitted-text hash, provider, and model. Retrying appends a new artifact under the segment's generated-audio directory.

Alignment attempts are `Pending`, `Aligned`, `Partial`, `Failed`, `Interrupted`, or `Unsupported`. Reopening a project converts abandoned pending attempts to interrupted without making network requests. The newest successful or partial artifact remains the active alignment.

The production-script parser exposes narration and authoring source ranges for each canonical word. Cue lines have no narration words; escaped literal cue syntax maps to its visible word range. Pronunciation preparation preserves mappings between exact submitted tokens and canonical words, including one-to-many substitutions.

## Alignment behavior

Provider timestamps are sequence-aligned against exact submitted tokens using conservative normalization for case, punctuation, apostrophes, dashes, and common numeric forms. Matches remain monotonic. Ambiguous fuzzy words are omitted. At least 95% canonical coverage is aligned; any lower nonzero coverage is partial; zero usable mappings is failed.

Historical takes can be aligned only when immutable snapshots and hashes allow their submitted map to be reconstructed exactly. Otherwise VoiceBench requires a rerender.

## Playback experience

Take Review shows alignment status, an explicit align/retry action, progress, and a subtle persisted Follow script toggle that defaults on. The audio transport publishes playback position about every 100 ms and immediately after seeking.

Following temporarily makes the editor read-only, selects the active canonical narration word, and scrolls it into view. It preserves and restores the operator's caret, selection, focus, and read-only state on pause, stop, completion, take/segment change, or view unload. Unmatched partial-alignment gaps clear the temporary selection. Playback selection never changes text, formatting, saved state, or undo history.

Before following, VoiceBench compares the take's narration hash with the current parsed narration. Narration edits disable following and require rerendering. Cue-only edits remain followable because source ranges are recalculated from current canonical word ordinals.

## Acceptance

- New renders remain usable when automatic alignment fails.
- Existing trustworthy takes expose Align words and immutable retries.
- Partial alignment reports coverage and skips uncertain gaps.
- Playback, seeking, pausing, and switching takes restore editor state safely.
- Cues are never selected or sent as narration.
- Cue formatting and undo/redo are unaffected by playback ticks.
- Migration and reopening never initiate provider requests.
