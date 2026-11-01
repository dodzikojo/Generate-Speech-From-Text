Ship the self-contained MCP companion in signed VoiceBench 1.0.0.5 with a stable Windows execution alias and Codex/generic setup instructions. Register all projects in the solution and add a repeatable installed-runtime check. The expired FFmpeg download pin is replaced with a dated 9.0.2 LGPL build whose published SHA256 was verified.

Slice 4 of 4, stacked on `codex/local-mcp-desktop`.

Verified: Release build; 292 automated tests; signed MSIX signature and in-place installation (Windows status Ok); 117 tools and seven resources; 16 installed-runtime assertions including local-tone playback and MP3 preview; actual Codex diagnostics call; CLI validation routed to the active desktop; git diff --check.

The smoke skill and live paid generation were not used. Operator checklist: docs/local-mcp.md. Original user project/sample/generated working-tree files are excluded. Review and merge the stack in order after its prerequisite branch.
