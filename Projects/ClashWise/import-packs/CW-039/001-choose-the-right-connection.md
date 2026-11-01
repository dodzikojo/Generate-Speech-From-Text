## Preview three controlled outcomes

@visual: Show an API key row with no secret visible, an active Connected Apps row, and one audited tool call.

ClashWise has distinct machine-access paths.

The Power BI connector uses OAuth and read-only reporting permission. Developer API keys use a Client I D and Client Secret for read-only export endpoints. M C P uses OAuth, organisation-enabled per-member capabilities, and can include controlled edits when those capabilities are granted.

Do not use an API key when an OAuth connector already exists. Do not grant an M C P write capability when the client only needs to inspect data.

The governing idea is minimum access, a named connection, a visible audit trail, and immediate revocation when the integration is no longer required.
