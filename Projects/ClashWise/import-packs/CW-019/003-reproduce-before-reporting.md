## Trigger the harmless prepared failure

@action: Reproduce the synthetic “Title Update review rows stop after retry” scenario and show its exact visible state.

Use a controlled synthetic failure, never a customer model. Record the steps in order: open the Riverside test, start Title Update, filter failures, retry, and observe where the review rows stop.

Capture the exact message, the row state, whether Reset repairs the view, and whether restarting the workflow changes the result.

One clean reproduction is more useful than repeatedly clicking until the error disappears.

## Decide when to escalate

@screen: Show a short checklist: current version, repeatable steps, expected result, actual result, screenshot, safe attachment.

Escalate when the problem remains on the supported current setup, or when the error itself instructs you to contact support.

Separate expected product boundaries from faults. A skipped title because it was previously updated is not the same as a failed generation. A missing Publish button in the Starter installer is not a crashed Full Suite command.

Describe what you expected and what happened instead.
