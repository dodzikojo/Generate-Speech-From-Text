## Treat unmatched clashes as coverage gaps

@action: Open the prepared unmatched clash and show the warning with no matched rule.

This clash completed analysis but has no matching rule. That usually means its extracted class pair is not covered by the current matrix, or its property data did not produce the class you expected.

Do not invent a recommendation simply to remove the warning. Inspect both elements and their captured properties. If the class values are valid, add the missing class or rule to the matrix. If the values are wrong or empty, repair the mapping and published data first.

Unmatched rows are a useful quality report for the rulebook. A concentrated pattern often points to one missing class rather than many unrelated clash problems.

## Recognise stale analysis

@screen: Show the matrix-changed warning on Matched Rule and the set-changed next-action message.

Analysis is a snapshot. If the matrix changes afterwards, the Matched Rule column warns that a rerun is advised. If an Update adds or changes clashes, the set guide reports that the set changed since the last analysis.

Rerunning uses the current matrix and current set while preserving intentionally locked manual edits. Clearing analysis is different: it removes recommendations, matched rules, summaries, dates, and manual analysis edits, and cannot be undone. Use a rerun for ordinary refreshes; reserve Clear analysis for a deliberate clean slate.
