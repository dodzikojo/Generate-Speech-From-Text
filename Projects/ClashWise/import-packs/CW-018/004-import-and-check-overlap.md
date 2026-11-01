## Import a native search set as a preset

@action: Choose From model, select the prepared structural-beams search set, and import it.

From model converts supported native search-set conditions into a reusable preset. Static selection sets are disabled because a fixed list contains no search logic to import.

Review any warning about unsupported conditions. Importing a partial definition without noticing dropped logic can widen future sets.

Name the resulting preset STR - Beams and materialise it as a native search set if needed.

## Check for shared elements

@action: Select MEP Supply Air and STR Beams presets, choose Check overlaps, and inspect the prepared overlap example.

Overlap means the same element belongs to both sets. That may be intentional, but it can create duplicate-feeling or self-referential clash tests.

Use Select shared in model to inspect the overlap. In the prepared example, one over-broad category condition admitted a structural item into the M E P set. Tighten the source or system condition, refresh, and confirm zero unintended overlap.

Scope is a design choice. Generated sets accelerate the work, but they do not replace the coordination team's test standard.

## Build search sets from stable properties

Prefer properties that survive model updates: source file, category, system classification, type, or another authored coordination parameter. Display names that change from one export to the next make a saved search set brittle.

When one property contains several values, use the split or value-generation options only after inspecting the raw data. Extra spaces, punctuation, and inconsistent spelling can create near-duplicate sets that look valid but divide the model unexpectedly.

Name the saved sets for what they contain, not for the button used to generate them. Supply air terminals is useful months later; generated set three is not.

## Verify both sides of every test

@action: Open the prepared clash test and highlight Selection A, Selection B, type, and tolerance.

A search set is only an input. The clash test also records which set is on each side, the test type, and tolerance. Check all four before running.

Use Select in model for each side separately, then Select shared in model to expose unintended overlap. Zero results may mean there are genuinely no intersections, but it can also mean the property rule matched nothing. Compare model selection counts before diagnosing Clash Detective.

Once the test is correct, save it under the agreed project naming convention so publishing and later updates address the same intended scope.
