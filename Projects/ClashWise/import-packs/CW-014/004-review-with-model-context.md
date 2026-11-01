## Compare original and proposed titles

@action: Select two materially different rows and read Original title against Proposed title.

The original title is what currently exists in Clash Detective. The proposal is what ClashWise may write later.

Look for four things: both elements are recognisable, the important location is present when the chosen standard expects it, stable IDs have not been mistaken for names, and the wording remains concise enough to scan in a long list.

A strange pattern across several rows often points to Property Mapping or the naming standard. One unusual row may simply need a manual edit.

## Show each clash in Navisworks

@action: Enable Show clash in model when selected and move between the two review rows.

With model preview enabled, selecting a row displays that clash using familiar Clash Detective highlighting. Use this to check whether the proposal describes the geometry you see.

Turn preview off or close the window to restore the previous view. Do not apply the batch yet; CW-016 covers editing, retry, apply, undo, and restore in a single continuous story.

## Read each streamed state correctly

Generation is progressive. A row can still be waiting while earlier clashes already show proposals. Do not interpret an incomplete list as a finished batch or start a second run because the last rows have not appeared yet.

Use the counters and current status to distinguish queued, running, completed, rejected, and failed items. A failed row is local to that clash; it does not automatically invalidate the successful proposals above it.

When the run completes, compare the generated count with the selected clash count. Differences should be explainable as skipped, rejected, cancelled, or failed work before you move to review.

## Judge the first sample before scaling up

Review a small cross-section: one straightforward service-versus-structure clash, one long or ambiguous component name, and one row with weak property data. Check whether the result identifies both sides, location, and useful project context without inventing certainty.

If several titles fail for the same reason, stop and correct the mapping, naming standard, or source properties. Regenerating a larger batch with the same inputs only creates more review work.

The first run is a calibration exercise. Apply nothing until you can explain why the chosen standard succeeds on the prepared examples.
