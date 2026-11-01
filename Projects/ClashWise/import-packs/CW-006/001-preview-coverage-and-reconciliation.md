## Start with the reviewed intersection

@visual: Show the reviewed Riverside matrix, the model coverage summary on the Coordination setup, and the plugin reconciliation preview.

The matrix describes the coordination strategy. The scanned model determines which parts of that strategy can be installed today.

ClashWise compares exact class and matching-discipline keys. A matrix endpoint that is absent from the model causes affected rules to be skipped. A selected model class that is absent from the matrix appears as a coverage gap. Neither warning stops us from creating the valid intersection.

This preview is especially useful with a company standard. The library matrix can be wider than one building or phase, so some unused rules are expected.

## Decide how to handle missing classes

@action: On Coordination setups, open the Riverside coverage section and inspect both missing-endpoint lists.

If a real Riverside class is missing from an editable matrix, choose Add missing classes and review the newly generated rules before returning to Navisworks.

If the matrix is read-only, ClashWise clones it before expansion and links the editable copy to this setup. The library original remains unchanged.

Do not add a class simply to eliminate a warning. Confirm that its raw property value is stable and belongs in the rulebook. Temporary or over-detailed model classes should be excluded from the profile inventory instead.

Return to the plugin and choose Refresh reviewed matrix. The reconciliation table now shows what will be created, updated, left unchanged, preserved as a conflict, or considered obsolete.
