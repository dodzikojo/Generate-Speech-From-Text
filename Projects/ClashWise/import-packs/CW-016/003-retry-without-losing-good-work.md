## Isolate and retry failures

@action: Choose the Failures chip, open the prepared failure reason, then choose Retry failed.

Filter to Failures and read the reason before retrying. A missing value may require Property Mapping; a transient model-engine failure may succeed on retry.

Retry failed sends only the failed rows back through generation. It does not discard generated, edited, or rejected work elsewhere in the batch.

When the prepared row succeeds, inspect its proposal and acceptance checkbox exactly like any other generated result.

## Understand interrupted-run recovery

@screen: Show the prepared “Recover proposed titles from the interrupted run?” prompt in a safe copied state.

ClashWise writes a recovery record while generation is in progress. If Navisworks or the workflow closes unexpectedly, reopening Title Update for the same document can offer to recover completed proposals.

Recovery saves the generated work; it does not claim that unprocessed rows completed. Accept the recovery prompt, audit the restored grid, and continue or retry as needed.
