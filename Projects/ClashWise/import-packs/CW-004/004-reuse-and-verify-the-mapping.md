## Apply a verified mapping to related models

@action: With the verified mechanical source selected, choose Apply to Other Models, select only the related mechanical exports, and apply.

Once one source is correct, choose Apply to Other Models. Select only models that share the same property structure. In this example, the other mechanical exports came from the same authoring workflow, so the mapping is a sensible starting point.

Do not apply a Revit mapping indiscriminately to IFC, DWG, or another consultant's export. The command copies settings; it cannot prove the target properties carry the same meaning.

After applying, select one target and run Sample Elements. Confirm the warning marker cleared and the preview still returns names, IDs, categories, levels, types, and source values in the correct columns.

When a level is selected, Apply to Other Models works on sibling levels instead. Copying a level that has no overrides can also reset selected siblings to inherit the source mapping.

## Recognise an incomplete configuration

@visual: Switch briefly to the prepared incomplete snapshot, showing empty Layer or Level and Source values, then restore the verified mapping.

Here is the failure state. The dropdowns may look nearly complete, but the Level and Source preview columns are empty. If we continue, those gaps will weaken location filters, naming context, source separation, class extraction, matrix matching, and reporting.

Fix the mapping where the data originates, then sample again. If Title Update later reports Required Property Mappings Missing, return here and clear every warning icon. If a run reports that mapped properties returned no values, cancel, inspect the affected sample, and correct the property pair.

Property Mapping is now ready for Riverside. The configuration is stored for this Windows user and reused across sessions, but it should be reviewed when models are re-exported, new files are appended, or the Navisworks display language changes.

@visual: End on Sets and Clashes, Create from ClashWise matrix, and the CW-005 title card.

Next, start with a clean federation and create the Coordination setup and reviewed matrix that will define the first clash tests.
