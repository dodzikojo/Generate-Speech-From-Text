## Verify the spatial context is ready

@screen: Briefly show the Riverside IFC as Ready in Model library, then the clash set's Add model context dialog with Provides rooms and levels checked and Rooms ready.

Plan needs more than ordinary background geometry. Upload an IFC to the Model library, attach it to
the clash set, and mark it as providing rooms and levels. Wait for both geometry and room processing
to become ready.

In the viewer, expand the uploaded model and verify its alignment against known clash geometry.
Nudge, rotate, or fit only as required, then confirm a known clash centre falls inside the expected
room. A loading preview is not a failure, and an older active revision can remain available while a
replacement is updating.

The detailed upload, retry, replacement, and alignment steps live in the Preparing IFC Room Context
guide. Here, the prepared Riverside model must be ready before we use its spatial evidence.

## Choose building, level, and room context

@action: Open the Model, Building, and Level selector, then choose Riverside, Level 02.

Rooms and spaces comes from context models explicitly designated to provide rooms and levels. In a multi-building federation, choose the building before the level so two identically named floors do not become one ambiguous location.

The viewer remembers the selected building, level, room, display choice, and separate cameras for 3D and Plan. A ready aligned IFC can assign clashes to its spaces. A G L B or an unaligned model may still provide geometry without spatial identity.

## Move from 3D into Plan

@action: Switch from 3D to Plan and show Level 02 from the top with room outlines, labels, grids, and clash pins.

Plan is a true top-orthographic level review, not a screenshot of the model. It clips context to the selected level and composes with the section box. Room fills or outlines, labels, grids, and clash pins create a coordination drawing directly from the current scene.

Use the prepared one-point-two-metre cut height unless another height better exposes the route. Keep the active level visible in the control strip so the meeting never mistakes one floor for another.

Click the plant room. The selection connects the plan shape, its 3D volume when available, and the clashes assigned to that space. Choose Review this space to apply an exact room filter. It does not pull in a nearby clash whose anchor falls outside the room boundary.

Switch back to 3D and confirm the same room remains selected. Use the all-level view to understand vertical context, then lazy-load the selected room volume when a detailed enclosure helps.

@note: Before recording, verify the prepared IFC spatial derivative is ready and aligned. Geometry-only fallback is not a successful take for this chapter.
