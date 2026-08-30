# Durable Authoring Transactions Execution Plan

1. Add versioned Core transaction contracts and an `IAuthoringTransactionManager` boundary.
2. Implement filesystem snapshots, atomic history writes, cursor management, redo truncation, pruning, divergence checks, rollback, and interrupted-operation recovery in Infrastructure.
3. Add deterministic tests for commit, failure rollback, restart recovery, undo, redo, branching history, path safety, and immutable audio preservation.
4. Compose the manager in `AppServices`; recover before project load and expose a single Workbench transaction wrapper.
5. Route script saves, AI acceptance/restoration, import, create, rename, duplicate, reorder, inclusion, archive, split, and delete through named transactions. Route Assembly chapter inclusion/reordering through the same manager.
6. Add native RichEdit undo/redo controls and durable fallback, clear editor history at clean boundaries, and add a compact persisted-history dialog with progress and confirmation states.
7. Add App contract tests, run Release build and all automated tests, build the MSIX package, and run `git diff --check`. Do not invoke the smoke skill; hand off a runtime checklist to Dodzi.
