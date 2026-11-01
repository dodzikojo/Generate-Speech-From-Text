using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Rendering;

namespace IssueLab.VoiceBench.Application;

public sealed record AutomationWritingProposal(Guid ProposalId, string Kind, string Status, DateTimeOffset CreatedAt,
    string ProviderId, string ModelId, string? SegmentId, string? SourceSha256, WritingAction? Action,
    JsonElement Content, string? Brief = null, string[]? ReferenceIds = null);

public sealed partial class AutomationDispatcher
{
    private async Task<object?> ExecuteWorkflowAsync(string command, JsonElement args, CancellationToken ct)
    {
        string root = RequireRoot();
        switch (command)
        {
            case "project.rename": return await UpdateAsync("Rename project", p => p with { Name = Required(args, "name") }, ct).ConfigureAwait(false);
            case "project.refresh": return await ReloadAsync(ct).ConfigureAwait(false);
            case "chapter.create": return await MutateAsync("Create chapter", () => _chapters.CreateBlankChapterAsync(root, Required(args, "title"), Optional(args, "brief"), ct), ct).ConfigureAwait(false);
            case "chapter.import": return await MutateAsync("Import chapters", () => _imports.ImportFilesAsync(root, Strings(args, "sourcePaths"), ct), ct).ConfigureAwait(false);
            case "chapter.duplicate": return await MutateAsync("Duplicate chapter", () => _chapters.DuplicateChapterAsync(root, Required(args, "chapterId"), ct), ct).ConfigureAwait(false);
            case "chapter.delete": return await MutateAsync("Delete chapter", () => _chapters.DeleteChapterAsync(root, Required(args, "chapterId"), ct), ct).ConfigureAwait(false);
            case "chapter.rename": return await MutateAsync("Rename chapter", () => _chapters.UpdateChapterAsync(root, Required(args, "chapterId"), title: Required(args, "title"), cancellationToken: ct), ct).ConfigureAwait(false);
            case "chapter.archive": case "chapter.restore": return await MutateAsync("Change chapter status", () => _chapters.UpdateChapterAsync(root, Required(args, "chapterId"), status: command == "chapter.archive" ? ChapterStatus.Archived : ChapterStatus.Active, cancellationToken: ct), ct).ConfigureAwait(false);
            case "chapter.include": return await MutateAsync("Change compilation inclusion", () => _chapters.UpdateChapterAsync(root, Required(args, "chapterId"), includedInCompilation: Bool(args, "included"), cancellationToken: ct), ct).ConfigureAwait(false);
            case "chapter.reorder": return await MutateAsync("Reorder chapter", () => _chapters.MoveChapterAsync(root, Required(args, "chapterId"), Int(args, "offset"), ct), ct).ConfigureAwait(false);
            case "chapter.configure": return await UpdateAsync("Configure chapter", p =>
            {
                ChapterDocument chapter = RequireChapter(p, args);
                string[] references = args.TryGetProperty("referenceIds", out _) ? Strings(args, "referenceIds") : chapter.ReferenceIds.ToArray();
                if (references.Any(id => p.SourceReferences.All(item => item.ReferenceId != id))) throw new AutomationException("reference.not-found", "A selected reference does not exist.");
                return p with { Chapters = p.Chapters.Select(item => item.ChapterId == chapter.ChapterId ? item with { Brief = Optional(args, "brief") ?? item.Brief, ReferenceIds = references.Distinct().ToList() } : item).ToList() };
            }, ct).ConfigureAwait(false);
            case "chapter.from-draft":
            {
                ChapterDraft draft = Deserialize<ChapterDraft>(args, "draft");
                return await MutateAsync("Create chapter from draft", () => _chapters.CreateChapterFromDraftAsync(root, draft, Optional(args, "brief"), Strings(args, "referenceIds"), ct), ct).ConfigureAwait(false);
            }
            case "segment.create": return await MutateAsync("Create segment", () => _chapters.AddSegmentAsync(root, Required(args, "chapterId"), Required(args, "title"), Optional(args, "text") ?? "", ct), ct).ConfigureAwait(false);
            case "segment.duplicate": return await MutateAsync("Duplicate segment", () => _chapters.DuplicateSegmentAsync(root, Required(args, "segmentId"), ct), ct).ConfigureAwait(false);
            case "segment.delete": return await MutateAsync("Delete segment", () => _chapters.DeleteSegmentAsync(root, Required(args, "segmentId"), ct), ct).ConfigureAwait(false);
            case "segment.rename": return await MutateAsync("Rename segment", () => _chapters.RenameSegmentAsync(root, Required(args, "segmentId"), Required(args, "title"), ct), ct).ConfigureAwait(false);
            case "segment.reorder":
            {
                SegmentDocument segment = RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args);
                return await MutateAsync("Reorder segment", () => _chapters.MoveSegmentAsync(root, segment.ChapterId, segment.SegmentId, Int(args, "offset"), ct), ct).ConfigureAwait(false);
            }
            case "segment.split":
            {
                SegmentDocument segment = RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args);
                string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, segment.ScriptRelativePath), ct).ConfigureAwait(false);
                int cursor = Int(args, "cursor");
                if (cursor < 0 || cursor > text.Length) throw new AutomationException("editor.invalid-range", "The split cursor is outside the script.");
                LiveEditorDraft.RequireBoundary(text, cursor);
                return await MutateAsync("Split segment", () => _chapters.SplitSegmentAsync(root, segment.SegmentId, cursor, Required(args, "expectedAuthoringSha256"), ct), ct).ConfigureAwait(false);
            }
            case "segment.ready": return await UpdateAsync("Mark segment ready", p => ReplaceSegment(p, RequireSegment(p, args) with { Status = SegmentStatus.Ready }), ct).ConfigureAwait(false);
            case "presets.list":
            {
                VoiceBenchProjectDocument p = await ReloadAsync(ct).ConfigureAwait(false);
                return new { presets = p.VoicePresets, p.DefaultVoicePresetId };
            }
            case "presets.save":
            {
                VoicePreset preset = Deserialize<VoicePreset>(args, "preset");
                if (!VoicePresetRules.CanSelectAsProjectDefault(preset) || string.IsNullOrWhiteSpace(preset.PresetId) || string.IsNullOrWhiteSpace(preset.Name)) throw new AutomationException("preset.invalid", "A preset requires ID, name, exact provider, model and voice.");
                if (_desktop is not null) await DesktopAsync("presets.validate", AutomationJson.Element(new { preset }), ct).ConfigureAwait(false);
                return await UpdateAsync("Save voice preset", p => p with { VoicePresets = p.VoicePresets.Where(item => item.PresetId != preset.PresetId).Append(preset).ToList() }, ct).ConfigureAwait(false);
            }
            case "presets.default": return await UpdateAsync("Choose default voice", p =>
            {
                string id = Required(args, "presetId");
                if (!p.VoicePresets.Any(item => item.PresetId == id && VoicePresetRules.CanSelectAsProjectDefault(item))) throw new AutomationException("preset.invalid", "Select an existing exact provider/model/voice preset.");
                return ProjectVoiceSelectionRules.SetProjectVoice(p, id);
            }, ct).ConfigureAwait(false);
            case "presets.assign": return await UpdateAsync("Assign voice preset", p =>
            {
                string id = Required(args, "presetId");
                if (p.VoicePresets.All(item => item.PresetId != id)) throw new AutomationException("preset.not-found", "The voice preset does not exist.");
                return ReplaceSegment(p, RequireSegment(p, args) with { VoicePresetId = id });
            }, ct).ConfigureAwait(false);
            case "presets.delete": return await UpdateAsync("Delete voice preset", p =>
            {
                string id = Required(args, "presetId");
                if (p.DefaultVoicePresetId == id || p.Segments.Any(item => item.VoicePresetId == id)) throw new AutomationException("preset.in-use", "Choose another voice for the project and segments before deleting this preset.");
                return p with { VoicePresets = p.VoicePresets.Where(item => item.PresetId != id).ToList() };
            }, ct).ConfigureAwait(false);
            case "lexicon.get": return (await ReloadAsync(ct).ConfigureAwait(false)).Lexicon;
            case "lexicon.set":
            {
                List<PronunciationEntry> entries = Deserialize<List<PronunciationEntry>>(args, "entries");
                if (entries.Any(item => string.IsNullOrWhiteSpace(item.Written) || string.IsNullOrWhiteSpace(item.Spoken))) throw new AutomationException("lexicon.invalid", "Pronunciation entries require written and spoken text.");
                return await UpdateAsync("Change pronunciation lexicon", p => p with { Lexicon = entries }, ct).ConfigureAwait(false);
            }
            case "writing.profile.get": return (await ReloadAsync(ct).ConfigureAwait(false)).WritingProfile;
            case "writing.profile.set":
            {
                WritingProfile profile = Deserialize<WritingProfile>(args, "profile");
                if (!_writing.Providers.Any(provider => provider.ProviderId == profile.DefaultProviderId && provider.Models.Any(model => model.ModelId == profile.DefaultModelId))) throw new AutomationException("writing.model-invalid", "Choose an available exact writing provider and model.");
                return await UpdateAsync("Configure writing profile", p => p with { WritingProfile = profile }, ct).ConfigureAwait(false);
            }
            case "writing.references.list": return (await ReloadAsync(ct).ConfigureAwait(false)).SourceReferences;
            case "writing.references.import": return await MutateAsync("Import writing reference", () => _chapters.ImportReferenceAsync(root, Required(args, "sourcePath"), ct), ct).ConfigureAwait(false);
            case "writing.references.remove": return await UpdateAsync("Remove writing reference", p =>
            {
                string id = Required(args, "referenceId");
                return p with { SourceReferences = p.SourceReferences.Where(item => item.ReferenceId != id).ToList(), Chapters = p.Chapters.Select(chapter => chapter with { ReferenceIds = chapter.ReferenceIds.Where(item => item != id).ToList() }).ToList() };
            }, ct).ConfigureAwait(false);
            case "writing.proposals.list": return await GetProposalsAsync(ct).ConfigureAwait(false);
            case "writing.proposals.accept": return await AcceptProposalAsync(Id(args, "proposalId"), ct).ConfigureAwait(false);
            case "writing.proposals.reject":
            {
                AutomationWritingProposal proposal = await GetProposalAsync(Id(args, "proposalId"), ct).ConfigureAwait(false);
                if (proposal.Status != "Pending") throw new AutomationException("proposal.resolved", "This proposal has already been resolved.");
                proposal = proposal with { Status = "Rejected" };
                await SaveProposalAsync(proposal, ct).ConfigureAwait(false);
                return proposal;
            }
            case "writing.outline.edit":
            {
                AutomationWritingProposal proposal = await GetProposalAsync(Id(args, "proposalId"), ct).ConfigureAwait(false);
                if (proposal.Kind != "Outline" || proposal.Status != "Pending") throw new AutomationException("proposal.invalid", "Choose a pending outline proposal.");
                ChapterOutline outline = Deserialize<ChapterOutline>(args, "outline");
                if (outline.Segments.Count == 0 || string.IsNullOrWhiteSpace(outline.Title)) throw new AutomationException("outline.invalid", "An outline requires a title and segments.");
                proposal = proposal with { Content = AutomationJson.Element(outline) };
                await SaveProposalAsync(proposal, ct).ConfigureAwait(false);
                return proposal;
            }
            case "revisions.list": return RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args).WritingRevisions;
            case "revisions.restore": return await MutateAsync("Restore writing revision", () => _chapters.RestoreRevisionAsync(root, Required(args, "segmentId"), Id(args, "revisionId"), Bool(args, "restorePreviousText"), ct), ct).ConfigureAwait(false);
            case "render.list": return RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args).RenderHistory;
            case "render.approve":
            {
                SegmentDocument selected = RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args);
                string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, selected.ScriptRelativePath), ct).ConfigureAwait(false);
                ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
                if (parsed.HasErrors) throw new AutomationException("script.invalid", "Resolve invalid production cues before approving a take.");
                return await UpdateAsync("Approve take", p =>
                {
                    SegmentDocument current = SegmentStateRules.WithSourceHashes(RequireSegment(p, args), SourceTextHasher.Compute(parsed.NarrationText), SourceTextHasher.Compute(text));
                    RenderRecord render = current.RenderHistory.SingleOrDefault(item => item.RenderId == Id(args, "renderId")) ?? throw new AutomationException("render.not-found", "The explicit take does not belong to the segment.");
                    if (render.Origin == RenderOrigin.Imported)
                    {
                        if (!Bool(args,"confirmedTranscript")) throw new AutomationException("render.transcript-confirmation-required","Confirm that the recording matches its attached source transcript before approval.");
                        render=render with { ReferenceTranscriptConfirmed=true };
                        current=current with { RenderHistory=current.RenderHistory.Select(item=>item.RenderId==render.RenderId?render:item).ToList() };
                    }
                    if (string.IsNullOrWhiteSpace(render.RawAudioRelativePath) || !File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(root, render.RawAudioRelativePath))) throw new AutomationException("render.audio-missing", "The selected take has no raw audio artifact.");
                    return ReplaceSegment(p, SegmentStateRules.ApproveRender(current, render.RenderId, DateTimeOffset.UtcNow, Bool(args, "explicitlyApproveStaleSource")));
                }, ct).ConfigureAwait(false);
            }
            case "render.pickup": return await UpdateAsync("Mark pickup required", p => ReplaceSegment(p, RequireSegment(p, args) with { Status = SegmentStatus.PickupRequired }), ct).ConfigureAwait(false);
            case "alignment.get":
            {
                RenderRecord render = RequireSegment(await ReloadAsync(ct).ConfigureAwait(false), args).RenderHistory.SingleOrDefault(item => item.RenderId == Id(args, "renderId")) ?? throw new AutomationException("render.not-found", "The take does not belong to the segment.");
                WordAlignmentRecord? latest = render.AlignmentHistory.LastOrDefault(item => item.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial);
                JsonElement? artifact = latest?.ArtifactRelativePath is string path ? await ReadJsonAsync<JsonElement>(VoiceBenchProjectLayout.ResolveRelativePath(root, path), ct).ConfigureAwait(false) : null;
                return new { records = render.AlignmentHistory, artifact };
            }
            case "assembly.get":
            {
                VoiceBenchProjectDocument p = await ReloadAsync(ct).ConfigureAwait(false);
                MasterScope scope = Scope(args);
                string? chapter = Optional(args, "chapterId");
                var eligibility = MasterEligibilityEvaluator.Evaluate(p, scope, chapter);
                ProjectValidationResult validation = await _projects.ValidateAsync(root, ct).ConfigureAwait(false);
                return new { audioStandard = p.AudioStandard, orderedSegmentIds = ProjectOrdering.GetOrderedSegmentIds(p, scope, chapter), canBuild = eligibility.CanBuild && validation.IsValid, issues = eligibility.Issues.Concat(validation.Issues).ToArray() };
            }
            case "assembly.configure":
            {
                AudioStandard standard = Deserialize<AudioStandard>(args, "audioStandard");
                if (standard.SampleRate is < 8000 or > 192000 || standard.Channels is < 1 or > 2 || standard.PcmBitsPerSample is not (16 or 24 or 32) || !double.IsFinite(standard.IntegratedLufs) || standard.IntegratedLufs is < -60 or > 0 || !double.IsFinite(standard.TruePeakDbtp) || standard.TruePeakDbtp is < -20 or > 0 || standard.PauseMilliseconds is < 0 or > 60000 || standard.ChapterPauseMilliseconds is < 0 or > 60000) throw new AutomationException("audio-standard.invalid", "The audio standard is outside supported narration ranges.");
                return await UpdateAsync("Configure audio standard", p => p with { AudioStandard = standard }, ct).ConfigureAwait(false);
            }
            case "exports.create":
                return await new ProductionExportService(_projects).ExportAsync(new(root, Required(args, "name"), Scope(args), Optional(args, "chapterId")), ct).ConfigureAwait(false);
            case "exports.list": return ListArtifacts(root, null);
            case "artifacts.list": return ListArtifacts(root, Optional(args, "relativeDirectory"));
            case "artifacts.read":
            {
                string relative = Required(args, "relativePath");
                string path = ResolveArtifact(root, relative);
                if (!new[] { ".txt", ".md", ".csv", ".json" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) throw new AutomationException("artifact.not-text", "Use playback for audio artifacts.");
                FileInfo info = new(path);
                if (info.Length > 16 * 1024 * 1024) throw new AutomationException("artifact.too-large", "The artifact exceeds the 16 MiB text source limit.");
                TextPage page = PageText(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), args);
                return new { relativePath = relative, page.Text, page.Offset, page.TotalUtf16Length, page.NextOffset };
            }
            default: throw new AutomationException("command.unavailable", "This command is unavailable in the current application context.");
        }
    }

    private static VoiceBenchProjectDocument ReplaceSegment(VoiceBenchProjectDocument project, SegmentDocument segment) => project with { Segments = project.Segments.Select(item => item.SegmentId == segment.SegmentId ? segment : item).ToList() };
    private static MasterScope Scope(JsonElement args) => Optional(args, "scope") is not string scope ? MasterScope.Compilation : Enum.TryParse(scope, true, out MasterScope value) && Enum.IsDefined(value) ? value : throw new AutomationException("scope.invalid", "Scope must be Chapter or Compilation.");

    private async Task<object?> ExecuteWritingAsync(string command, JsonElement args, CancellationToken ct)
    {
        string root = RequireRoot();
        string provider = Required(args, "providerId"), model = Required(args, "modelId");
        if (!_writing.Providers.Any(item => item.ProviderId == provider && item.Models.Any(item => item.ModelId == model))) throw new AutomationException("writing.model-invalid", "Choose an exact available writing provider and model.");
        VoiceBenchProjectDocument project = await _projects.LoadAsync(root, ct).ConfigureAwait(false);
        WritingContextLoader contexts = new(_projects);
        string? brief = Optional(args, "brief");
        string[] references = Strings(args, "referenceIds");
        string? segmentId = null, hash = null;
        WritingAction? action = null;
        object content;
        string kind;
        if (command == "writing.revise")
        {
            SegmentDocument segment = RequireSegment(project, args);
            segmentId = segment.SegmentId;
            ChapterDocument chapter = project.Chapters.Single(item => item.ChapterId == segment.ChapterId);
            brief = chapter.Brief;
            references = chapter.ReferenceIds.ToArray();
            string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, segment.ScriptRelativePath), ct).ConfigureAwait(false);
            hash = SourceTextHasher.Compute(text);
            if (!string.Equals(hash, segment.AuthoringSha256, StringComparison.OrdinalIgnoreCase)) throw new AutomationException("source.conflict", "The script changed externally; refresh and save it before requesting a proposal.");
            if (!Enum.TryParse(Required(args, "action"), true, out WritingAction parsed) || !Enum.IsDefined(parsed)) throw new AutomationException("writing.action-invalid", "Choose a supported writing action.");
            action = parsed;
            WritingContext context = await contexts.LoadAsync(root, brief ?? "", references, ct).ConfigureAwait(false);
            var request = new WritingRevisionRequest(model, parsed, text, Optional(args, "instruction"), context, EstimateWritingTokens(text, context));
            WritingProposal proposal = await _writing.ReviseAsync(provider, request, ct).ConfigureAwait(false);
            WritingProposalRules.Validate(text, proposal.ProposedText, parsed);
            content = proposal;
            kind = "Revision";
        }
        else if (command == "writing.outline")
        {
            int duration = Int(args, "targetDurationSeconds");
            if (duration <= 0) throw new AutomationException("writing.duration-invalid", "Target duration must be positive.");
            WritingContext context = await contexts.LoadAsync(root, brief ?? "", references, ct).ConfigureAwait(false);
            string goal = Required(args, "goal");
            content = await _writing.CreateOutlineAsync(provider, new(model, Required(args, "title"), goal, duration, context, EstimateWritingTokens(goal, context)), ct).ConfigureAwait(false);
            kind = "Outline";
        }
        else
        {
            AutomationWritingProposal outline = await GetProposalAsync(Id(args, "outlineId"), ct).ConfigureAwait(false);
            if (outline.Kind != "Outline" || outline.Status == "Rejected") throw new AutomationException("proposal.invalid", "Choose an available outline proposal.");
            brief ??= outline.Brief;
            if (!args.TryGetProperty("referenceIds", out _)) references = outline.ReferenceIds ?? [];
            ChapterOutline outlineContent = outline.Content.Deserialize<ChapterOutline>(AutomationJson.Options)!;
            WritingContext context = await contexts.LoadAsync(root, brief ?? "", references, ct).ConfigureAwait(false);
            content = await _writing.CreateDraftAsync(provider, new(model, outlineContent, context, EstimateWritingTokens(outline.Content.GetRawText(), context), Bool(args, "generateProductionCues", project.WritingProfile.GenerateProductionCues)), ct).ConfigureAwait(false);
            kind = "Draft";
        }
        AutomationWritingProposal saved = new(Guid.NewGuid(), kind, "Pending", DateTimeOffset.UtcNow, provider, model, segmentId, hash, action, AutomationJson.Element(content), brief, references);
        await SaveProposalAsync(saved, ct).ConfigureAwait(false);
        return saved;
    }

    private static int EstimateWritingTokens(string text, WritingContext context) => SegmentRenderService.EstimateInputTokens(text + JsonSerializer.Serialize(context, AutomationJson.Options));
    private string ProposalPath(Guid id) => Path.Combine(RequireRoot(), ".voicebench", "automation", "proposals", id.ToString("N") + ".json");
    private Task SaveProposalAsync(AutomationWritingProposal proposal, CancellationToken ct) => WriteJsonAsync(ProposalPath(proposal.ProposalId), proposal, ct);
    private Task<AutomationWritingProposal> GetProposalAsync(Guid id, CancellationToken ct) => File.Exists(ProposalPath(id)) ? ReadJsonAsync<AutomationWritingProposal>(ProposalPath(id), ct) : throw new AutomationException("proposal.not-found", "The writing proposal does not exist in this project.");
    private async Task<IReadOnlyList<AutomationWritingProposal>> GetProposalsAsync(CancellationToken ct)
    {
        string directory = Path.Combine(RequireRoot(), ".voicebench", "automation", "proposals");
        List<AutomationWritingProposal> proposals = [];
        if (Directory.Exists(directory)) foreach (string file in Directory.EnumerateFiles(directory, "*.json")) proposals.Add(await ReadJsonAsync<AutomationWritingProposal>(file, ct).ConfigureAwait(false));
        return proposals.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    private async Task<object> AcceptProposalAsync(Guid id, CancellationToken ct)
    {
        AutomationWritingProposal proposal = await GetProposalAsync(id, ct).ConfigureAwait(false);
        if (proposal.Status != "Pending") throw new AutomationException("proposal.resolved", "This proposal has already been resolved.");
        if (proposal.Kind == "Outline") throw new AutomationException("proposal.outline", "Generate a chapter draft from this outline before accepting it.");
        if (_desktop is not null) await DesktopAsync("project.prepare", AutomationJson.Element(new { }), ct).ConfigureAwait(false);
        if (proposal.Kind == "Draft")
        {
            ChapterDraft draft = proposal.Content.Deserialize<ChapterDraft>(AutomationJson.Options)!;
            await MutateAsync("Accept chapter draft", () => _chapters.CreateChapterFromDraftAsync(RequireRoot(), draft, proposal.Brief, proposal.ReferenceIds ?? [], ct), ct).ConfigureAwait(false);
        }
        else
        {
            WritingProposal revision = proposal.Content.Deserialize<WritingProposal>(AutomationJson.Options)!;
            await MutateAsync("Accept writing proposal", () => _chapters.ApplyWritingProposalAsync(RequireRoot(), proposal.SegmentId!, proposal.SourceSha256!, revision, proposal.Action!.Value, proposal.ProviderId, proposal.ModelId, ct), ct).ConfigureAwait(false);
        }
        proposal = proposal with { Status = "Accepted" };
        await SaveProposalAsync(proposal, ct).ConfigureAwait(false);
        return new { proposal, project = Project };
    }

    private static readonly string[] ArtifactDirectories = ["01-source/audio", "03-render-copies", "04-generated-audio", "05-masters", "06-exports", "07-reports"];
    public static string ResolveArtifact(string root, string relative)
    {
        string normalized = relative.Replace('\\', '/');
        if (!ArtifactDirectories.Any(directory => normalized == directory || normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))) throw new AutomationException("artifact.path-invalid", "Choose a file in the project's known output directories.");
        string path = VoiceBenchProjectLayout.ResolveRelativePath(root, relative);
        string current = path;
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (!string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new AutomationException("artifact.path-invalid", "Artifacts cannot traverse filesystem links.");
            current = Path.GetDirectoryName(current) ?? throw new AutomationException("artifact.path-invalid", "Invalid artifact path.");
        }
        return path;
    }
    private static object[] ListArtifacts(string root, string? relativeDirectory)
    {
        IEnumerable<string> directories = relativeDirectory is null ? ArtifactDirectories.Select(item => ResolveArtifact(root, item)) : [ResolveArtifact(root, relativeDirectory)];
        List<object> artifacts = [];
        foreach (string directory in directories.Where(Directory.Exists))
            foreach (string path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                FileInfo info = new(path);
                artifacts.Add(new { relativePath = Path.GetRelativePath(root, path).Replace('\\', '/'), bytes = info.Length, updatedAt = info.LastWriteTimeUtc });
            }
        return artifacts.ToArray();
    }

    private sealed record TextPage(string Text, int Offset, int TotalUtf16Length, int? NextOffset);
    private static TextPage PageText(string text, JsonElement args)
    {
        int offset = Int(args, "offset");
        int count = Int(args, "count", 32768);
        if (offset < 0 || offset > text.Length || count is < 1 or > 65536) throw new AutomationException("page.invalid", "Text offset must be within the document and count must be between 1 and 65536 UTF-16 units.");
        LiveEditorDraft.RequireBoundary(text, offset);
        int end = (int)Math.Min(text.Length, (long)offset + count);
        if (end > offset && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
        // A one-unit page beginning with a supplementary character must make progress.
        if (end == offset && end < text.Length) end = Math.Min(text.Length, end + 2);
        return new(text[offset..end], offset, text.Length, end < text.Length ? end : null);
    }
}
