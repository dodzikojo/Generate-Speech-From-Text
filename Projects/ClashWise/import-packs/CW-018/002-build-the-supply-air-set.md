## Define AND and OR conditions

@action: Name the set MEP - Supply Air and add the prepared category, property, operator, and value conditions.

Conditions inside one group all use AND. Every condition must match. Additional groups use OR, so an element can qualify through any complete group.

Build the Riverside supply-air definition from the real system property and the element category needed to exclude unrelated objects. Use the case option only when the model's stored values require it.

The value list comes from the active model. If a property is missing, verify category, scope, spelling, and source export rather than choosing a nearby value blindly.

## Verify the live match

@action: Read the match count, choose Select in model, inspect the highlighted ducts, then save as a search set.

A plausible count is not proof. Select the matches in the model and check that supply-air ducts are highlighted without structural or return-air objects.

Prune below match is useful when matching an assembly should not also return all nested children. Leave it off unless that hierarchy is part of the definition.

Save as Search Set. The native set is dynamic: as model properties change, Navisworks reevaluates the conditions. Overwriting a same-named search set preserves its folder and position after confirmation.
