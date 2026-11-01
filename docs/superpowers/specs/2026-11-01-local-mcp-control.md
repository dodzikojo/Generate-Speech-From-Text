# VoiceBench Local MCP Control

Status: Ratified by Dodzi, 2026-11-01.

Implement the approved local desktop-control MCP plan. A stdio companion launches the
installed desktop app on the first operational request and uses a same-user, local-only
named pipe. Discovery does not launch the app. No HTTP transport or headless MCP mode.

Agents and the operator share the live editor, selected project/segment/take, undo/save,
directions, voice presets, conflicts, playback, authoring services, and render history.
Edits require revision tokens and UTF-16 ranges. Durable requests are deduplicated.
Project writes are coordinated and hold a cross-process lease. Long jobs have durable
IDs, progress, cancellation, explicit retries and interrupted recovery.

Coverage: recent projects/create/open/close/rename/validate; chapter/segment create,
import, duplicate, rename, reorder, split, archive, include, recoverable delete; scripts,
cues, live/saved text, direction, selection, history, revisions; provider/model/voice/tag
catalogs, presets, pronunciation; AI profiles/references/outlines/drafts/proposals;
render/audition/process/approve/pickup/alignment; playback/navigation/panels/font;
assembly eligibility/audio standard/pauses/mastering/production exports/artifacts;
credential status and secure key-entry UI, FFmpeg and local-control diagnostics.

Credentials are never read or supplied over MCP. Provider/model fallback remains
prohibited. Production cues never reach TTS. Approval takes an explicit render ID.
Successful audio survives alignment failure. Source snapshots and assembly eligibility
remain authoritative. Script text and credentials never enter diagnostic logs.

Ship a self-contained x64 voicebench-mcp.exe in the signed MSIX with a stable execution
alias. Settings displays connections/jobs and copyable Codex/generic configuration.
Verify protocol and application tests, Release build, signed packaging/install,
real stdio calls, and packaged UI. Never invoke the smoke skill.

Preserve user Projects/, artifacts/, sample manifest and audition files. Pre-existing
voice/tag/direction/version fixes were checkpointed in commit 78efcaf.
