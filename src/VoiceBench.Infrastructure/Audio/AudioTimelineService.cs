using System.Security.Cryptography;
using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Audio;

/// <summary>One revision-checked transaction per batch; reads never mutate project authoring state.</summary>
public sealed class AudioTimelineService(
    IProjectRepository repository,
    IAuthoringTransactionManager? transactions = null,
    TimeProvider? timeProvider = null) : IAudioTimelineService
{
    private readonly IAuthoringTransactionManager _transactions = transactions ?? new FileAuthoringTransactionManager(timeProvider);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AudioTimelineDocument> GetAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default)
    {
        var project = AudioTimelineRules.RegisterRenderAssets(await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false));
        var alignments = await LoadAlignmentsAsync(projectRoot, project, cancellationToken).ConfigureAwait(false);
        return PrepareTimeline(project, scope, chapterId, alignments);
    }

    public async Task<AudioTimelineDocument> ApplyAsync(string projectRoot, AudioTimelineScope scope, string? chapterId,
        long expectedRevision, IReadOnlyList<TimelineEdit> edits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0) return await GetAsync(projectRoot, scope, chapterId, cancellationToken).ConfigureAwait(false);
        // Alignment reads are immutable snapshots. Revision and manifest merge happen again under the disk update gate.
        var loaded = AudioTimelineRules.RegisterRenderAssets(await repository.LoadAsync(projectRoot, cancellationToken).ConfigureAwait(false));
        var alignments = await LoadAlignmentsAsync(projectRoot, loaded, cancellationToken).ConfigureAwait(false);
        AudioTimelineDocument? result = null;
        await _transactions.ExecuteAsync(projectRoot, "Edit audio timeline", async token =>
        {
            await ProjectDocumentUpdater.UpdateAsync(repository, projectRoot, current =>
            {
                current = AudioTimelineRules.RegisterRenderAssets(current);
                var timeline = PrepareTimeline(current, scope, chapterId, alignments);
                if (timeline.Revision != expectedRevision) throw new TimelineRevisionConflictException(expectedRevision, timeline.Revision);
                var changed = AudioTimelineRules.Apply(current, timeline, edits, alignments);
                result = changed with { Revision = checked(timeline.Revision + 1) };
                Dictionary<string, long> revisions = new(current.AudioTimelineRevisionCounters, StringComparer.Ordinal) { [AudioTimelineRules.RevisionKey(scope, chapterId)] = result.Revision };
                return current with { AudioTimelines = [.. current.AudioTimelines.Where(t => t.Scope != scope || t.ChapterId != chapterId), result], AudioTimelineRevisionCounters = revisions };
            }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("The timeline batch produced no result.");
    }

    public async Task<MixEligibilityResult> ValidateAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default)
    {
        var graph = await LoadGraphAsync(projectRoot, scope, chapterId, true, cancellationToken).ConfigureAwait(false);
        return new(graph.Issues);
    }

    public Task<MixRenderPlan> BuildPlanAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default) =>
        BuildPlanCoreAsync(projectRoot, scope, chapterId, true, cancellationToken);

    public Task<MixRenderPlan> BuildPreviewPlanAsync(string projectRoot, AudioTimelineScope scope, string? chapterId = null, CancellationToken cancellationToken = default) =>
        BuildPlanCoreAsync(projectRoot, scope, chapterId, false, cancellationToken);

    private async Task<MixRenderPlan> BuildPlanCoreAsync(string root, AudioTimelineScope scope, string? chapterId, bool forExport, CancellationToken token)
    {
        var graph = await LoadGraphAsync(root, scope, chapterId, forExport, token).ConfigureAwait(false);
        if (graph.Issues.Count > 0) throw new InvalidOperationException(string.Join(" ", graph.Issues.Select(i => i.Message)));
        List<MixRenderClip> clips = [];
        List<MixTranscriptEntry> transcript = [];
        Dictionary<Guid, ParsedScriptDocument> transcriptSources = [];
        List<TimelineMarker> markers = [];
        foreach (var item in graph.Timelines)
        {
            foreach (AudioClipDocument clip in item.Timeline.Clips)
            {
                AudioTrackDocument track = item.Timeline.Tracks.Single(t => t.TrackId == clip.TrackId);
                AudioAssetRecord asset = graph.Project.AudioAssets.Single(a => a.AssetId == clip.AssetId);
                double start = ResolveAnchor(clip.Anchor, clip.StartSeconds, item.OffsetSeconds, graph.ChapterOffsets, graph.Timelines, graph.Alignments);
                // Every reference is copied so a caller's live project lists cannot mutate a frozen render graph.
                var frozen = new MixRenderClip
                {
                    ClipId = clip.ClipId, Asset = asset with { }, Track = track with { Solo = false },
                    StartSeconds = start, SourceStartFrame = clip.SourceStartFrame, DurationFrames = clip.DurationFrames,
                    GainDb = clip.GainDb, Pan = clip.Pan, FadeInSeconds = clip.FadeInSeconds, FadeOutSeconds = clip.FadeOutSeconds,
                    FadeInOffsetSeconds = clip.FadeInOffsetSeconds, FadeOutOffsetSeconds = clip.FadeOutOffsetSeconds,
                    Muted = clip.Muted, Loop = clip.Loop,
                    Envelope = Array.AsReadOnly(clip.Envelope.ToArray()), RetainedWordRanges = Array.AsReadOnly(clip.RetainedWordRanges.ToArray()),
                    AlignedWords = Array.AsReadOnly(AudioTimelineRules.MatchingAlignment(clip, graph.Alignments)?.Words.ToArray() ?? []),
                };
                if (asset.AuthoringSourceRelativePath is string authoringPath && File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(root, authoringPath)))
                {
                    string authoring = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, authoringPath), token).ConfigureAwait(false);
                    frozen = frozen with { ProductionCues = Array.AsReadOnly(new ProductionScriptParser().Parse(authoring).Cues.ToArray()) };
                }
                clips.Add(frozen);
                if (clip.SegmentId is not string segmentId || clip.RenderId is not Guid renderId || clip.Muted || track.Muted) continue;
                string? snapshotPath = clip.CleanSourceRelativePath;
                if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(root, snapshotPath))) continue;
                string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, snapshotPath), token).ConfigureAwait(false);
                ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
                IReadOnlyList<RetainedWordRange> ranges = clip.RetainedWordRanges;
                bool full = clip.SourceStartFrame == 0 && clip.DurationFrames == TimelineTime.ToFrames(asset.DurationSeconds);
                if (full && parsed.NarrationWords.Count > 0) ranges = [new RetainedWordRange(0, parsed.NarrationWords.Count - 1)];
                clips[^1] = frozen with { RetainedWordRanges = Array.AsReadOnly(ranges.ToArray()) };
                transcriptSources[clip.ClipId] = parsed;
                AddTranscriptFragments(transcript, clip, segmentId, renderId, start, parsed, ranges, AudioTimelineRules.MatchingAlignment(clip, graph.Alignments));
            }
            foreach (TimelineMarker marker in item.Timeline.Markers)
            {
                try
                {
                    markers.Add(marker with { PositionSeconds = ResolveAnchor(marker.Anchor, marker.PositionSeconds, item.OffsetSeconds, graph.ChapterOffsets, graph.Timelines, graph.Alignments) });
                }
                catch (InvalidOperationException) { markers.Add(marker with { Anchor = marker.Anchor with { Placed = false } }); }
            }
        }
        var orderedClips = clips.OrderBy(c => c.StartFrame).ThenBy(c => c.Track.Order).ThenBy(c => c.ClipId).ToArray();
        double duration = graph.DurationSeconds;
        if (duration <= 0 && orderedClips.Length > 0) duration = orderedClips.Max(c => c.StartSeconds + c.DurationSeconds);
        return new MixRenderPlan
        {
            ProjectId = graph.Project.ProjectId, Scope = scope, ChapterId = chapterId, Revision = graph.Requested.Revision,
            CreatedAt = _timeProvider.GetUtcNow(), OutputStandard = graph.Project.MixOutputStandard with { },
            Clips = Array.AsReadOnly(orderedClips), Markers = Array.AsReadOnly(markers.OrderBy(m => m.PositionSeconds).ToArray()),
            Transcript = Array.AsReadOnly(CoalesceTranscript(transcript, transcriptSources).ToArray()), DurationSeconds = duration,
        };
    }

    private sealed record TimelinePlacement(AudioTimelineDocument Timeline, double OffsetSeconds);
    private sealed record LoadedGraph(VoiceBenchProjectDocument Project, AudioTimelineDocument Requested,
        IReadOnlyDictionary<Guid, WordAlignmentDocument> Alignments, List<TimelinePlacement> Timelines,
        Dictionary<string, double> ChapterOffsets, List<MixEligibilityIssue> Issues, double DurationSeconds);

    private async Task<LoadedGraph> LoadGraphAsync(string root, AudioTimelineScope scope, string? chapterId, bool forExport, CancellationToken token)
    {
        var project = AudioTimelineRules.RegisterRenderAssets(await repository.LoadAsync(root, token).ConfigureAwait(false));
        var alignments = await LoadAlignmentsAsync(root, project, token).ConfigureAwait(false);
        var requested = PrepareTimeline(project, scope, chapterId, alignments);
        List<TimelinePlacement> placements = [];
        Dictionary<string, double> chapterOffsets = new(StringComparer.Ordinal);
        double duration = 0;
        if (scope == AudioTimelineScope.Chapter)
        {
            placements.Add(new(requested, 0)); chapterOffsets[chapterId!] = 0; duration = TimelineDuration(requested);
        }
        else
        {
            var chapters = ProjectOrdering.GetOrderedChapters(project, includedOnly: true);
            foreach ((ChapterDocument chapter, int index) in chapters.Select((c, i) => (c, i)))
            {
                var timeline = PrepareTimeline(project, AudioTimelineScope.Chapter, chapter.ChapterId, alignments);
                chapterOffsets[chapter.ChapterId] = duration;
                placements.Add(new(timeline, duration)); duration += TimelineDuration(timeline);
                if (index < chapters.Count - 1) duration += project.AudioStandard.ChapterPauseMilliseconds / 1000d;
            }
            placements.Add(new(requested, 0));
            duration = requested.EndMarkerSeconds ?? Math.Max(duration, requested.Clips.Select(c =>
            {
                try { return ResolveAnchor(c.Anchor, c.StartSeconds, 0, chapterOffsets, placements, alignments) + c.DurationSeconds; }
                catch (InvalidOperationException) { return 0; }
            }).DefaultIfEmpty(0).Max());
        }
        List<MixEligibilityIssue> issues = [];
        foreach (var placement in placements)
        {
            issues.AddRange(AudioTimelineRules.Validate(project, placement.Timeline, alignments, forExport).Issues);
            foreach (AudioClipDocument clip in placement.Timeline.Clips)
            {
                try
                {
                    double anchoredStart = ResolveAnchor(clip.Anchor, clip.StartSeconds, placement.OffsetSeconds, chapterOffsets, placements, alignments);
                    if (!double.IsFinite(anchoredStart) || anchoredStart < 0) throw new InvalidOperationException("The resolved clip position must not be negative or non-finite.");
                }
                catch (InvalidOperationException exception) { issues.Add(new("timeline.anchor-unplaced", exception.Message, clip.ClipId, clip.SegmentId)); }
            }
        }
        if (forExport && scope == AudioTimelineScope.Compilation && placements.All(p => p.Timeline.Clips.Count == 0)) issues.Add(new("timeline.empty", "Place an audible clip before exporting the compilation."));
        issues.AddRange(await ValidateSourceFilesAsync(root, project, placements, forExport, token).ConfigureAwait(false));
        if (forExport && scope == AudioTimelineScope.Compilation && requested.EndMarkerSeconds is double end && placements.Where(p => p.Timeline.Scope == AudioTimelineScope.Chapter).Any(p => p.Timeline.Clips.Any(c => c.SegmentId is not null && !c.Muted && c.StartSeconds + c.DurationSeconds + p.OffsetSeconds > end))) issues.Add(new("timeline.end-cuts-narration", "Move the compilation end marker beyond all retained narration."));
        return new(project, requested, alignments, placements, chapterOffsets, issues, duration);
    }

    private static AudioTimelineDocument PrepareTimeline(VoiceBenchProjectDocument project, AudioTimelineScope scope, string? chapterId, IReadOnlyDictionary<Guid, WordAlignmentDocument> alignments)
    {
        var timeline = AudioTimelineRules.GetOrCreate(project, scope, chapterId);
        var clips = timeline.Clips.Select(c => AudioTimelineRules.WithRetainedWords(c, project, alignments)).ToList();
        var proposals = timeline.ReplacementProposals.Select(p =>
        {
            var clip = clips.FirstOrDefault(c => c.ClipId == p.ClipId);
            var asset = project.AudioAssets.FirstOrDefault(a => a.AssetId == p.ProposedAssetId);
            bool canTransfer = clip is not null && asset?.RenderId is Guid renderId
                && string.Equals(clip.SourceSha256, asset.SourceSha256, StringComparison.OrdinalIgnoreCase)
                && clip.RetainedWordRanges.Count > 0 && alignments.TryGetValue(renderId, out var alignment)
                && clip.RetainedWordRanges.All(range => Enumerable.Range(range.FirstWordOrdinal, range.LastWordOrdinal - range.FirstWordOrdinal + 1).All(ordinal => alignment.Words.Any(w => w.NarrationWordOrdinal == ordinal)));
            return p with { CanTransferCuts = canTransfer, Reason = canTransfer ? "Matching narration and aligned retained words support reviewed cut transfer." : "Review the full take; existing cuts cannot be transferred safely." };
        }).ToList();
        return timeline with { Clips = clips, ReplacementProposals = proposals };
    }

    private static double TimelineDuration(AudioTimelineDocument timeline) => timeline.EndMarkerSeconds ?? timeline.Clips.Select(c => c.StartSeconds + c.DurationSeconds).DefaultIfEmpty(0).Max();

    private static double ResolveAnchor(TimelineAnchor anchor, double localStart, double timelineOffset,
        IReadOnlyDictionary<string, double> chapterOffsets, IReadOnlyList<TimelinePlacement> placements,
        IReadOnlyDictionary<Guid, WordAlignmentDocument> alignments)
    {
        if (!anchor.Placed) throw new InvalidOperationException("Place the clip's cue anchor before playback or export.");
        if (anchor.Kind == TimelineAnchorKind.ProjectTime) return localStart + timelineOffset;
        if (anchor.Kind == TimelineAnchorKind.ChapterStart)
            return anchor.ChapterId is string id && chapterOffsets.TryGetValue(id, out double chapterStart)
                ? chapterStart + anchor.OffsetSeconds + localStart
                : throw new InvalidOperationException("The anchored chapter is not included in this mix.");
        if (anchor.SegmentId is not string segmentId || anchor.WordOrdinal is not int ordinal) throw new InvalidOperationException("The source-word cue is unplaced.");
        List<double> candidates = [];
        foreach (var placement in placements)
        foreach (var clip in placement.Timeline.Clips.Where(c => c.SegmentId == segmentId && c.RenderId is not null && !c.Muted
            && placement.Timeline.Tracks.Any(t => t.TrackId == c.TrackId && !t.Muted)))
        {
            var alignment = AudioTimelineRules.MatchingAlignment(clip, alignments);
            var word = alignment?.Words.FirstOrDefault(w => w.NarrationWordOrdinal == ordinal);
            if (word is null || word.Start.TotalSeconds < clip.SourceStartSeconds || word.End.TotalSeconds > clip.SourceStartSeconds + clip.DurationSeconds) continue;
            candidates.Add(placement.OffsetSeconds + clip.StartSeconds + word.Start.TotalSeconds - clip.SourceStartSeconds + anchor.OffsetSeconds + localStart);
        }
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1) throw new InvalidOperationException("The retained cue word has multiple audible occurrences; place its anchor explicitly.");
        throw new InvalidOperationException("The retained cue word is missing or uncertain; place its anchor explicitly.");
    }

    private static string RetainedText(ParsedScriptDocument parsed, IReadOnlyList<RetainedWordRange> ranges)
    {
        List<string> fragments = [];
        foreach (var range in ranges)
        {
            var words = parsed.NarrationWords.Where(w => w.Ordinal >= range.FirstWordOrdinal && w.Ordinal <= range.LastWordOrdinal).ToArray();
            if (words.Length == 0) continue;
            int start = words[0].NarrationStart, end = words[^1].NarrationStart + words[^1].NarrationLength;
            // Keep punctuation belonging to the last retained word, without consuming the next word.
            while (end < parsed.NarrationText.Length && !char.IsWhiteSpace(parsed.NarrationText[end])) end++;
            fragments.Add(parsed.NarrationText[start..end]);
        }
        return string.Join(" ", fragments);
    }

    private static void AddTranscriptFragments(List<MixTranscriptEntry> entries, AudioClipDocument clip, string segmentId,
        Guid renderId, double start, ParsedScriptDocument parsed, IReadOnlyList<RetainedWordRange> ranges, WordAlignmentDocument? alignment)
    {
        foreach (var range in ranges)
        {
            var words = alignment?.Words.Where(w => w.NarrationWordOrdinal >= range.FirstWordOrdinal && w.NarrationWordOrdinal <= range.LastWordOrdinal)
                .OrderBy(w => w.NarrationWordOrdinal).ToArray() ?? [];
            if (words.Length == 0)
            {
                entries.Add(new(clip.ClipId, segmentId, renderId, start, Array.AsReadOnly(new[] { range }), RetainedText(parsed, [range])));
                continue;
            }
            // Unknown words remain attached to their retained neighbouring word. Their source
            // gaps are conservatively occupied speech, so another clip cannot interleave there.
            for (int index = 0; index < words.Length; index++)
            {
                var retained = new RetainedWordRange(index == 0 ? range.FirstWordOrdinal : words[index].NarrationWordOrdinal,
                    index + 1 < words.Length ? words[index + 1].NarrationWordOrdinal - 1 : range.LastWordOrdinal);
                double audibleStart = start + words[index].Start.TotalSeconds - clip.SourceStartSeconds;
                entries.Add(new(clip.ClipId, segmentId, renderId, audibleStart, Array.AsReadOnly(new[] { retained }), RetainedText(parsed, [retained])));
            }
        }
    }

    private static List<MixTranscriptEntry> CoalesceTranscript(IEnumerable<MixTranscriptEntry> fragments, IReadOnlyDictionary<Guid, ParsedScriptDocument> sources)
    {
        List<MixTranscriptEntry> result = [];
        foreach (var entry in fragments.OrderBy(e => e.StartSeconds))
        {
            if (result.Count > 0 && result[^1] is var previous && previous.ClipId == entry.ClipId
                && previous.WordRanges.Count == 1 && entry.WordRanges.Count == 1
                && previous.WordRanges[0].LastWordOrdinal + 1 == entry.WordRanges[0].FirstWordOrdinal)
            {
                var range = new RetainedWordRange(previous.WordRanges[0].FirstWordOrdinal, entry.WordRanges[0].LastWordOrdinal);
                result[^1] = previous with { WordRanges = Array.AsReadOnly(new[] { range }), Text = RetainedText(sources[entry.ClipId], [range]) };
            }
            else result.Add(entry);
        }
        return result;
    }

    private static async Task<Dictionary<Guid, WordAlignmentDocument>> LoadAlignmentsAsync(string root, VoiceBenchProjectDocument project, CancellationToken token)
    {
        Dictionary<Guid, WordAlignmentDocument> result = [];
        foreach (RenderRecord render in project.Segments.SelectMany(s => s.RenderHistory))
        {
            AudioAssetRecord? pinned = project.AudioAssets.FirstOrDefault(a => a.RenderId == render.RenderId);
            string audioHash = pinned?.Sha256 ?? render.AudioSha256;
            double duration = pinned?.DurationSeconds ?? render.Duration.TotalSeconds;
            WordAlignmentRecord? record = render.AlignmentHistory.LastOrDefault(a => a.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial && a.AudioSha256.Equals(audioHash, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(a.ArtifactRelativePath));
            if (record?.ArtifactRelativePath is not string path) continue;
            try
            {
                await using FileStream stream = File.OpenRead(VoiceBenchProjectLayout.ResolveRelativePath(root, path));
                var document = await JsonSerializer.DeserializeAsync<WordAlignmentDocument>(stream, ProjectJson.SerializerOptions, token).ConfigureAwait(false);
                if (document is null || document.RenderId != render.RenderId || document.AlignmentId != record.AlignmentId || !document.AudioSha256.Equals(audioHash, StringComparison.OrdinalIgnoreCase) || !document.SourceSha256.Equals(render.SourceSha256, StringComparison.OrdinalIgnoreCase) || !document.SubmittedTextSha256.Equals(render.SubmittedTextSha256, StringComparison.OrdinalIgnoreCase)) continue;
                var ordered = document.Words.OrderBy(w => w.NarrationWordOrdinal).ToArray();
                if (ordered.Any(w => w.NarrationWordOrdinal < 0 || w.NarrationWordOrdinal >= document.TotalWordCount || w.Start < TimeSpan.Zero || w.End <= w.Start || w.End.TotalSeconds > duration) || ordered.Select(w => w.NarrationWordOrdinal).Distinct().Count() != ordered.Length || ordered.Zip(ordered.Skip(1), (left, right) => left.End > right.Start).Any(overlap => overlap)) continue;
                result[render.RenderId] = document;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException) { /* Missing/bad alignment never fabricates safe boundaries. */ }
        }
        return result;
    }

    private static async Task<IReadOnlyList<MixEligibilityIssue>> ValidateSourceFilesAsync(string root, VoiceBenchProjectDocument project, IReadOnlyList<TimelinePlacement> placements, bool forExport, CancellationToken token)
    {
        List<MixEligibilityIssue> issues = [];
        HashSet<Guid> checkedAssets = [];
        HashSet<string> checkedSources = new(StringComparer.Ordinal);
        foreach (AudioClipDocument clip in placements.SelectMany(p => p.Timeline.Clips))
        {
            AudioAssetRecord? asset = project.AudioAssets.FirstOrDefault(a => a.AssetId == clip.AssetId);
            if (asset is null) continue;
            if (checkedAssets.Add(asset.AssetId))
            {
                try
                {
                    await using FileStream stream = File.OpenRead(VoiceBenchProjectLayout.ResolveRelativePath(root, asset.RelativePath));
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                    if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) issues.Add(new("timeline.asset-changed", "The source audio hash changed; import an explicit replacement.", clip.ClipId, clip.SegmentId));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { issues.Add(new("timeline.asset-unavailable", "The pinned source audio is unavailable.", clip.ClipId, clip.SegmentId)); }
            }
            if (!forExport || clip.SegmentId is not string segmentId) continue;
            if (checkedSources.Add(segmentId))
            {
                SegmentDocument? segment = project.Segments.FirstOrDefault(s => s.SegmentId == segmentId);
                if (segment is not null)
                {
                    try
                    {
                        string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, segment.ScriptRelativePath), token).ConfigureAwait(false);
                        string hash = SourceTextHasher.Compute(new ProductionScriptParser().Parse(text).NarrationText);
                        if (!hash.Equals(segment.SourceSha256, StringComparison.OrdinalIgnoreCase)) issues.Add(new("timeline.source-changed", "Canonical narration changed on disk; review and render the new revision.", clip.ClipId, segmentId));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException) { issues.Add(new("timeline.source-unavailable", "The canonical narration script is unavailable.", clip.ClipId, segmentId)); }
                }
            }
            if (clip.CleanSourceRelativePath is string clean)
            {
                try
                {
                    string text = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, clean), token).ConfigureAwait(false);
                    if (!SourceTextHasher.Compute(new ProductionScriptParser().Parse(text).NarrationText).Equals(clip.SourceSha256, StringComparison.OrdinalIgnoreCase)) issues.Add(new("timeline.snapshot-changed", "The immutable clean narration snapshot changed.", clip.ClipId, segmentId));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException) { issues.Add(new("timeline.snapshot-unavailable", "The pinned clean narration snapshot is unavailable.", clip.ClipId, segmentId)); }
            }
        }
        return issues;
    }
}
