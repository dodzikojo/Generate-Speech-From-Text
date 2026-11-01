## Read the safe-write boundary

@screen: Filter or point through Create, Update, Unchanged, Conflict, and Obsolete rows in the reconciliation preview.

ClashWise only manages artifacts recorded in this setup's local installation manifest. It never edits unrelated search sets or tests.

A Create row has no managed native artifact yet. Update means the artifact still matches the last installed definition and can safely receive a reviewed matrix change. Unchanged needs no write.

Conflict means a person changed the managed artifact, or ClashWise cannot verify its last state. It is preserved by default. Inspect it and decide whether the native edit or matrix definition should win; the installer does not guess.

Obsolete means a formerly managed artifact is no longer required. Removal is explicit and unchecked by default. Obsolete tests are removed before their now-unused sets so references are not broken. An edited obsolete test also protects the set it still uses.

## Create the Navisworks definitions

@action: Leave removal off and choose Create search sets and clash tests.

The dynamic search sets are created below ClashWise dot Riverside Coordination Setup. Exact raw variants that consolidate into one class are combined as OR alternatives. Within each alternative, source, class, and optional discipline predicates are combined together.

One eligible active rule creates one test with a stable rule identity. Different endpoints use A versus B. Identical endpoints use self-intersection. Detection type and converted tolerance come from the reviewed rule.

The completion message reports created, updated, removed, and conflicting counts. Confirm the expected active-rule test count and inspect one Hard, one Clearance, and one self-intersection example in Clash Detective.

Nothing has been detected yet. The setup has created definitions only.
