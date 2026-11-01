# Local MCP control

VoiceBench exposes its desktop workspace through a local stdio MCP server. The signed
x64 installer includes the self-contained `voicebench-mcp.exe` companion and registers
its Windows execution alias. A developer SDK is not needed on the installed PC.

Enable **Settings → Local MCP**. Discovery is immediate and does not launch a window.
The first operational request attaches to VoiceBench or opens the installed app. All
requests run as the current Windows user. There is no HTTP listener or remote endpoint.

## Connect Codex

Run once after installing VoiceBench:

```powershell
codex mcp add voicebench -- voicebench-mcp.exe
codex mcp get voicebench
```

Alternatively add this to the MCP client's configuration:

```toml
[mcp_servers.voicebench]
command = "voicebench-mcp.exe"
```

Generic stdio configuration:

```json
{
  "mcpServers": {
    "voicebench": { "command": "voicebench-mcp.exe", "args": [] }
  }
}
```

Settings also provides the full local execution-alias path if the client's PATH does
not include WindowsApps. Start a new client session after adding the server. Do not
use `dotnet run` as the installed server command: build output is not MCP protocol.

Configuration follows the [official Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp).

## Work with the live workspace

Tools are named `voicebench_<area>_<action>`. Discover tools and their schemas from the
server; don't infer provider or model IDs. Tool results contain `isError`, `code`,
`message`, and `data`. Business errors keep the connection open.

1. Read `voicebench_workspace_get`, then open or create the intended project.
2. Create/import chapters and select the segment to edit.
3. Read `voicebench_editor_get`; use its current `revision` for every editor change.
4. Submit `voicebench_editor_replace` or `voicebench_editor_edit` with
   `expectedRevision`. Range offsets are UTF-16, matching WinUI. Changes appear in the
   desktop immediately and share editor undo/redo.
5. Save with `voicebench_editor_save`. Directions participate in the same save.
6. Discover providers/voices, save a precise preset and choose its project or segment
   scope. Starting a render saves the shared draft through the same UI preflight.
7. Start the operation with a stable unique `requestId`; inspect its returned `jobId`
   with `voicebench_jobs_get` or bounded `voicebench_jobs_wait`.
8. Process/review a particular `renderId`, approve it explicitly, then inspect
   `voicebench_assembly_get` before starting a chapter or compilation master.

Reuse a request ID only for a retry of the identical request. A different operation
must use a new ID. Interrupted work requires an explicit retry. Disconnecting the MCP
client does not silently repeat generation. Project switching is blocked while jobs
are active.

On a revision or external-file conflict, read the current draft and compare the disk
version before issuing another edit. Editor conflict tools expose explicit reload,
keep and compare choices. Changing narration makes previous audio stale; cue-only
changes continue to follow the existing clean-narration rules.

Writing calls return durable proposals instead of applying provider output. Inspect
the proposal, then explicitly accept or reject it. Acceptance checks the original
source hash. Credentials are entered in VoiceBench's secure dialog; tools expose only
configured status. Local MCP control does not change the normal internet requirements
of the selected speech or writing provider.

Resources expose workspace/project/editor state, provider catalogs, jobs, proposals,
exports and segment text. Clients can subscribe to changes. Long text supports bounded
reads with explicit continuation. Audio-preview tools return a derived clip of at most
30 seconds, and link the original artifact; original take files remain unchanged.

## Verify an installation

The automated stdio check creates an isolated project and restores the previous
project afterward. It performs no provider requests:

```powershell
.\scripts\Test-LocalMcp.ps1 -ServerExecutable "$env:LOCALAPPDATA\Microsoft\WindowsApps\voicebench-mcp.exe" -TestProjectRoot "C:\Temp\VoiceBench-Mcp-Test"
```

Use a new, empty test location. The fixture remains there for inspection. Automated
provider tests use mocks. For the final operator check:

- Start from a closed app and ask Codex to inspect VoiceBench; confirm one window opens.
- Edit text, cues and directions through MCP and manually; check revision conflicts,
  undo/redo, cue colors and Save.
- Choose a voice, generate audio, check progress/cancel/retry, play/seek and Follow script.
- Approve the chosen take, build a master and inspect the exported source-aligned files.
- Restart VoiceBench, reconnect and inspect project/job persistence.

The smoke skill is not part of this workflow.
