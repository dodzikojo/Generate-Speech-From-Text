# Synchronized Word-Follow Playback Execution Plan

1. Upgrade the manifest to schema 4 and add parser word ranges, submitted-token maps, alignment contracts, migration, and path validation.
2. Implement conservative sequence alignment plus immutable filesystem attempts and interrupted-attempt recovery.
3. Add same-provider OpenAI and Gemini transcription adapters with bounded retry, cancellation, sanitized errors, and no fallback.
4. Persist submitted maps during render and run alignment only after durable audio completion; expose explicit existing-take alignment in app services.
5. Extend the audio transport with 100 ms position/state events and add Take Review status, progress, align/retry, and persisted Follow script controls.
6. Implement temporary read-only editor selection by current canonical word ordinal, stale narration checks, cue-only remapping, gap clearing, and complete editor-state restoration.
7. Cover schema migration, parsing, substitutions, matching, providers, persistence, rendering, playback rules, and UI contracts. Run Release build, all tests, MSIX packaging, and `git diff --check`. Do not invoke the smoke skill; hand Dodzi the focused runtime checklist.
