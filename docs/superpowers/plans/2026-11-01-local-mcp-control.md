# Local MCP execution ledger

Branch: codex/local-mcp-control. Parent checkpoint: 78efcaf.

## Global constraints

- Follow AGENTS.md and the ratified local-MCP spec. No smoke skill, no live paid calls
  for automated tests. Do not touch user project/sample/generated files.
- Fresh-context implementation agents own independent paths. Root integrates and
  verifies slices; no agent commits or pushes. Reviewable commits/PRs per slice.
- Existing .NET 10 SDK pin is unavailable here: run dotnet from the local Temp folder
  with absolute project paths. Do not change global.json for that environment issue.
- Shared wire contracts are in VoiceBench.Application/AutomationContracts.cs.
  Commands use dot names (project.create); MCP tools use voicebench_project_create.
  Arguments/results are JSON with web/camelCase options. Desktop-only commands use
  IDesktopAutomationAdapter. No raw UI objects or credentials cross the bridge.
- Application owns workflow commands/jobs/leases and shared revisioned editor logic.
  Bridge owns named-pipe serialization/server/client in Application/Bridge/.
  Mcp owns SDK integration/tools/resources and companion startup.
  App owns WinUI adapter/live-editor/save/history/navigation integration.

## Slices

1. Application commands, live editing primitives, project ownership, operation tracking;
   deterministic tests. Add shared projects/references at integration.
2. Bridge + MCP host + desktop integration for project/editor/voice/history.
3. Complete writing/audio/jobs/playback/assembly/settings/artifact coverage.
4. Signed MSIX/alias, real stdio and installed-app integration, Codex instructions.

## Verification ledger

- Planning/checkpoint: complete.
- Implementation: all four slices complete, review commits/PRs recorded below.
- Release solution build: zero warnings/errors. All 292 tests pass: Core64, App42,
  Infrastructure133, Application28, MCP25. App42 rerun after the final status refresh fix.
- Signed x64 MSIX 1.0.0.5 built and installed, Windows status Ok, CN=IssueLab signature
  verified. Symbols-package warning (mspdbcmf.exe unavailable) does not affect delivery.
- Installed alias: 117 tools and seven resources, 16 live-runtime assertions pass.
  Verified closed-app launch, project/chapter creation, Unicode draft/direction save,
  conflict rejection, presets, history undo/redo, production exports, local PCM-tone
  playback/pause/seek/stop and bounded MP3 preview. Previous operator project restored;
  fixture entries removed from recents. Fixtures retained under artifacts/mcp-runtime.
- Codex: global voicebench stdio server registered to the WindowsApps alias; an actual
  ephemeral Codex session successfully called voicebench_diagnostics_get, confirming
  localOnly=true and the active project. Other configured third-party servers produced
  unrelated startup noise; isolated Codex configuration verified the VoiceBench call.
- CLI validation of the active desktop-owned project routed through MCP and returned
  issues=[] / isValid=true without competing for the manifest writer.
- No paid speech/writing requests or smoke skill were used. Provider tests use mocks.

## Implementation notes and remaining operator checks

- Shutdown retains the project lease until cancelled workers actually finish. Reopening
  the same root reuses its lease. FFmpeg setup also has durable jobs without a project.
- Provider completion merges into current manifests. GUI saves reject stale timestamps
  rather than overwriting concurrent results. Live dirty drafts survive refreshes.
- GUI audio/export operations share the durable queue with MCP. Existing GUI AI writing
  retains its shared WritingAssistant service and operator-review flow; MCP AI writing
  uses durable jobs/proposals. There is no headless MCP mode.
- Actual second-account/elevated-client connection attempts were not run. Pipe code
  enforces current SID/elevation and kernel rejection of remote clients; same-user,
  duplicate-owner, cancellation and framing tests pass.
- Runtime revealed the old FFmpeg9.0.1 dated download had expired (HTTP404). Updated the
  explicit pin to BtbN autobuild-2026-10-31-12-59, FFmpeg9.0.2 LGPL, SHA256
  9d37c564e0d01c18cde184a079fa6ef04d7d2783ada41da81d04165cc8f1a650,
  matching its published checksums.sha256 and GitHub asset digest. Actual install and
  preview succeeded. Future expired pins report an actionable Settings/update message.
- The PowerShell runtime harness must explicitly set UTF8 for all redirected streams;
  default Windows encoding corrupted café. No RichEditBox/newline patch was warranted.
- Original protected dirty sample manifest, Projects/, artifacts/ and old auditions
  remain outside commits. Host auto-memory storage was not configured, so the tracked
  program index is linked from repository AGENTS.md for fresh-session discovery.
- Next: review the stack and run the focused live-provider operator checklist in
  docs/local-mcp.md (real generation, approval, mastering, restart). No automatic merge.

## Review commits

- Prerequisite checkpoint: 78efcaf on codex/synchronized-word-follow-playback.
- Shared application/ownership: 0ef8675, codex/local-mcp-application.
- MCP host and bridge: 91d6114, codex/local-mcp-bridge.
- Desktop/audio/CLI: f0af1ed, codex/local-mcp-desktop.
- Packaging and verified delivery: 06b6a3c, codex/local-mcp-control.
- Stacked PRs in review order: [#3](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/3)
  → [#4](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/4)
  → [#5](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/5)
  → [#6](https://github.com/dodzikojo/Generate-Speech-From-Text/pull/6).
  The first PR targets the secured synchronized-playback prerequisite; later PRs target
  the preceding slice. They have not been merged into master.

Record test counts, runtime outcomes, package version, PRs and remaining limitations
here before delivery. Never claim an unverified surface works.
