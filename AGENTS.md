# VoiceBench repository guide

VoiceBench is a Windows 11 x64 narration workstation for one local operator.
It combines a packaged WinUI 3 desktop app with a small automation-friendly CLI.

## Start here

- Read `docs/superpowers/specs/2026-08-26-voicebench-design.md` before changing product behavior.
- Read `docs/superpowers/plans/2026-08-26-voicebench-execution.md` before starting a new slice.
- Read `docs/superpowers/MEMORY.md` for current program status and verified resume pointers.
- Treat Markdown/text files under a project's `02-scripts` directory as canonical content.
- Keep manifest paths relative to the project root so folders remain movable.
- Never put provider credentials, submitted text, or secret-bearing HTTP payloads in logs.

## Verified toolchain

- Windows 11 x64.
- .NET SDK 10.0.402, selected through `global.json`.
- Visual Studio 2026 with the **WinUI Application Development** workload.
- Windows App SDK 1.8.11 (`1.8.260804001`).
- Developer Mode is required for local packaged-app deployment.
- FFmpeg is managed at runtime; it is not a source-controlled prerequisite.

The required Visual Studio workload/component identifiers are:

- `Microsoft.VisualStudio.Workload.Universal`
- `Microsoft.VisualStudio.Component.WindowsAppSdkSupport.CSharp`

## Commands

Run commands from the repository root in PowerShell 5.1 or newer.

```powershell
dotnet restore VoiceBench.slnx
dotnet build VoiceBench.slnx -c Release
dotnet test VoiceBench.slnx -c Release --no-build
dotnet run --project src/VoiceBench.Cli -- doctor
dotnet msbuild src/VoiceBench.App/VoiceBench.App.csproj -restore -p:Configuration=Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
git diff --check
```

For a fast non-UI loop, build/test Core, Infrastructure, and CLI directly. A product-surface
change is not complete until the packaged app has also been launched and inspected.

## Architecture boundaries

- `VoiceBench.Core` owns versioned contracts and business rules; it has no provider or UI dependency.
- `VoiceBench.Infrastructure` owns filesystem, HTTP providers, Windows credentials, processes, and FFmpeg.
- `VoiceBench.Audio` owns the deterministic 48 kHz stereo mixer and bounded read-ahead;
  the same graph feeds WASAPI monitoring and offline mix exports. Never decode/read
  files or marshal to the UI from its device callback.
- `VoiceBench.Application` owns desktop automation commands, revisioned draft rules, project ownership, and durable operations.
- `VoiceBench.Mcp` is the packaged local stdio companion; it communicates through the same-user named-pipe bridge and never hosts HTTP.
- `VoiceBench.Cli` and `VoiceBench.App` are composition roots over the same application services.
- Provider-specific directions and pronunciation substitutions belong in `03-render-copies`.
- Raw provider audio is immutable. New attempts always create new versioned files.
- No provider/model fallback is allowed without an explicit operator choice.
- Approved text changes mark matching renders stale and block mastering.
- Render jobs are journaled; recovered `InProgress` jobs become `Interrupted`.
- Local MCP setup and runtime checks are documented in `docs/local-mcp.md`. The installed alias is `voicebench-mcp.exe`.
- Desktop layout, project-folder actions and chapter playback are described in `docs/workbench.md`.
  Testable presentation/path logic lives in `VoiceBench.App/Services` and is linked into
  `VoiceBench.App.Tests`; resolve file actions through `ProjectLocations` so they stay inside the project.
- Multitrack rules and runtime checks are in `docs/audio-timeline.md`. Audio clip splits
  do not split canonical scripts; mix transcripts use retained approved source ranges.

## Tests and fixtures

- Prefer deterministic clocks, IDs, HTTP handlers, and process runners in unit tests.
- Provider tests use mocked HTTP. They must never require live keys.
- Filesystem tests use a unique temporary directory and clean only that exact directory.
- Keep `samples/legacy-clashwise` useful as an import fixture and manual demo project.
- App tests verify XAML/resources and presentation logic; packaged runtime smoke remains mandatory.

## Known environment failure

Windows Application Control can block freshly built executables or test DLLs with:

`0x800711C7: Your organization used App Control for Business to block this app.`

That is an environmental failure, not a VoiceBench defect. Rebuild and rerun once. Do not
weaken security settings or alter product code to route around it. If it persists, report the
exact binary and command that Windows blocked.

## Explicit non-goals for v1

- Cross-platform builds or a browser client.
- Cloud sync, collaboration, team accounts, or automatic updates.
- Voice cloning, MIDI/VST hosting, spectral editing, or caption timing.
- Direct ClashWise repository coupling or ClashWise-specific project semantics.
- Legacy Python CLI compatibility.

## Change discipline

- Large slices use `codex/` branches and reviewable commits/PRs.
- Preserve user-authored working-tree changes and keep unrelated edits out of a slice.
- Update schema migrations and migration tests together.
- Run Release build, all relevant tests, packaging, `git diff --check`, and a runtime smoke
  before claiming a product slice is complete.
