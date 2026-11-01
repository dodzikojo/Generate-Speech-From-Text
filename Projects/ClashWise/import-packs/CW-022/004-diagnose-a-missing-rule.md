## Inspect both extracted classes

@action: Open the no-matching-rule clash, read Element A and B classes, then search those values in the matrix.

A missing rule does not receive silent defaults. Check whether both extracted classes exist in the matrix and whether the relevant rule is Active.

If a class is absent but valid, Add classes or Sync the matrix. If the class value is wrong, correct Property Mapping or extraction and update the set. If the exact rule is inactive, decide whether it should return to analysis.

## Assign a rule manually only with evidence

@screen: Open Assign Rule and show the suggested current match plus alternative rules without saving the wrong one.

Assign Rule lets a coordinator select a matrix rule for the clash. Choosing a rule applies its current priority, status, and contact to that clash and marks the result as manually modified.

Use this for a defensible exceptional classification, not as a way to hide a matrix coverage problem. The selected rule remains visible as provenance.

## Diagnose the four states in order

First ask whether the clash has a matched rule. If not, compare its current extracted classes with the matrix class list. A new or incorrectly mapped class can leave the clash unmatched until the matrix is maintained and analysis runs again.

Second, check whether the clash carries a manual override. An override is intentional coordination data. Analysis preserves the chosen workflow value rather than replacing it with the rule's recommendation.

Third, compare the stored matched-rule snapshot with the rule as it exists now. If the matrix owner changed priority, status, contact, or tolerance after analysis, the match can be stale even though the rule still exists.

Fourth, check whether the matched rule was removed or became unavailable. That is different from a changed rule and may require reclassification.

## Restore only when the current rule is right

@action: On the prepared stale clash, open provenance, compare old and current values, and choose Restore current rule.

Restoring applies the rule's current coordination values to this clash and clears the intentional divergence represented by the older snapshot or manual value. Read the before-and-after state before confirming.

Then refresh the grid and detail view. Confirm the permanent clash number, matched rule, current values, and audit entry all agree.

If the rule itself is wrong, fix the matrix and reanalyse the appropriate scope instead. Restore is not a shortcut around rule governance.
