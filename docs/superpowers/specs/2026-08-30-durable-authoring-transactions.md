# VoiceBench Durable Authoring Transactions

## Scope

VoiceBench provides two coordinated undo layers:

1. The active `RichEditBox` owns immediate character-level undo and redo while a script is dirty.
2. Saved authoring operations become durable project transactions that can be undone and redone after navigation or application restart.

Durable transactions cover canonical script saves, AI proposal acceptance and revision restoration, chapter and segment creation, import, rename, duplicate, reorder, include/exclude, archive, split, and delete. They do not reverse provider renders, take approvals, mastering, exports, credentials, or uncontrolled external-file edits.

## Transaction boundary and storage

Each transaction captures the project manifest plus the complete managed authoring state beneath `01-source/references` and `02-scripts`. Immutable audio and export folders are never copied or removed by authoring undo.

History is stored beneath `.voicebench/authoring-transactions/` in the project root:

- `history.json` contains versioned transaction metadata and the current cursor.
- Each transaction has immutable `before/` and `after/` snapshots.
- `pending.json` points to the pre-operation snapshot while a transaction is executing.

Snapshots contain only relative paths. All restore targets are resolved and verified beneath the project root. History is capped at 100 transactions; committing after undo truncates the redo branch.

## Atomicity and recovery

- Only one authoring transaction may execute, undo, or redo at a time.
- Before an operation, VoiceBench writes the `before` snapshot and durable pending marker.
- After the operation succeeds, it writes the `after` snapshot and atomically advances `history.json`.
- If the action or journal commit fails, VoiceBench restores the `before` snapshot and leaves no committed transaction.
- On startup, a pending operation is treated as interrupted and restored to its `before` state before the project is loaded.
- Undo and redo first verify that the current managed-state hash matches the journal cursor. External divergence blocks restoration with an actionable message instead of overwriting files silently.
- Applying any snapshot uses a rollback snapshot. A failed restore returns the project to the state present when undo or redo began.

## Editor behavior

- `Ctrl+Z` and `Ctrl+Y` operate on the active editor while native undo or redo entries exist.
- Loading a segment, saving successfully, reloading an external edit, or applying a durable transaction clears native editor history and establishes a new clean boundary.
- Once the editor has no applicable native entry, Undo and Redo act on durable project history.
- Undoing a saved text change restores the authoring fields, hashes, and canonical Markdown together. Existing render records remain intact; VoiceBench recomputes their stale/approval eligibility against the restored narration.

## UI

The editor command bar receives restrained icon-only Undo, Redo, and History controls. Tooltips and automation names identify whether the next action is an editor change or saved project transaction. Disabled states are explicit.

History opens a compact dialog showing newest-first entries with timestamp, label, applied/undone state, and the current boundary. Undo/redo operations show progress and a confirmation in the existing Workbench status surface. No decorative timeline or additional permanent panel is introduced.

## Acceptance

- Native text undo/redo works without touching disk.
- Saving clears the native buffer and adds one durable transaction.
- Durable undo/redo survives restart.
- New work after undo removes the redo branch.
- Delete, split, import, AI acceptance, and reordering restore manifest and files together.
- Interrupted and failed transactions recover without partial files.
- External divergence blocks undo/redo.
- Render/audio/master/export files are unchanged by authoring undo.
