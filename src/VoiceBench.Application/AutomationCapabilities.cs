using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Application;

public sealed record AutomationCapability(string Command, string Description, bool ReadOnly, bool Destructive, JsonElement InputSchema)
{
    public string ToolName => "voicebench_" + Command.Replace('.', '_');
}

/// <summary>Discovery is static and never starts or connects to the desktop.</summary>
public static class AutomationCapabilities
{
    public static IReadOnlyList<AutomationCapability> All { get; } = Build();

    private static IReadOnlyList<AutomationCapability> Build()
    {
        List<AutomationCapability> commands = [];
        void Add(string command, string description, bool readOnly = false, bool destructive = false, params (string Name, string Type, bool Required)[] fields)
        {
            Dictionary<string, object> properties = [];
            foreach (var field in fields)
                properties[field.Name] = field.Type switch
                {
                    "strings" => new { type = "array", items = new { type = "string" } },
                    "edits" => new { type = "array", items = new { type = "object", properties = new { start = new { type = "integer", minimum = 0 }, length = new { type = "integer", minimum = 0 }, text = new { type = "string" } }, required = new[] { "start", "length", "text" }, additionalProperties = false } },
                    _ => new { type = field.Type },
                };
            commands.Add(new(command, description, readOnly, destructive, AutomationJson.Element(new
            {
                type = "object", properties, required = fields.Where(field => field.Required).Select(field => field.Name).ToArray(), additionalProperties = false,
            })));
        }
        Add("workspace.get", "Read the active project, selection, editor and desktop state.", true);
        Add("project.recent", "List recent local projects.", true);
        Add("project.recent.remove", "Remove a project from the recent list without deleting its files.", fields: [("projectRoot", "string", true)]);
        Add("project.create", "Create and activate a project at an exact folder path.", fields: [("projectRoot", "string", true), ("name", "string", true)]);
        Add("project.open", "Open a local project and acquire exclusive ownership.", fields: [("projectRoot", "string", true)]);
        Add("project.close", "Close the project; dirty editors require an explicit discard choice.", fields: [("discardDraft", "boolean", false)]);
        Add("project.get", "Read the active project manifest and root.", true);
        Add("project.refresh", "Refresh the active project from disk.");
        Add("project.rename", "Rename the project display name.", fields: [("name", "string", true)]);
        Add("project.validate", "Validate paths, scripts, production cues and hashes.", true);
        Add("chapter.list", "Read all chapters, including archived chapters.", true);
        Add("chapter.create", "Create a chapter with one blank opening segment.", fields: [("title", "string", true), ("brief", "string", false)]);
        Add("chapter.import", "Import Markdown or text files as chapters.", fields: [("sourcePaths", "strings", true)]);
        foreach (string verb in new[] { "duplicate", "archive", "restore", "delete" })
            Add("chapter." + verb, verb + " a chapter; deletes are recoverable through project history.", destructive: verb == "delete", fields: [("chapterId", "string", true)]);
        Add("chapter.rename", "Rename a chapter.", fields: [("chapterId", "string", true), ("title", "string", true)]);
        Add("chapter.reorder", "Move a chapter by an offset in project order.", fields: [("chapterId", "string", true), ("offset", "integer", true)]);
        Add("chapter.include", "Set whether a chapter participates in compilation mastering.", fields: [("chapterId", "string", true), ("included", "boolean", true)]);
        Add("chapter.configure", "Set chapter brief and selected writing references.", fields: [("chapterId", "string", true), ("brief", "string", false), ("referenceIds", "strings", false)]);
        Add("chapter.from-draft", "Create a chapter from locally authored draft segments.", fields: [("draft", "object", true), ("brief", "string", false), ("referenceIds", "strings", false)]);
        Add("segment.list", "Read segments, optionally within one chapter.", true, fields: [("chapterId", "string", false)]);
        Add("segment.create", "Create a segment from canonical authoring text.", fields: [("chapterId", "string", true), ("title", "string", true), ("text", "string", false)]);
        foreach (string verb in new[] { "duplicate", "delete" }) Add("segment." + verb, verb + " a segment; deletion is recoverable in project history.", destructive: verb == "delete", fields: [("segmentId", "string", true)]);
        Add("segment.rename", "Rename a segment.", fields: [("segmentId", "string", true), ("title", "string", true)]);
        Add("segment.reorder", "Move a segment within its chapter by an offset.", fields: [("segmentId", "string", true), ("offset", "integer", true)]);
        Add("segment.split", "Split saved text at a UTF-16 position guarded by its authoring hash.", fields: [("segmentId", "string", true), ("cursor", "integer", true), ("expectedAuthoringSha256", "string", true)]);
        Add("segment.ready", "Mark a segment ready for explicit rendering.", fields: [("segmentId", "string", true)]);
        Add("script.get", "Read a page of saved authoring text, narration and parsed production cues.", true, fields: [("segmentId", "string", true), ("offset", "integer", false), ("count", "integer", false)]);
        Add("editor.get", "Read the shared live editor and revision token.", true);
        Add("editor.replace", "Replace live editor text, requiring its current revision.", fields: [("expectedRevision", "string", true), ("text", "string", true)]);
        Add("editor.edit", "Apply nonoverlapping UTF-16 range edits to the shared live draft.", fields: [("expectedRevision", "string", true), ("edits", "edits", true)]);
        foreach (string verb in new[] { "save", "undo", "redo", "reload" }) Add("editor." + verb, verb + " the shared live editor.", fields: [("expectedRevision", "string", true)]);
        Add("editor.conflict", "Resolve an external edit using reload, keep or compare.", fields: [("expectedRevision", "string", true), ("choice", "string", true)]);
        Add("directions.get", "Read the selected segment's live directions.", true);
        Add("directions.set", "Edit live directions using the current editor revision.", fields: [("expectedRevision", "string", true), ("directions", "string", true)]);
        Add("cues.insert", "Insert a production cue in the live editor.", fields: [("expectedRevision", "string", true), ("position", "integer", true), ("type", "string", true), ("content", "string", true)]);
        Add("cues.update", "Update a cue by ordinal in the live editor.", fields: [("expectedRevision", "string", true), ("ordinal", "integer", true), ("type", "string", true), ("content", "string", true)]);
        Add("cues.delete", "Delete a cue by ordinal in the live editor.", fields: [("expectedRevision", "string", true), ("ordinal", "integer", true)]);
        Add("selection.get", "Read selected chapter, segment, take and UTF-16 text range.", true);
        Add("selection.set", "Select a chapter, segment or explicit take and optional revision-guarded text range.", fields: [("chapterId", "string", false), ("segmentId", "string", false), ("renderId", "string", false), ("start", "integer", false), ("length", "integer", false), ("expectedRevision", "string", false)]);
        Add("navigation.show", "Navigate to Projects, Workbench, VoiceLab, Assembly or Settings.", fields: [("page", "string", true)]);
        Add("panels.set", "Set an editor panel's visibility.", fields: [("panel", "string", true), ("visible", "boolean", true)]);
        Add("font.set", "Set the script editor's font size.", fields: [("size", "number", true)]);
        Add("providers.list", "Read available speech and writing providers, models, voices and tags.", true);
        Add("director.get", "Read live exact provider, model, voice and directions controls.", true);
        Add("director.set", "Set live exact provider, model, voice and directions controls.", fields: [("providerId", "string", false), ("modelId", "string", false), ("voice", "string", false), ("directions", "string", false), ("expectedRevision", "string", true)]);
        Add("presets.list", "Read project voice presets and default preset.", true);
        Add("presets.save", "Create or update an exact provider/model/voice preset.", fields: [("preset", "object", true)]);
        Add("presets.delete", "Remove an unused voice preset.", destructive: true, fields: [("presetId", "string", true)]);
        Add("presets.default", "Select an exact project default preset.", fields: [("presetId", "string", true)]);
        Add("presets.assign", "Assign an exact voice preset to one segment.", fields: [("segmentId", "string", true), ("presetId", "string", true)]);
        Add("lexicon.get", "Read pronunciation substitutions.", true);
        Add("lexicon.set", "Replace pronunciation substitutions.", fields: [("entries", "array", true)]);
        Add("history.get", "Read durable project undo and redo history.", true);
        Add("history.undo", "Undo the latest saved authoring transaction.");
        Add("history.redo", "Redo the next saved authoring transaction.");
        Add("revisions.list", "Read a segment's accepted writing revisions.", true, fields: [("segmentId", "string", true)]);
        Add("revisions.restore", "Restore accepted or previous text from a writing revision.", fields: [("segmentId", "string", true), ("revisionId", "string", true), ("restorePreviousText", "boolean", false)]);
        Add("writing.profile.get", "Read the project writing profile.", true);
        Add("writing.profile.set", "Replace the project writing profile.", fields: [("profile", "object", true)]);
        Add("writing.references.list", "Read imported writing references.", true);
        Add("writing.references.import", "Import a Markdown or text reference.", fields: [("sourcePath", "string", true)]);
        Add("writing.references.remove", "Remove a writing reference and chapter associations.", destructive: true, fields: [("referenceId", "string", true)]);
        Add("writing.outline", "Create an AI chapter-outline proposal using an explicit provider and model.", fields: [("providerId", "string", true), ("modelId", "string", true), ("title", "string", true), ("goal", "string", true), ("targetDurationSeconds", "integer", true), ("brief", "string", false), ("referenceIds", "strings", false)]);
        Add("writing.draft", "Create a chapter-draft proposal from an outline proposal.", fields: [("providerId", "string", true), ("modelId", "string", true), ("outlineId", "string", true), ("brief", "string", false), ("referenceIds", "strings", false), ("generateProductionCues", "boolean", false)]);
        Add("writing.outline.edit", "Edit a pending outline before creating its draft.", fields: [("proposalId", "string", true), ("outline", "object", true)]);
        Add("writing.revise", "Create a source-guarded writing proposal; explicitly save the live draft first if requested.", fields: [("providerId", "string", true), ("modelId", "string", true), ("segmentId", "string", true), ("action", "string", true), ("instruction", "string", false), ("saveDraft", "boolean", false), ("expectedRevision", "string", false)]);
        Add("writing.proposals.list", "Read local pending and accepted writing proposals.", true);
        Add("writing.proposals.accept", "Accept a guarded draft or text proposal through project history.", fields: [("proposalId", "string", true)]);
        Add("writing.proposals.reject", "Mark a proposal rejected.", fields: [("proposalId", "string", true)]);
        Add("jobs.list", "Read durable local jobs and their progress.", true);
        Add("jobs.get", "Read one durable job including result.", true, fields: [("jobId", "string", true)]);
        Add("jobs.wait", "Wait up to thirty seconds for a job to finish.", true, fields: [("jobId", "string", true), ("timeoutMilliseconds", "integer", false)]);
        Add("jobs.cancel", "Cancel a queued or running job.", fields: [("jobId", "string", true)]);
        Add("jobs.retry", "Explicitly retry a failed, interrupted or cancelled job.", fields: [("jobId", "string", true)]);
        Add("render.start", "Queue a saved segment render with its selected exact preset.", fields: [("segmentId", "string", true)]);
        Add("render.batch", "Queue sequential renders for explicit segments, a chapter or ready segments.", fields: [("segmentIds", "strings", false), ("chapterId", "string", false), ("ready", "boolean", false)]);
        Add("render.audition", "Queue an audition with an explicit provider, model and voice.", fields: [("slot", "string", true), ("providerId", "string", true), ("modelId", "string", true), ("voice", "string", true), ("directions", "string", false), ("text", "string", true)]);
        Add("render.list", "Read immutable take history.", true, fields: [("segmentId", "string", true)]);
        Add("render.process", "Queue processing of an explicitly selected take.", fields: [("segmentId", "string", true), ("renderId", "string", true)]);
        Add("render.approve", "Approve an explicit render ID; imported narration requires confirmedTranscript=true.", fields: [("segmentId", "string", true), ("renderId", "string", true), ("explicitlyApproveStaleSource", "boolean", false), ("confirmedTranscript", "boolean", false)]);
        Add("render.pickup", "Mark a segment as requiring a pickup.", fields: [("segmentId", "string", true)]);
        Add("alignment.start", "Queue take word alignment; imported narration requires explicit providerId/modelId.", fields: [("segmentId", "string", true), ("renderId", "string", true), ("providerId", "string", false), ("modelId", "string", false)]);
        Add("alignment.get", "Read a take's word-alignment records and latest artifact.", true, fields: [("segmentId", "string", true), ("renderId", "string", true)]);
        Add("playback.get", "Read desktop playback state.", true);
        Add("playback.play", "Play a selected take or specified project audio artifact.", fields: [("segmentId", "string", false), ("renderId", "string", false), ("relativePath", "string", false)]);
        Add("playback.pause", "Pause playback.");
        Add("playback.stop", "Stop playback.");
        Add("playback.seek", "Seek playback in seconds.", fields: [("seconds", "number", true)]);
        Add("playback.follow", "Enable or disable word-follow playback.", fields: [("enabled", "boolean", true)]);
        Add("follow.set", "Enable or disable word-follow playback.", fields: [("enabled", "boolean", true)]);
        Add("assembly.get", "Read authoritative assembly eligibility and ordering.", true, fields: [("scope", "string", false), ("chapterId", "string", false)]);
        Add("assembly.configure", "Set the project audio standard and inter-segment/chapter pauses.", fields: [("audioStandard", "object", true)]);
        Add("master.start", "Queue mastering of approved source-aligned takes.", fields: [("name", "string", true), ("scope", "string", false), ("chapterId", "string", false)]);
        Add("exports.create", "Queue generation of the source-aligned production script and cue-sheet pack.", fields: [("name", "string", true), ("scope", "string", false), ("chapterId", "string", false)]);
        Add("exports.list", "List project export, master and report artifacts.", true);
        Add("artifacts.list", "List artifacts under an allowed project output directory.", true, fields: [("relativeDirectory", "string", false)]);
        Add("artifacts.read", "Read a page of a text artifact under an allowed project output directory.", true, fields: [("relativePath", "string", true), ("offset", "integer", false), ("count", "integer", false)]);
        Add("artifacts.audio-preview", "Read a bounded audio preview of a known project audio artifact.", true, fields: [("relativePath", "string", true), ("startSeconds", "number", false), ("durationSeconds", "number", false)]);
        Add("artifacts.open", "Open a known project output artifact.", fields: [("relativePath", "string", true)]);
        Add("artifacts.reveal", "Reveal a known project output artifact in Explorer.", fields: [("relativePath", "string", true)]);
        Add("file.open", "Open a known canonical script, reference or output file.", fields: [("relativePath", "string", true)]);
        Add("file.reveal", "Reveal a known canonical script, reference or output file.", fields: [("relativePath", "string", true)]);
        Add("credentials.status", "Read configured status only; credential contents are never returned.", true);
        Add("credentials.open", "Open secure desktop credential-entry UI.", fields: [("providerId", "string", false)]);
        Add("toolchain.status", "Read installed local FFmpeg status.", true);
        Add("toolchain.ensure", "Install the pinned managed FFmpeg toolchain.");
        Add("toolchain.select", "Select an existing local FFmpeg executable.", fields: [("executablePath", "string", true)]);
        Add("diagnostics.get", "Read local-control transport and active-project status without script text or secrets.", true);
        Add("control.get", "Read local desktop-control connection state.", true);
        Add("control.set", "Enable or disable local desktop control.", fields: [("enabled", "boolean", true)]);
        // Schemas for nested contracts retain their full types so generated MCP tools are useful.
        int lexicon = commands.FindIndex(item => item.Command == "lexicon.set");
        commands[lexicon] = commands[lexicon] with { InputSchema = AutomationJson.Element(new { type = "object", properties = new { entries = new { type = "array", items = new { type = "object", properties = new { written = new { type = "string" }, spoken = new { type = "string" }, matchCase = new { type = "boolean" }, wholeWord = new { type = "boolean" } }, required = new[] { "written", "spoken" }, additionalProperties = false } } }, required = new[] { "entries" }, additionalProperties = false }) };
        foreach ((string command, string property, Type type) in new (string, string, Type)[]
        {
            ("presets.save", "preset", typeof(VoicePreset)), ("writing.profile.set", "profile", typeof(WritingProfile)),
            ("assembly.configure", "audioStandard", typeof(AudioStandard)), ("writing.outline.edit", "outline", typeof(ChapterOutline)),
            ("chapter.from-draft", "draft", typeof(ChapterDraft)),
        })
        {
            int index = commands.FindIndex(item => item.Command == command);
            JsonNode schema = JsonNode.Parse(commands[index].InputSchema.GetRawText())!;
            JsonNode nested = AutomationJson.Options.GetJsonSchemaAsNode(type);
            nested["type"] = "object";
            schema["properties"]![property] = nested;
            commands[index] = commands[index] with { InputSchema = JsonSerializer.SerializeToElement(schema, AutomationJson.Options) };
        }
        commands.AddRange(AudioTimelineCapabilities.All);
        return commands.AsReadOnly();
    }
}
