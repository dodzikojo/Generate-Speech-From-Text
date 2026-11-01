## Undo the complete run

@action: Immediately press Control Z once and verify every title from the applied batch returns to its pre-apply value.

One Control Z reverts the complete apply transaction. It is not one undo per clash when the transaction opens successfully.

Verify both the edited and generated examples returned. Then reapply the accepted batch so the final recording state contains the intended titles.

Undo is most predictable immediately after apply, before unrelated Navisworks actions add more history entries.

## Use purpose-built restore actions

@action: Show Restore this run, then close and reopen Title Update and choose Restore original titles for the selected test.

After apply, Restore this run puts that batch's original titles back. It is useful when the current configuration was wrong.

Across sessions, select the tests in Title Update and choose Restore original titles. ClashWise uses its local title audit to return each recognised clash to the title it had before ClashWise first renamed it, even after several runs.

A restore is itself one undoable transaction. The confirmation reports how many titles can be restored and which clashes have no stored original.

## Recover after a closed or interrupted window

An unfinished review is not the same as an applied batch. Reopen Title Update with the same tests and use the restore or recovery option shown by the current release. Confirm which generated proposals were persisted for review and which rows must be regenerated.

Do not start a fresh full run until you know whether a recoverable session exists. Otherwise, two sets of proposals can make it harder to understand what was actually accepted.

If only a few rows failed, retry those rows. Keep the accepted proposals in place and preserve any manual edits. Reset a row only when you intentionally want to discard its current proposal and return it to the earlier state.

## Apply with a rollback plan

Before Apply, state the selected tests, destination, accepted count, rejected count, and failures. Capture a quick screenshot of the summary for the recording log.

After Apply, open two native clashes and confirm the expected field changed. Then demonstrate Control Z while the transaction is still the latest undoable operation. Reapply only after verifying the original values returned.

Undo protects the most recent apply in the current workflow. Restore original titles is the durable recovery path across later sessions. Neither should be treated as permission to skip the pre-apply review.
