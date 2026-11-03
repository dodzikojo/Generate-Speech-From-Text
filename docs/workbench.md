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
  **Save script text as…**. Hover a row for its quick actions (see **Rail row actions**),
  and drag a row to reorder it.
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

A new render selects its take automatically, unless you are listening to something: then
playback carries on and the new take waits in the take list. A refresh of the same
segment (for example after approval, or a render elsewhere saving the project) keeps the
take you were reviewing selected, keeps it playing, and leaves the editor's caret and
word-follow highlighting where they were.

## Rail row actions

Hover a row in the chapter rail to show its actions. They float over the right edge of
the row, so the text does not move.

- **Segment rows**: **Play** plays the take that needs listening to: the newest take
  awaiting review, otherwise the approved take, otherwise the latest take. The tooltip
  names the take. Play opens the segment (saving a draft first, as a click does) so word
  follow works, and turns into **Pause** while that take plays. **Approve** appears when
  a take made from the current script is awaiting review, and approves exactly that take.
  **Generate** opens the batch confirmation for that one segment with the project voice.
  **More** opens the row menu.
- **Chapter rows**: **Play** plays the chapter's approved takes (the tooltip gives the
  count and running time) and turns into **Stop** while the chapter plays. **Generate**
  opens the batch confirmation for the whole chapter.

The row's right-click menu has the same **Play take** and **Approve take** items, for
keyboard and screen-reader use.

Approving converts the take with FFmpeg and saves the project, so it never runs at the
same time as a render. Approvals asked for while a take renders (from the rail, the
Takes tab or the take bar) wait their turn: they run as soon as the current take
finishes, before a batch starts its next take. The batch strip shows how many approvals
are waiting.

## Reorder by dragging

Drag a segment above or below another segment of the same chapter, or a chapter above or
below another chapter. The drop is saved as one move in the project history, like **Move
up** and **Move down**, and the rail and the editor heading renumber at once. Archived
chapters keep their place. Moving a segment into another chapter, a chapter into a
chapter, or dropping a segment onto another segment is refused with a message, and the
rail goes back to the saved order. Dragging is off while you tick segments to generate.

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
the render queue shows `BATCH 2 OF 5`. The editor stays on the segment you have open:
segments that aren't open render in the background from their saved script and voice, so
you can keep reading, listening to finished takes and approving them. **Stop after
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

## Package approved takes

**Package approved takes…** copies each approved take, unchanged, into a folder you choose.
It is in Assembly's header, in the rail's **•••** menu, and on a chapter's right-click menu
(which ticks that chapter). The package looks like this:

```
ORCA Meta AI Hackathon - approved takes├─ 01 Introduction│  ├─ 01-001 Opening.wav
│  └─ 01-002 Introducing ORCA.wav
├─ 02 Demo│  └─ 02-001 Live walkthrough.wav
└─ voicebench-package.json
```

- Numbers follow the chapter rail, so files sort in narration order anywhere. Characters
  Windows doesn't allow in names become `-`.
- The dialog lists every chapter and segment. Approved segments can be ticked; the others
  say why not (not approved yet, script changed since approval, rendering). Chapters
  excluded from the compilation start unticked. Each chapter shows how much take audio it
  holds and how much clean-up would free, with a colour-coded bar: green is approved audio,
  amber is what clean-up can free, grey is takes that stay (segments awaiting review, takes
  a mix uses). Bars are drawn to the same scale, so the heaviest chapter stands out; hover
  a bar for exact sizes. A bar above the list shows the whole project.
- **Choose folder…** steps the dialog aside while the Windows folder picker is open, then
  brings it back with your ticks. Choosing a folder creates `<project> - approved takes`
  inside it, unless the folder already holds a package. VoiceBench remembers the folder for each project, so the next
  package goes there by default.
- **Packaging again syncs the folder.** `voicebench-package.json` records every file
  VoiceBench wrote and its hash. Unchanged files stay, newly approved segments are added, a
  segment with a new approved take is replaced, a moved or renamed segment's file is
  renamed (empty chapter folders are tidied away), and files for segments you untick, or
  that are no longer approved, are removed. These are copies; the takes stay in the
  project.
- Files you add to the package, or packaged files you edit afterwards, are never
  overwritten or removed; the result lists them as left untouched. A folder holding
  another project's package is refused.
- Every copy is verified against the approved take before it replaces anything. When it
  finishes, the result offers **Open folder** and, when there is something to free,
  **Clean up takes…**.

## Clean up takes

**Clean up takes…** (Assembly, the rail's **•••** menu, or after packaging) shows, per
chapter, the take audio on disk, the approved audio, and how much it can free, with the same
colour-coded bars. **Cancel** is the default button. Confirming moves
the audio of the **other takes of approved segments** to the Windows Recycle Bin:

- Approved takes are kept. Segments still awaiting review keep every take.
- A take used in a mix (a clip, or a pending replacement) is kept.
- Each take's raw file and every processed version of it go together.
- Cleaned-up takes stay in the segment's history marked **Audio cleaned up**, so take
  numbers never repeat. They can't be played or approved. Their files stay in the Recycle
  Bin until you empty it; restoring one puts the file back on disk, but the take stays
  cleaned up in VoiceBench.
- Files are only ever recycled, never deleted outright. When Windows can't recycle a file
  (a drive with no Recycle Bin, such as a network share, or a file bigger than the bin
  allows), the take is kept and listed, as is a take whose files are open in another app.
- Space is freed when the Recycle Bin is emptied. A segment whose approved take's audio is
  missing on disk keeps all its takes.
- Clean-up re-checks every take against the latest project while it runs, so a take that an
  agent approves or adds to a mix in the meantime is kept.
- A package run that stops part-way (for example, a file open in another app) still records
  what it wrote, so packaging again picks up where it left off.
- Clean-up waits while a render or approval runs.

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
