# Production guidance cues execution

## Global constraints

- Preserve canonical scripts as UTF-8 Markdown and manifest paths as project-relative paths.
- Never send cue text to speech providers or include it in clean narration exports.
- Cue-only edits must not invalidate approved audio.
- Keep provider fallback prohibited and credentials out of files and logs.
- Do not invoke the smoke skill; provide a manual runtime checklist to Dodzi.

## Slices

1. Add the Core cue parser, schema-3 hashes/migration, validation, and unit tests.
2. Project clean narration through rendering, immutable snapshots, eligibility, mastering, and production exports.
3. Replace the Workbench editor with a plain-text `RichEditBox`, add cue commands/styling, and preserve external-edit and split behavior.
4. Extend AI drafting/revision and update the source `voicebench-scriptwriter` skill plus validator.
5. Run Release build/tests, MSIX packaging, and `git diff --check`; hand off runtime scenarios.
