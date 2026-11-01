## Prove that refresh follows access

@action: Change one prepared synthetic status in ClashWise, return to Power BI, select Refresh, and show the count update.

Desktop refresh requests current data through the connector. Membership changes apply on the next refresh. Data already imported remains in the report even if later access is removed, until the report owner deletes or replaces it.

Scheduled refresh in Power BI Service currently requires a Standard on-premises data gateway with the same custom connector installed and an authorized data source. Keep the precise gateway and connector-version procedure in the linked text reference.

@action: Open Connected Apps in ClashWise and locate the prepared Power BI authorization.

Revoke blocks future token renewal and refresh. It does not remotely erase data already imported into Power BI.

@visual: End on a successful refreshed report and the Connected Apps control.

Next, compare this OAuth reporting connection with read-only API keys and governed M C P clients.
