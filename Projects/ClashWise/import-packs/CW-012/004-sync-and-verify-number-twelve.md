## Run one controlled sync

@action: Choose Sync to Navisworks and show progress for the selected Riverside scope.

Read the scope and field choices one final time, then choose Sync to Navisworks. Progress reports how many clashes are being processed and whether groups are being applied.

The completion summary separates Updated from Skipped. A non-zero skipped count is not cosmetic; investigate whether the open document still contains the original GUIDs.

The entire sync is one Navisworks operation. If the result is wrong, press Control Z once to revert the changes from that sync, correct the configuration, and try again.

## Verify in Clash Detective

@action: Open clash number twelve in Clash Detective and inspect Status, Priority where supported, Assigned To where supported, Comments, and its group.

Select the original number-twelve result. Verify its status, native priority on Navisworks 2026 or later, Assigned To on 2025 or later, the sync comment, and any chosen group.

The web title has not replaced the native title, which is the expected current limitation. The copied test also remains unchanged because its recreated clash has a different GUID.

Do not declare sync successful from the completion toast alone. The final check is the intended field on the intended original clash.
