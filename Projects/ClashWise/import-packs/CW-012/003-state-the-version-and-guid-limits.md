## Know the Navisworks version limits

@screen: Keep the current Sync settings visible while the version caveats appear as simple on-screen callouts.

Status and sync comments are available across the currently supported Navisworks versions.

Native priority write-back is available only in Navisworks 2026 and later, because older supported releases do not expose the priority field the plugin needs.

Responsible-contact write-back to Assigned To is available in Navisworks 2025 and later. The control can still be visible when an older version cannot write that field, so verify the result in Clash Detective.

These are product compatibility details. Check the linked sync article immediately before recording and keep the version table in text help.

## Know the identity and title limits

@visual: Compare the original clash GUID with the copied or recreated result, then show an edited web title.

Sync matches the clash GUID. A copied test, recreated result, different document, or model reload that produces a new GUID is not the same record to the plugin. Unmatched clashes are skipped; ClashWise does not fall back to title or position.

Web-side title changes currently remain on the web and survive an Update, but Sync does not rename the Navisworks clash. Use Title Update inside the plugin when the model's native clash title must change.

These two boundaries prevent an approximate match from writing a decision onto the wrong issue.
