## Choose exact classification properties

@action: Configure the selected source, then repeat for the remaining source types using their real Navisworks properties.

Each source needs a Class property. This is the raw value that should become a matrix endpoint, such as Walls, Structural Framing, Ducts, Pipes, or Mechanical Equipment.

Discipline is optional. Use it when the same class name needs trade context, and leave it empty when the class is already unambiguous. The generated coverage key is the exact combination of class and matching discipline, so inconsistent spellings produce genuinely different endpoints.

The picker stores Navisworks internal category and property names when they are available, while retaining display names for compatibility. Later generated searches prefer those internal names, which makes the setup more resilient to display-language changes.

Confirm the Source property used by cloud analysis. It must return the raw source values that distinguish these appended models. The setup preserves those raw values so future search predicates can be rebuilt exactly rather than guessed from a friendly label.

## Use segmentation only when the preview proves it helps

@screen: Enter a prepared class delimiter and zero-based part, compare the resulting value, then restore the chosen Riverside setting.

Optional class and discipline segmentation can clean composite properties. If a raw value is Basic Wall colon Interior Partition, splitting on the colon and keeping part zero produces Basic Wall.

Do not segment simply to shorten the screen. The discarded section may contain the distinction the matrix needs. Use the preview and keep the transformation stable, because the same mapping is later stamped onto published sets for analysis.

Continue to Inventory and choose Scan model. The scan is cancellable and reports the current source, visited items, and distinct tuple count. It uploads no per-element inventory. It compacts the model into distinct raw source, class, and discipline combinations with counts.

If the scan reaches ten thousand distinct tuples, stop and choose a coarser class property. A matrix built from near-unique element names would be unusable even if the scan were allowed to continue.
