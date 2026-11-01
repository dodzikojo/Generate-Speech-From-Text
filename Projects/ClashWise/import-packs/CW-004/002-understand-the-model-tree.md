## Open the correct document and source

@action: Save the Riverside federated document, confirm the prepared clash tests contain results, then open General, Property Mapping.

Start with a saved Navisworks document containing appended models and at least one clash test with results. The dedicated Property Mapping window uses those results to discover the source models involved. If it says no properties were found, run Clash Detective first and reopen Property Mapping.

The model-first Coordination setup in CW-zero-zero-five is different: its own scanner can enumerate the federation and select class, discipline, and source properties before tests or clashes exist. Return to this full mapping before publishing so names, identifiers, levels, types, and other coordination fields are captured as well.

The tree on the left lists sources and, where applicable, child levels. A warning icon means the mapping is incomplete. A dot on a level means that level has its own overrides.

Select the Riverside mechanical source. The blue heading confirms exactly what you are configuring. This matters in a federated model: changing a correct mapping on the wrong source can make a later preview look plausible while publishing the wrong values.

## Inheritance and automatic suggestions

@screen: Show an automatically populated source, then expand its child levels and point to inherited fields.

ClashWise tries to detect sensible property pairs for unmapped models. Review those suggestions; do not accept them only because every dropdown is filled.

A source provides the base mapping for its children. Levels inherit that mapping unless one level was exported differently. Select a child level and the editor shows the effective inherited values. Change only a field that genuinely differs. The level receives an override marker, and the reset control beside that field returns it to inheritance.

This pattern keeps maintenance small. Configure the common source once, add a narrow level override only where necessary, and avoid copying six independent settings across every file segment.

All changes save as you make them. There is no final Save button, so use the preview as your explicit verification step before closing the window.
