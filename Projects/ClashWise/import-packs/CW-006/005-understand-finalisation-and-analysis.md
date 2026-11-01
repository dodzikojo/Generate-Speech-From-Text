## See what finalisation protects

@screen: Show a prepared publish state moving from Awaiting properties to Complete, followed by automatic analysis activity.

Initial ingestion stamps the profile, its current default matrix, and the extraction mapping onto Riverside Cycle zero one immediately.

After clash ingestion, publish finalisation checks that every current clash has its element-property document. If the counts are complete, ClashWise queues matrix analysis automatically and records the publish operation so a retry cannot start duplicate work.

If properties are still arriving, publishing remains successful. The set reports Awaiting properties with expected and actual counts, analysis is held, and finalisation can be retried. This avoids analysing a half-indexed set and disguising missing property data as unmatched rules.

The analysis job remains the existing idempotent workflow. On later publishes it processes new, stale, or remapped clashes while retaining valid prior analysis.

## Verify the new set on the web

@action: Open Riverside Cycle 01 and show its permanent clash numbers, selected test provenance, profile matrix, property state, and analysis progress.

Confirm the set inherited Riverside Coordination Matrix and that analysis began only after properties completed. Open one clash and verify its source information and matched-rule path.

The first publish is now connected to the strategy that created its tests. Future clashes from this federation can receive the same profile default immediately.

@visual: End on one completed matched recommendation beside the CW-007 title card.

Next, read the analysis state, trace recommendations to their rules, and separate automated recommendations from saved coordination decisions.
