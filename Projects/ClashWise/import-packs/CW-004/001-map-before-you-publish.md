## See the finished mapping first

@visual: Open Property Mapping on a completed mechanical source and run Sample Elements so all six preview columns contain recognisable values.

This is the result we want before publishing: element names look like names, identifiers are unique, categories describe the object class, levels locate the object, types add useful detail, and source values identify the authoring file.

For this example, the preview includes M L zero three A H U zero two, Level zero three, Mechanical Equipment, and the mechanical source model. We will check more than one element so the mapping works for the model, not only for a convenient selection.

Property Mapping is the translation layer between Navisworks properties and ClashWise. Authoring and export tools store the same concepts under different property categories and names. ClashWise therefore asks you to identify the real property locations instead of assuming one Revit, IFC, or AutoCAD convention fits every file.

## Why six fields matter downstream

@screen: Highlight the six required rows: Name, ID, Category, Layer or Level, Type, and Source.

The current Property Mapping window has six required fields.

Name is the readable element name. ID is the stable source identifier. Category is the broad classification used when extracting and matching matrix classes. Layer or Level locates the element. Type adds the family or specific kind. Source identifies the authoring file the element came from.

Source Property is the current user-facing label for the field that older versions called Discipline Property. Saved configurations still carry over; the label changed to describe the value more accurately.

Incomplete or incorrect mappings show up later as weak titles, empty locations, noisy class lists, unmatched matrix rules, poorer filters, and less useful reports. Mapping is not glamorous, but it is where dependable coordination data begins.
