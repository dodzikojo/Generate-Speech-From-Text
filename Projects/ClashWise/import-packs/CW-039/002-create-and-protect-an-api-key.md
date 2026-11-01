## Generate a read-only key

@action: Open Profile, scroll to API access, and generate Training, legacy export read.

Developer API access requires a current Clash Management entitlement. Give each integration its own descriptive key so the Last used value and future revocation have a clear meaning.

The creation dialog shows a Client I D and a Client Secret. The secret is shown once and cannot be recovered.

@screen: Replace the live secret reveal with the prepared masked pickup; show no clipboard, notification, terminal, or password-manager surface.

Store the secret in an appropriate secrets manager off-screen. Never email it, paste it into a script, commit it, or include it in a recording.

## Understand the API boundary

The legacy export API uses Basic authentication: Client I D as username and Client Secret as password. It is read-only. It can retrieve available data but cannot modify a clash.

Keep endpoint paths, parameters, and response formats in the text-first developer reference where they can be copied accurately.
