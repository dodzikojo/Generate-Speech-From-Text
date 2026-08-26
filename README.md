# VoiceBench

VoiceBench is a Windows 11 narration workstation for turning folder-based scripts into
auditioned, approved, mastered audio. It keeps the clean transcript separate from the exact
provider submission, preserves every raw take, and makes narration status visible on a
segment-by-segment take rail.

The repository contains a packaged .NET 10 / WinUI 3 app and a companion `voicebench` CLI.
Google Gemini and OpenAI speech generation sit behind provider-neutral contracts; FFmpeg is
downloaded and verified on first use for mastering.

## Prerequisites

- Windows 11 x64 with Developer Mode enabled.
- .NET SDK 10.0.303 or a compatible 10.0 patch.
- Visual Studio 2026 with **WinUI Application Development** installed.
- A Gemini and/or OpenAI API key for live rendering. Unit tests do not require keys.

## Build

```powershell
dotnet restore VoiceBench.slnx
dotnet build VoiceBench.slnx -c Release
dotnet test VoiceBench.slnx -c Release --no-build
dotnet run --project src/VoiceBench.Cli -- doctor
```

Build an x64 MSIX with:

```powershell
dotnet msbuild src/VoiceBench.App/VoiceBench.App.csproj -restore `
  -p:Configuration=Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true
```

## CLI

```text
voicebench doctor
voicebench project create <folder> --name <name>
voicebench project validate <project>
voicebench render <project> --segment <id>
voicebench render <project> --ready
voicebench master <project> --name <name>
```

The CLI looks for `GEMINI_API_KEY` or `OPENAI_API_KEY` first, then Windows Credential
Manager. Keys are never accepted as command-line arguments.

## Project layout

Each project is an ordinary movable folder. Script files in `02-scripts` are canonical;
`voicebench.project.json` stores stable IDs, order, settings, hashes, statuses, and history.
See the [product design](docs/superpowers/specs/2026-08-26-voicebench-design.md) for the full
contract and the [execution plan](docs/superpowers/plans/2026-08-26-voicebench-execution.md)
for slice boundaries and acceptance checks.

The retired Python prototype and its original README are preserved under `legacy/python-v1`.
Legacy ClashWise text is retained only as a sample/import fixture; VoiceBench itself is generic.
