# Chapter authoring, AI writing, and assembly execution

1. Persist the schema-1 to schema-2 migration; add chapter ordering, authoring, multi-file
   import, revisions, references, and atomic rollback tests.
2. Add provider-neutral writing contracts and exact OpenAI Responses / Gemini Interactions
   adapters with mocked success, refusal, authentication, retry, cancellation, malformed-output,
   stateless-storage, selected-reference, and no-fallback tests.
3. Snapshot clean and submitted text for every render; safely backfill provable historical takes;
   block unprovable alignment.
4. Add chapter/compilation mastering scope, chapter pauses, readable/audit transcripts, cue sheets,
   and versioned mastering reports.
5. Replace the flat Workbench rail with the chapter TreeView, add in-app authoring, outline-first
   AI drafting, proposal diffs, revision restore, responsive director layout, and Assembly page.
6. Verify Release build/tests, x64 MSIX packaging, and `git diff --check`. Runtime smoke is handed
   to the user and the smoke skill is not invoked.
