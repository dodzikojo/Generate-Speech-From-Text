## Prove revocation works

@action: Show a prepared harmless read request succeeding with all credentials and headers cropped.

Confirm the request reads only synthetic training data. Then return to the key list, select Revoke key, and read the confirmation.

Revocation is immediate and cannot be undone. Confirm it, repeat the same prepared request, and show only the unauthorized status—not the command, header, or secret.

If a secret is lost or may have been exposed, revoke it and create a replacement. The key list supports a limited number of credentials, so remove retired integrations rather than reusing one shared key indefinitely.
