# Workbench and studio layout

VoiceBench 1.2 reorganises the desktop around the script. The editor always gets the
full height of the window; everything else sits beside it or in a single take bar.

## Title bar and project folders

The project menu in the centre of the title bar names the open project and opens its
folders in File Explorer:

| Menu item | Folder |
| --- | --- |
| Open project folder (Ctrl+Shift+E) | project root |
| Generated audio (takes) | `04-generated-audio` |
| Masters and mix exports | `05-masters` |
| Scripts | `02-scripts` |
| Exports (transcripts, cue sheets) | `06-exports` |
| Reports | `07-reports` |

Empty output folders are created on first open. **Copy project path** puts the root on
the clipboard, and **Switch or create project…** opens Projects. The button at the left
of the title bar expands the section rail. Sections have shortcuts Alt+1 to Alt+6
(Projects, Workbench, Voice Lab, Audio, Assembly, Settings).

## Workbench layout

- **Chapter rail** (left): chapters and segments with status colours. Each chapter shows
  how many segments are approved and the total running time of its approved takes
  (for example `7/8 approved · 01:37 approved audio`). The **•••** menu and the
  right-click menu include **Show in folder** (the script) and **Show takes folder**
  (that segment's audio). A segment's right-click menu also has **Copy script text** and
  **Save script text as…**.
- **Script editor** (centre): the folder icon beside the script path shows the file in
  Explorer. **Export text** copies or saves the segment's text: the **full script**
  (with production cues) or the **narration only** (the words that are spoken). It
  exports what the editor shows, including unsaved edits, and saves with Windows line
  endings. **Mark ready** is Ctrl+Enter.
- **Inspector** (right) has three tabs. The last tab you used is remembered.
  - **Voice**: provider, model, voice, direction, **Render segment** (Ctrl+R) and
    **Render & play**, which plays the new take as soon as it is ready. **Batch** starts
    a batch generation (see below).
  - **Takes**: every take of the segment, newest first. Each take shows its status
    (Approved, Awaiting review, Script changed, Previously approved, Failed), voice,
    duration, and time. Use **Play**, **Show in folder**, **Copy path** and **Approve** on
    each card. The right-click menu adds **Open in default player**. **Takes folder**
    and **All audio** open the segment's take folder and `04-generated-audio`.
  - **Write**: the writing assistant. Proposals never apply silently.
- **Take bar** (bottom): older/newer take, **Play take** (Ctrl+Space), restart, seek
  and time, word-follow status, **Request pickup**, **Approve take**, show the take in
  Explorer, and a **•••** menu with Open in default player, Copy file path, Open takes
  folder and Show all takes.
- **Mix timeline dock**: hidden by default so the script keeps its height. Turn it on
  with the timeline toggle at the right of the header. The Audio section has the full
  timeline.

The header toggles show or hide the rail, the inspector, the take bar and the timeline
dock. These choices persist. Narrow windows replace the rail with a segment picker and
keep the inspector beside the script. Only windows narrower than 700 px stack the
inspector beneath it.

A new render selects its take automatically. A refresh of the same segment (for example
after approval) keeps the take you were reviewing selected.

Status messages appear on a card at the bottom of the script. Progress and success
messages close on their own after a few seconds (hovering keeps them open). Warnings
and errors stay until you close them.

## Generate several segments

Batch generation renders a new take for several segments, one provider request at a
time, with the **project voice**. Start it from:

- **Batch › From this segment to chapter end…** or **Batch › Whole chapter…** in the
  Voice tab.
- **Generate from here to chapter end…** on a segment's right-click menu, or **Generate
  chapter…** on a chapter's.
- The rail's select button (or **Select segments to generate**): tick segments, or tick
  a chapter for all of its segments, then press **Generate…**. Ticks survive saves and
  renders while selection mode is on.

A confirmation lists every candidate in chapter order with its status and estimated
length, and names the voice. Approved segments start unticked, because a new take
replaces their approved status until you approve again. Segments that are already
rendering, or whose script has a cue error or no narration, cannot be ticked. If the
Voice tab shows a different voice from the project voice, the confirmation says so;
click **Use as project voice** first to generate with it. When the project has no voice
yet, the voice shown in the Voice tab becomes the project voice.

While it runs, a strip above the take bar shows `Generating 2 of 5` and the segment, and
the render queue shows `BATCH 2 OF 5`. The editor follows each segment. **Stop after
this take** finishes the current take and stops; pressing it again (**Cancel this take
too**) cancels the current request. **Cancel render** in the Voice tab cancels at once.

A problem with one segment's script or the provider refusing one script skips to the
next segment. Missing or rejected keys, rate limits, network failures, file errors or an
unresolved external edit stop the batch, so no more requests are sent. So do three
failures in a row. The final message lists what was generated, what failed and why, and
how many segments were not generated. Generated takes are never discarded.

## Play a chapter

Chapter playback plays every approved take of a chapter in chapter order. It uses the
take named as each segment's approval. Segments without an approved take are skipped,
listed in a message, and never replaced by another take.

Start it from any of these:

- The play-list button beside **Play take** in the take bar, or Ctrl+Shift+Space.
- **Play approved takes** in the chapter's right-click menu, or **Play chapter (approved
  takes)** in the rail's **•••** menu.
- **Play chapter from here** on a segment's right-click menu.
- **Play approved takes** on a chapter card in Assembly.

The editor follows each segment, so word-follow highlighting works take by take. The
project's segment pause (up to 2 seconds) separates takes. A strip above the take bar
shows the chapter, the position (for example `2 / 5`), the take number, and whether the
script changed since approval. Use it to skip back or forward, or to **Stop chapter**.
Pausing keeps the session, so pressing Play take resumes it. Choosing another segment
or take yourself ends it.

## Projects, Assembly, Audio, Voice Lab and Settings

- **Projects**: cards show each project's status. Each card has **Show in folder**,
  **Copy folder path** (right-click) and **Remove from list**. Removing a project never
  touches its folder. Filter by name or folder when more than one project is listed.
- **Assembly**: **Masters folder** opens `05-masters`. **Built masters** lists narration
  masters and mix exports, newest first, with inline playback, Show in folder, Open in
  default player and Copy file path. After a build, the message offers **Show in
  folder**. Each chapter card shows its approved-audio total and has **Play approved
  takes**.
- **Audio**: header buttons open `04-generated-audio` and `05-masters`. Mix export can
  show the exported file in Explorer when it finishes (on by default).
- **Voice Lab**: each audition card has **Show in folder** for its sample.
- **Settings**: credential rows show green when a key is configured and offer
  **Replace key**. The FFmpeg card shows the executable path and can show it in Explorer.

## Local MCP

`panels.set` also accepts `timelineDock`, and `workspace.get` reports it under
`panels.timelineDock`.
