## Choose each property pair deliberately

@action: Review the six rows on the mechanical source, correcting the intentionally missing Layer or Level and Source selections.

Each row has two controls. First choose the Navisworks property category; then choose the property inside it. The second control is unavailable until a category is selected. Both controls are editable, so you can type an exact property name when a translated or project-specific value is not easy to find.

For the mechanical source, map Name to the readable object label, ID to the source element identifier, Category to the classification such as Mechanical Equipment, Layer or Level to Level, Type to the equipment type, and Source to the authoring-file value.

Do not copy these exact category names into every project. A Revit export, an IFC export, and a DWG can store the correct answer in different places. The test is the value returned by real elements, not whether the dropdown resembles this demonstration.

## Prove the values with Sample and Preview

@action: Run Sample Elements, inspect several rows, then use Preview and select two different mechanical elements in the Navisworks view.

Sample Elements is the fastest broad check. It takes representative elements below the selected source or level and fills the preview grid. Scan down each column. A few blank cells may be legitimate; an entire column of blanks or the wrong kind of value means the mapping needs correction.

Now choose Preview and select a real air-handling unit in the model. The grid should show a readable name, a unique ID, Mechanical Equipment, Level zero three, the expected type, and the mechanical source file.

Select a second element from the same source. If both look correct, click a preview row to select that object back in Navisworks and compare it with the native Properties panel. This round trip is the strongest evidence that ClashWise is reading the intended data.

Use Isolate when other models make selection difficult. Selecting the same source again restores the wider view. Clear removes a source mapping, or removes a level's overrides, so use it only when you intend to rebuild that configuration.
