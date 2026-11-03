using System.Security.Cryptography;
using System.Text;

namespace IssueLab.VoiceBench.Core;

/// <summary>Pure authoring and narration-safety rules, shared by every timeline client.</summary>
public static class AudioTimelineRules
{
    public static string RevisionKey(AudioTimelineScope scope, string? chapterId) => $"{scope}:{chapterId}";
    public static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    public static VoiceBenchProjectDocument RegisterRenderAssets(VoiceBenchProjectDocument project)
    {
        List<AudioAssetRecord> assets = [.. project.AudioAssets];
        foreach (SegmentDocument segment in project.Segments)
        foreach (RenderRecord render in segment.RenderHistory)
        {
            if (assets.Any(a => a.RenderId == render.RenderId) || string.IsNullOrWhiteSpace(render.RawAudioRelativePath)) continue;
            assets.Add(new AudioAssetRecord
            {
                AssetId = render.RenderId, RenderId = render.RenderId, SegmentId = segment.SegmentId,
                Name = $"{segment.Title} · take {render.TakeNumber}",
                RelativePath = string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath) ? render.RawAudioRelativePath : render.ProcessedAudioRelativePath, Sha256 = render.AudioSha256,
                SourceSha256 = render.SourceSha256, CleanSourceRelativePath = render.CleanSourceRelativePath,
                AuthoringSourceRelativePath = render.AuthoringSourceRelativePath, AuthoringSourceSha256 = render.AuthoringSourceSha256,
                DurationSeconds = render.Duration.TotalSeconds, SampleRate = render.SampleRate, Channels = render.Channels,
                Origin = render.Origin == RenderOrigin.Imported ? AudioAssetOrigin.ImportedRecording : AudioAssetOrigin.GeneratedTake,
                CreatedAt = render.CreatedAt,
            });
        }
        return project with { AudioAssets = assets };
    }

    public static AudioTimelineDocument GetOrCreate(VoiceBenchProjectDocument project, AudioTimelineScope scope, string? chapterId = null)
    {
        if (scope == AudioTimelineScope.Chapter) ProjectOrdering.RequireChapter(project, chapterId ?? throw new ArgumentException("A chapter ID is required.", nameof(chapterId)));
        else if (chapterId is not null) throw new ArgumentException("Compilation timelines do not take a chapter ID.", nameof(chapterId));
        project = RegisterRenderAssets(project);
        AudioTimelineDocument timeline = project.AudioTimelines.SingleOrDefault(t => t.Scope == scope && t.ChapterId == chapterId)
            ?? new AudioTimelineDocument
            {
                TimelineId = StableId($"{project.ProjectId:N}/{scope}/{chapterId}"), Scope = scope, ChapterId = chapterId,
                Tracks = scope == AudioTimelineScope.Chapter
                    ? [new AudioTrackDocument { TrackId = StableId($"{project.ProjectId:N}/{chapterId}/narration"), Name = "Narration", Kind = AudioTrackKind.Narration }]
                    : [],
            };
        if (project.AudioTimelineRevisionCounters.TryGetValue(RevisionKey(scope, chapterId), out long revision) && revision > timeline.Revision)
            timeline = timeline with { Revision = revision };
        if (scope != AudioTimelineScope.Chapter) return timeline;
        ChapterDocument chapter = ProjectOrdering.RequireChapter(project, chapterId!);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(s => s.SegmentId, StringComparer.Ordinal);
        List<AudioClipDocument> clips = [.. timeline.Clips];
        List<string> placeholders = [];
        AudioTrackDocument? track = timeline.Tracks.FirstOrDefault(t => t.Kind == AudioTrackKind.Narration);
        long cursor = 0;
        foreach (string segmentId in chapter.SegmentOrder)
        {
            if (timeline.OmittedSegmentIds.Contains(segmentId, StringComparer.Ordinal)) continue;
            var existing = clips.Where(c => c.SegmentId == segmentId).ToArray();
            if (existing.Length > 0)
            {
                cursor = Math.Max(cursor, existing.Max(c => checked(c.StartFrame + c.DurationFrames)) + TimelineTime.ToFrames(project.AudioStandard.PauseMilliseconds / 1000d));
                continue;
            }
            if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
            AudioAssetRecord? asset = segment.ApprovedRenderId is Guid approved ? project.AudioAssets.FirstOrDefault(a => a.RenderId == approved) : null;
            if (timeline.LayoutMode == TimelineLayoutMode.Automatic && track is not null && asset is not null && asset.DurationSeconds > 0)
            {
                clips.Add(new AudioClipDocument
                {
                    ClipId = StableId($"{timeline.TimelineId:N}/{segmentId}"), TrackId = track.TrackId, AssetId = asset.AssetId,
                    AssetSha256 = asset.Sha256, RenderId = asset.RenderId, SegmentId = segmentId,
                    SourceSha256 = asset.SourceSha256, CleanSourceRelativePath = asset.CleanSourceRelativePath,
                    Name = segment.Title, StartFrame = cursor, DurationSeconds = asset.DurationSeconds,
                });
                cursor = checked(cursor + TimelineTime.ToFrames(asset.DurationSeconds) + TimelineTime.ToFrames(project.AudioStandard.PauseMilliseconds / 1000d));
            }
            else placeholders.Add(segmentId);
        }
        List<TakeReplacementProposal> proposals = [.. timeline.ReplacementProposals];
        foreach (AudioClipDocument clip in clips.Where(c => c.SegmentId is not null && c.RenderId is not null))
        {
            if (!segments.TryGetValue(clip.SegmentId!, out SegmentDocument? segment) || segment.ApprovedRenderId is not Guid approved || approved == clip.RenderId) continue;
            AudioAssetRecord? replacement = project.AudioAssets.FirstOrDefault(a => a.RenderId == approved);
            if (replacement is null || proposals.Any(p => p.ClipId == clip.ClipId && p.ProposedRenderId == approved)) continue;
            Guid proposalId = StableId($"{clip.ClipId:N}/{approved:N}");
            if (timeline.RejectedReplacementProposalIds.Contains(proposalId)) continue;
            proposals.Add(new TakeReplacementProposal
            {
                ProposalId = proposalId, ClipId = clip.ClipId, CurrentRenderId = clip.RenderId!.Value,
                ProposedAssetId = replacement.AssetId, ProposedRenderId = approved,
                CanTransferCuts = false, Reason = "Review the replacement and its aligned word boundaries before accepting it.",
            });
        }
        return timeline with { Clips = clips, PlaceholderSegmentIds = placeholders, ReplacementProposals = proposals };
    }

    public static AudioTimelineDocument Apply(VoiceBenchProjectDocument project, AudioTimelineDocument timeline,
        IReadOnlyList<TimelineEdit> edits, IReadOnlyDictionary<Guid, WordAlignmentDocument>? alignments = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        AudioTimelineDocument next = timeline with
        {
            Tracks = [.. timeline.Tracks], Clips = [.. timeline.Clips], Markers = [.. timeline.Markers],
            OmittedSegmentIds = [.. timeline.OmittedSegmentIds], PlaceholderSegmentIds = [.. timeline.PlaceholderSegmentIds],
            ReplacementProposals = [.. timeline.ReplacementProposals],
            RejectedReplacementProposalIds = [.. timeline.RejectedReplacementProposalIds],
        };
        foreach (TimelineEdit edit in edits)
        {
            AudioClipDocument RequireClip() => next.Clips.SingleOrDefault(c => c.ClipId == edit.ClipId)
                ?? throw new InvalidOperationException("The clip does not exist.");
            AudioTrackDocument RequireTrack(Guid? trackId = null) => next.Tracks.SingleOrDefault(t => t.TrackId == (trackId ?? edit.TrackId))
                ?? throw new InvalidOperationException("The track does not exist.");
            void Unlocked(Guid trackId) { if (RequireTrack(trackId).Locked) throw new InvalidOperationException("Unlock the track before editing its clips."); }
            void ReplaceClip(AudioClipDocument clip)
            {
                int index = next.Clips.FindIndex(c => c.ClipId == clip.ClipId);
                next.Clips[index] = clip;
            }
            switch (edit.Operation)
            {
                case TimelineEditOperation.AddTrack:
                    AudioTrackDocument addedTrack = edit.Track ?? throw new InvalidOperationException("A track is required.");
                    if (addedTrack.Solo) throw new InvalidOperationException("Solo is a monitoring setting, not an authoring edit.");
                    if (next.Tracks.Any(t => t.TrackId == addedTrack.TrackId)) throw new InvalidOperationException("Track IDs must be unique.");
                    next.Tracks.Add(addedTrack);
                    break;
                case TimelineEditOperation.UpdateTrack:
                    var updatedTrack = edit.Track ?? throw new InvalidOperationException("A track is required.");
                    if (RequireTrack(updatedTrack.TrackId).Solo != updatedTrack.Solo) throw new InvalidOperationException("Solo is a monitoring setting, not an authoring edit.");
                    next.Tracks[next.Tracks.FindIndex(t => t.TrackId == updatedTrack.TrackId)] = updatedTrack;
                    break;
                case TimelineEditOperation.DeleteTrack:
                    var deletedTrack = RequireTrack(); Unlocked(deletedTrack.TrackId);
                    next.Tracks.Remove(deletedTrack); next.Clips.RemoveAll(c => c.TrackId == deletedTrack.TrackId);
                    break;
                case TimelineEditOperation.ReorderTrack:
                    var reorderedTrack = RequireTrack();
                    next.Tracks[next.Tracks.IndexOf(reorderedTrack)] = reorderedTrack with { Order = edit.Order ?? throw new InvalidOperationException("An order is required.") };
                    break;
                case TimelineEditOperation.AddClip:
                    var addedClip = edit.Clip ?? throw new InvalidOperationException("A clip is required.");
                    Unlocked(addedClip.TrackId);
                    if (next.Clips.Any(c => c.ClipId == addedClip.ClipId)) throw new InvalidOperationException("Clip IDs must be unique.");
                    next.Clips.Add(WithRetainedWords(addedClip, project, alignments));
                    if (addedClip.SegmentId is string addedSegment) { next.PlaceholderSegmentIds.Remove(addedSegment); next.OmittedSegmentIds.Remove(addedSegment); }
                    break;
                case TimelineEditOperation.UpdateClip:
                    var update = edit.Clip ?? throw new InvalidOperationException("A clip is required.");
                    var previous = next.Clips.SingleOrDefault(c => c.ClipId == update.ClipId) ?? throw new InvalidOperationException("The clip does not exist.");
                    Unlocked(previous.TrackId); Unlocked(update.TrackId);
                    if (previous.AssetId != update.AssetId || previous.RenderId != update.RenderId || previous.AssetSha256 != update.AssetSha256 || previous.SourceSha256 != update.SourceSha256 || previous.CleanSourceRelativePath != update.CleanSourceRelativePath)
                        throw new InvalidOperationException("Source pins can only change through a reviewed replacement.");
                    if (previous.SourceStartFrame != update.SourceStartFrame || previous.DurationFrames != update.DurationFrames)
                    {
                        var preserved = PreserveFadePhase(previous, update);
                        update = update with
                        {
                            FadeInOffsetSeconds = preserved.FadeInOffsetSeconds,
                            FadeOutOffsetSeconds = preserved.FadeOutOffsetSeconds,
                            Envelope = update.Envelope.SequenceEqual(previous.Envelope)
                                ? SliceEnvelope(previous, update.SourceStartSeconds - previous.SourceStartSeconds, update.SourceStartSeconds - previous.SourceStartSeconds + update.DurationSeconds)
                                : update.Envelope,
                        };
                    }
                    if (previous.FadeInSeconds != update.FadeInSeconds) update = update with { FadeInOffsetSeconds = 0 };
                    if (previous.FadeOutSeconds != update.FadeOutSeconds) update = update with { FadeOutOffsetSeconds = 0 };
                    ReplaceClip(WithRetainedWords(update, project, alignments));
                    break;
                case TimelineEditOperation.DeleteClip:
                    var deleted = RequireClip(); Unlocked(deleted.TrackId); next.Clips.Remove(deleted);
                    break;
                case TimelineEditOperation.MoveClip:
                    var moved = RequireClip(); Unlocked(moved.TrackId); Unlocked(edit.TrackId ?? moved.TrackId);
                    ReplaceClip(moved with { StartSeconds = edit.StartSeconds ?? moved.StartSeconds, TrackId = edit.TrackId ?? moved.TrackId });
                    break;
                case TimelineEditOperation.TrimClip:
                    var trimmed = RequireClip(); Unlocked(trimmed.TrackId);
                    var trimmedRange = trimmed with
                    {
                        SourceStartSeconds = edit.SourceStartSeconds ?? trimmed.SourceStartSeconds,
                        DurationSeconds = edit.DurationSeconds ?? trimmed.DurationSeconds,
                        StartSeconds = edit.StartSeconds ?? trimmed.StartSeconds,
                    };
                    double envelopeStart = trimmedRange.SourceStartSeconds - trimmed.SourceStartSeconds;
                    ReplaceClip(WithRetainedWords(PreserveFadePhase(trimmed, trimmedRange) with
                    {
                        Envelope = SliceEnvelope(trimmed, envelopeStart, envelopeStart + trimmedRange.DurationSeconds),
                    }, project, alignments));
                    break;
                case TimelineEditOperation.SplitClip:
                    var split = RequireClip(); Unlocked(split.TrackId);
                    long offsetFrames = checked(TimelineTime.ToFrames(edit.SplitSeconds ?? throw new InvalidOperationException("A split position is required.")) - split.StartFrame);
                    if (offsetFrames <= 0 || offsetFrames >= split.DurationFrames) throw new InvalidOperationException("Split inside the clip.");
                    double offset = TimelineTime.ToSeconds(offsetFrames);
                    ReplaceClip(WithRetainedWords(PreserveFadePhase(split, split with { DurationFrames = offsetFrames }) with
                    { Envelope = SliceEnvelope(split, 0, offset) }, project, alignments));
                    next.Clips.Add(WithRetainedWords(PreserveFadePhase(split, split with
                    {
                        ClipId = edit.NewClipId ?? Guid.NewGuid(), StartFrame = checked(split.StartFrame + offsetFrames),
                        SourceStartFrame = checked(split.SourceStartFrame + offsetFrames), DurationFrames = split.DurationFrames - offsetFrames,
                    }) with { Envelope = SliceEnvelope(split, offset, split.DurationSeconds) }, project, alignments));
                    break;
                case TimelineEditOperation.DuplicateClip:
                    var duplicated = RequireClip(); Unlocked(duplicated.TrackId); Unlocked(edit.TrackId ?? duplicated.TrackId);
                    next.Clips.Add(duplicated with { ClipId = edit.NewClipId ?? Guid.NewGuid(), StartSeconds = edit.StartSeconds ?? duplicated.StartSeconds + duplicated.DurationSeconds, TrackId = edit.TrackId ?? duplicated.TrackId, Envelope = [.. duplicated.Envelope], RetainedWordRanges = [.. duplicated.RetainedWordRanges] });
                    break;
                case TimelineEditOperation.CloseGap:
                    var gap = RequireClip(); Unlocked(gap.TrackId);
                    double previousEnd = next.Clips.Where(c => c.TrackId == gap.TrackId && c.ClipId != gap.ClipId && c.StartSeconds + c.DurationSeconds <= gap.StartSeconds).Select(c => c.StartSeconds + c.DurationSeconds).DefaultIfEmpty(0).Max();
                    double shift = gap.StartSeconds - previousEnd;
                    foreach (var following in next.Clips.Where(c => c.TrackId == gap.TrackId && c.StartSeconds >= gap.StartSeconds).ToArray()) ReplaceClip(following with { StartSeconds = following.StartSeconds - shift });
                    break;
                case TimelineEditOperation.SetEnvelope:
                    var enveloped = RequireClip(); Unlocked(enveloped.TrackId);
                    ReplaceClip(enveloped with { Envelope = [.. edit.Envelope ?? []] });
                    break;
                case TimelineEditOperation.SetMarker:
                    var marker = edit.Marker ?? throw new InvalidOperationException("A marker is required.");
                    next.Markers.RemoveAll(m => m.MarkerId == marker.MarkerId); next.Markers.Add(marker);
                    break;
                case TimelineEditOperation.DeleteMarker: next.Markers.RemoveAll(m => m.MarkerId == edit.MarkerId); break;
                case TimelineEditOperation.SetEndMarker: next = next with { EndMarkerSeconds = edit.DurationSeconds ?? edit.StartSeconds }; break;
                case TimelineEditOperation.OmitSegment:
                    string omitted = edit.SegmentId ?? throw new InvalidOperationException("A segment is required.");
                    foreach (var clip in next.Clips.Where(c => c.SegmentId == omitted)) Unlocked(clip.TrackId);
                    next.Clips.RemoveAll(c => c.SegmentId == omitted); next.PlaceholderSegmentIds.Remove(omitted);
                    if (!next.OmittedSegmentIds.Contains(omitted, StringComparer.Ordinal)) next.OmittedSegmentIds.Add(omitted);
                    break;
                case TimelineEditOperation.IncludeSegment:
                    string included = edit.SegmentId ?? throw new InvalidOperationException("A segment is required.");
                    next.OmittedSegmentIds.Remove(included); if (!next.Clips.Any(c => c.SegmentId == included)) next.PlaceholderSegmentIds.Add(included);
                    break;
                case TimelineEditOperation.SetLayoutMode:
                    next = next with { LayoutMode = edit.LayoutMode ?? throw new InvalidOperationException("A layout mode is required.") }; break;
                case TimelineEditOperation.RejectReplacement:
                    if (edit.ProposalId is Guid rejectedId && !next.RejectedReplacementProposalIds.Contains(rejectedId)) next.RejectedReplacementProposalIds.Add(rejectedId);
                    next.ReplacementProposals.RemoveAll(p => p.ProposalId == edit.ProposalId); break;
                case TimelineEditOperation.AcceptReplacement:
                    if (!edit.ConfirmedReview) throw new InvalidOperationException("Review the replacement take before accepting it.");
                    var proposal = next.ReplacementProposals.SingleOrDefault(p => p.ProposalId == edit.ProposalId) ?? throw new InvalidOperationException("The replacement proposal does not exist.");
                    var replaced = next.Clips.Single(c => c.ClipId == proposal.ClipId); Unlocked(replaced.TrackId);
                    var asset = project.AudioAssets.Single(a => a.AssetId == proposal.ProposedAssetId);
                    var originalAsset = project.AudioAssets.Single(a => a.AssetId == replaced.AssetId);
                    bool hasCuts = replaced.SourceStartFrame != 0 || replaced.DurationFrames != TimelineTime.ToFrames(originalAsset.DurationSeconds);
                    if (hasCuts && (!proposal.CanTransferCuts || !string.Equals(asset.SourceSha256, replaced.SourceSha256, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("These cuts cannot be transferred; place the reviewed full replacement take instead.");
                    var replacementClip = replaced with { AssetId = asset.AssetId, AssetSha256 = asset.Sha256, RenderId = asset.RenderId, SourceSha256 = asset.SourceSha256, CleanSourceRelativePath = asset.CleanSourceRelativePath, DurationSeconds = asset.DurationSeconds };
                    if (hasCuts)
                    {
                        var oldAlignment = MatchingAlignment(replaced, alignments);
                        var newAlignment = MatchingAlignment(replacementClip, alignments);
                        double mappedStart = MapBoundary(oldAlignment, newAlignment, replaced.SourceStartSeconds, originalAsset.DurationSeconds, asset.DurationSeconds);
                        double mappedEnd = MapBoundary(oldAlignment, newAlignment, replaced.SourceStartSeconds + replaced.DurationSeconds, originalAsset.DurationSeconds, asset.DurationSeconds);
                        replacementClip = replacementClip with { SourceStartSeconds = mappedStart, DurationSeconds = mappedEnd - mappedStart };
                    }
                    ReplaceClip(WithRetainedWords(replacementClip, project, alignments));
                    next.ReplacementProposals.Remove(proposal);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(edits), "Unsupported timeline edit.");
            }
            if (edit.Operation is not (TimelineEditOperation.SetLayoutMode or TimelineEditOperation.SetMarker or TimelineEditOperation.DeleteMarker or TimelineEditOperation.RejectReplacement or TimelineEditOperation.AddTrack or TimelineEditOperation.UpdateTrack or TimelineEditOperation.ReorderTrack)) next = next with { LayoutMode = TimelineLayoutMode.Manual };
        }
        MixEligibilityResult validation = Validate(project, next, alignments, false);
        if (validation.Issues.Count > 0) throw new InvalidOperationException(string.Join(" ", validation.Issues.Select(i => i.Message)));
        return next;
    }

    public static MixEligibilityResult Validate(VoiceBenchProjectDocument project, AudioTimelineDocument timeline,
        IReadOnlyDictionary<Guid, WordAlignmentDocument>? alignments = null, bool forExport = true)
    {
        List<MixEligibilityIssue> issues = [];
        void Issue(string code, string message, AudioClipDocument? clip = null) => issues.Add(new(code, message, clip?.ClipId, clip?.SegmentId));
        if (timeline.Tracks.Select(t => t.TrackId).Distinct().Count() != timeline.Tracks.Count || timeline.Clips.Select(c => c.ClipId).Distinct().Count() != timeline.Clips.Count) Issue("timeline.duplicate-id", "Timeline track and clip IDs must be unique.");
        if (timeline.EndMarkerFrame is < 0) Issue("timeline.invalid-end", "The end marker must not be negative.");
        foreach (AudioTrackDocument track in timeline.Tracks)
        {
            if (!ValidGain(track.GainDb) || !ValidPan(track.Pan) || !double.IsFinite(track.DuckDepthDb) || track.DuckDepthDb < 0 || !double.IsFinite(track.DuckAttackSeconds) || track.DuckAttackSeconds < 0 || !double.IsFinite(track.DuckReleaseSeconds) || track.DuckReleaseSeconds < 0) Issue("timeline.invalid-track", "Track gain, pan and duck settings must be finite and within range.");
            if (timeline.Scope == AudioTimelineScope.Compilation && track.Kind == AudioTrackKind.Narration) Issue("timeline.compilation-narration", "Compilation narration is owned by chapter mixes.");
        }
        List<(double Start, double End, AudioClipDocument Clip)> spoken = [];
        foreach (AudioClipDocument clip in timeline.Clips)
        {
            AudioTrackDocument? track = timeline.Tracks.FirstOrDefault(t => t.TrackId == clip.TrackId);
            AudioAssetRecord? asset = project.AudioAssets.FirstOrDefault(a => a.AssetId == clip.AssetId);
            if (track is null) { Issue("timeline.track-missing", "The clip track is missing.", clip); continue; }
            if (asset is null) { Issue("timeline.asset-missing", "The pinned clip asset is missing.", clip); continue; }
            if (!string.Equals(clip.AssetSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase) || clip.RenderId != asset.RenderId || clip.SegmentId != asset.SegmentId || !string.Equals(clip.SourceSha256, asset.SourceSha256, StringComparison.OrdinalIgnoreCase) || clip.CleanSourceRelativePath != asset.CleanSourceRelativePath) Issue("timeline.asset-pin-mismatch", "The clip must pin the exact immutable asset, take, source and clean snapshot.", clip);
            if (clip.StartFrame < 0 || clip.SourceStartFrame < 0 || clip.DurationFrames <= 0 || !double.IsFinite(asset.DurationSeconds) || asset.DurationSeconds <= 0 || clip.SourceStartSeconds >= asset.DurationSeconds || (!clip.Loop && clip.SourceStartSeconds + clip.DurationSeconds > asset.DurationSeconds + 1d / TimelineTime.SampleRate)) Issue("timeline.invalid-range", "The clip range must stay inside its source audio.", clip);
            if (!ValidGain(clip.GainDb) || !ValidPan(clip.Pan) || !ValidFades(clip, asset) || !ValidEnvelope(clip)) Issue("timeline.invalid-envelope", "Clip gain, pan, fades and envelope must be valid for the original source range.", clip);
            bool narration = clip.SegmentId is not null || track.Kind == AudioTrackKind.Narration;
            if (!narration) continue;
            if (track.Kind != AudioTrackKind.Narration || timeline.Scope != AudioTimelineScope.Chapter) Issue("timeline.narration-owner", "Narration belongs on a chapter narration track.", clip);
            if (clip.Anchor.Kind != TimelineAnchorKind.ProjectTime) Issue("timeline.narration-anchor", "Place narration explicitly on its chapter timeline.", clip);
            if (clip.Loop) Issue("timeline.narration-loop", "Narration cannot be looped; duplicate an explicit word-safe clip.", clip);
            var alignment = MatchingAlignment(clip, alignments);
            bool startCut = clip.SourceStartFrame != 0;
            bool endCut = clip.SourceStartFrame + clip.DurationFrames != TimelineTime.ToFrames(asset.DurationSeconds);
            if ((startCut && !CanCutAt(alignment, clip.SourceStartSeconds, asset.DurationSeconds)) || (endCut && !CanCutAt(alignment, clip.SourceStartSeconds + clip.DurationSeconds, asset.DurationSeconds))) Issue("timeline.unsafe-word-cut", "Spoken cuts require reliable aligned word boundaries.", clip);
            if (!clip.Muted && !track.Muted)
            foreach (var range in SpokenRanges(alignment, asset.DurationSeconds))
            {
                double start = Math.Max(range.Start, clip.SourceStartSeconds), end = Math.Min(range.End, clip.SourceStartSeconds + clip.DurationSeconds);
                if (end > start) spoken.Add((clip.StartSeconds + start - clip.SourceStartSeconds, clip.StartSeconds + end - clip.SourceStartSeconds, clip));
            }
            if (!forExport || clip.Muted || track.Muted) continue;
            SegmentDocument? segment = project.Segments.FirstOrDefault(s => s.SegmentId == clip.SegmentId);
            RenderRecord? render = segment?.RenderHistory.FirstOrDefault(r => r.RenderId == clip.RenderId);
            if (segment is null || render is null) Issue("timeline.take-missing", "The pinned narration take is missing.", clip);
            else
            {
                if (render.Status != RenderStatus.Approved || render.ApprovedAt is null && segment.ApprovedRenderId != render.RenderId) Issue("timeline.take-unapproved", "Approve the pinned narration take before exporting.", clip);
                if (!string.Equals(clip.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase)) Issue("timeline.narration-stale", "Narration changed after this clip was rendered.", clip);
                bool currentArtifact = asset.RelativePath == (string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath) ? render.RawAudioRelativePath : render.ProcessedAudioRelativePath)
                    && string.Equals(render.AudioSha256, asset.Sha256, StringComparison.OrdinalIgnoreCase);
                // Processing creates another artifact of the same take. Existing raw pins keep
                // their original hash and are still verified against the original file on disk.
                bool pinnedOriginal = !string.IsNullOrWhiteSpace(render.ProcessedAudioRelativePath) && asset.RelativePath == render.RawAudioRelativePath;
                if ((!currentArtifact && !pinnedOriginal) || !string.Equals(render.SourceSha256, clip.SourceSha256, StringComparison.OrdinalIgnoreCase) || render.CleanSourceRelativePath != clip.CleanSourceRelativePath) Issue("timeline.take-pin-mismatch", "The source asset no longer matches its pinned take.", clip);
                if (render.Origin == RenderOrigin.Imported && !render.ReferenceTranscriptConfirmed) Issue("timeline.transcript-unconfirmed", "Confirm the imported recording's reference transcript before export.", clip);
                if (string.IsNullOrWhiteSpace(clip.CleanSourceRelativePath)) Issue("timeline.snapshot-missing", "The immutable clean narration snapshot is missing.", clip);
            }
        }
        var ordered = spoken.OrderBy(s => s.Start).ThenBy(s => s.End).ToArray();
        for (int index = 0; index < ordered.Length; index++)
        for (int other = index + 1; other < ordered.Length && ordered[other].Start < ordered[index].End - .5 / TimelineTime.SampleRate; other++)
            if (ordered[index].Clip.ClipId != ordered[other].Clip.ClipId) { Issue("timeline.spoken-overlap", "Spoken narration cannot overlap another narration clip.", ordered[other].Clip); break; }
        if (forExport)
        {
            foreach (string id in timeline.PlaceholderSegmentIds.Distinct(StringComparer.Ordinal)) issues.Add(new("timeline.segment-unplaced", "Place or explicitly omit every narration segment.", null, id));
            if (timeline.Clips.Count == 0 && timeline.Scope == AudioTimelineScope.Chapter) Issue("timeline.empty", "Place an audible clip before exporting this chapter.");
            if (timeline.EndMarkerSeconds is double end && timeline.Clips.Any(c => c.SegmentId is not null && !c.Muted
                && !timeline.Tracks.Any(t => t.TrackId == c.TrackId && t.Muted)
                && c.StartSeconds + c.DurationSeconds > end)) Issue("timeline.end-cuts-narration", "Move the end marker beyond all retained narration.");
        }
        return new(issues);
    }

    public static bool CanCutAt(WordAlignmentDocument? alignment, double sourceSeconds, double durationSeconds)
    {
        long frame = TimelineTime.ToFrames(sourceSeconds);
        if (frame == 0 || frame == TimelineTime.ToFrames(durationSeconds)) return true;
        if (alignment is null || alignment.TotalWordCount <= 0) return false;
        var words = alignment.Words.OrderBy(w => w.NarrationWordOrdinal).ToArray();
        for (int i = 0; i < words.Length; i++)
        {
            WordTiming word = words[i];
            bool startReliable = word.NarrationWordOrdinal == 0 || i > 0 && words[i - 1].NarrationWordOrdinal == word.NarrationWordOrdinal - 1 && words[i - 1].End <= word.Start;
            bool endReliable = word.NarrationWordOrdinal == alignment.TotalWordCount - 1 || i + 1 < words.Length && words[i + 1].NarrationWordOrdinal == word.NarrationWordOrdinal + 1 && word.End <= words[i + 1].Start;
            if (startReliable && Math.Abs(TimelineTime.ToFrames(word.Start.TotalSeconds) - frame) <= 1 || endReliable && Math.Abs(TimelineTime.ToFrames(word.End.TotalSeconds) - frame) <= 1) return true;
        }
        return false;
    }

    public static IReadOnlyList<(double Start, double End)> SpokenRanges(WordAlignmentDocument? alignment, double durationSeconds)
    {
        if (alignment is null || alignment.Words.Count == 0 || alignment.TotalWordCount <= 0) return [(0, durationSeconds)];
        var words = alignment.Words.OrderBy(w => w.NarrationWordOrdinal).ToArray();
        List<(double Start, double End)> ranges = [];
        if (words[0].NarrationWordOrdinal > 0) ranges.Add((0, words[0].Start.TotalSeconds));
        for (int i = 0; i < words.Length; i++)
        {
            ranges.Add((words[i].Start.TotalSeconds, words[i].End.TotalSeconds));
            if (i + 1 < words.Length && words[i + 1].NarrationWordOrdinal != words[i].NarrationWordOrdinal + 1) ranges.Add((words[i].End.TotalSeconds, words[i + 1].Start.TotalSeconds));
        }
        if (words[^1].NarrationWordOrdinal < alignment.TotalWordCount - 1) ranges.Add((words[^1].End.TotalSeconds, durationSeconds));
        return ranges;
    }

    public static WordAlignmentDocument? MatchingAlignment(AudioClipDocument clip, IReadOnlyDictionary<Guid, WordAlignmentDocument>? alignments) => clip.RenderId is Guid id && alignments is not null && alignments.TryGetValue(id, out WordAlignmentDocument? alignment)
        && alignment.RenderId == id && string.Equals(alignment.AudioSha256, clip.AssetSha256, StringComparison.OrdinalIgnoreCase) && string.Equals(alignment.SourceSha256, clip.SourceSha256, StringComparison.OrdinalIgnoreCase) ? alignment : null;

    public static AudioClipDocument WithRetainedWords(AudioClipDocument clip, VoiceBenchProjectDocument project, IReadOnlyDictionary<Guid, WordAlignmentDocument>? alignments)
    {
        if (clip.SegmentId is null) return clip;
        WordAlignmentDocument? alignment = MatchingAlignment(clip, alignments);
        if (alignment is null || alignment.TotalWordCount <= 0) return clip with { RetainedWordRanges = [] };
        int[] ordinals = alignment.Words.Where(w => TimelineTime.ToFrames(w.Start.TotalSeconds) >= clip.SourceStartFrame - 1 && TimelineTime.ToFrames(w.End.TotalSeconds) <= clip.SourceStartFrame + clip.DurationFrames + 1).Select(w => w.NarrationWordOrdinal).Distinct().Order().ToArray();
        // A continuous retained source span includes uncertain interior words. Keeping an original
        // audio edge also keeps its unmatched prefix/suffix: absence of timing is not an omission.
        AudioAssetRecord? asset = project.AudioAssets.FirstOrDefault(a => a.AssetId == clip.AssetId);
        bool keepsStart = clip.SourceStartFrame == 0;
        bool keepsEnd = asset is not null && clip.SourceStartFrame + clip.DurationFrames == TimelineTime.ToFrames(asset.DurationSeconds);
        List<RetainedWordRange> ranges = ordinals.Length == 0
            ? keepsStart && keepsEnd ? [new(0, alignment.TotalWordCount - 1)] : []
            : [new(keepsStart ? 0 : ordinals[0], keepsEnd ? alignment.TotalWordCount - 1 : ordinals[^1])];
        return clip with { RetainedWordRanges = ranges };
    }

    private static bool ValidGain(double gain) => double.IsFinite(gain) && gain >= -120 && gain <= 24;
    private static double MapBoundary(WordAlignmentDocument? previous, WordAlignmentDocument? replacement, double time, double previousDuration, double replacementDuration)
    {
        long frame = TimelineTime.ToFrames(time);
        if (frame == 0) return 0;
        if (frame == TimelineTime.ToFrames(previousDuration)) return replacementDuration;
        if (previous is null || replacement is null) throw new InvalidOperationException("Aligned words are required to transfer cuts.");
        foreach (var word in previous.Words)
        {
            var mapped = replacement.Words.FirstOrDefault(w => w.NarrationWordOrdinal == word.NarrationWordOrdinal);
            if (mapped is null) continue;
            if (Math.Abs(frame - TimelineTime.ToFrames(word.Start.TotalSeconds)) <= 1 && CanCutAt(replacement, mapped.Start.TotalSeconds, replacementDuration)) return mapped.Start.TotalSeconds;
            if (Math.Abs(frame - TimelineTime.ToFrames(word.End.TotalSeconds)) <= 1 && CanCutAt(replacement, mapped.End.TotalSeconds, replacementDuration)) return mapped.End.TotalSeconds;
        }
        throw new InvalidOperationException("The replacement has no reliable matching word boundary for this cut.");
    }
    private static bool ValidPan(double pan) => double.IsFinite(pan) && pan >= -1 && pan <= 1;
    private static bool ValidEnvelope(AudioClipDocument clip)
    {
        double last = -1;
        foreach (var point in clip.Envelope)
        {
            if (!double.IsFinite(point.OffsetSeconds) || point.OffsetSeconds < 0 || point.OffsetSeconds > clip.DurationSeconds || point.OffsetSeconds <= last || !ValidGain(point.GainDb)) return false;
            last = point.OffsetSeconds;
        }
        return true;
    }
    private static AudioClipDocument PreserveFadePhase(AudioClipDocument original, AudioClipDocument retained)
    {
        long delta = checked(retained.SourceStartFrame - original.SourceStartFrame);
        return retained with
        {
            FadeInOffsetSeconds = TimelineTime.ToSeconds(checked(TimelineTime.ToFrames(original.FadeInOffsetSeconds) + delta)),
            FadeOutOffsetSeconds = TimelineTime.ToSeconds(checked(TimelineTime.ToFrames(original.FadeOutOffsetSeconds) + original.DurationFrames - delta - retained.DurationFrames)),
        };
    }

    private static bool ValidFades(AudioClipDocument clip, AudioAssetRecord asset)
    {
        bool FrameTime(double seconds) => double.IsFinite(seconds) && Math.Abs(seconds) < long.MaxValue / (double)TimelineTime.SampleRate;
        if (!FrameTime(clip.FadeInSeconds) || !FrameTime(clip.FadeOutSeconds) || !FrameTime(clip.FadeInOffsetSeconds) || !FrameTime(clip.FadeOutOffsetSeconds)
            || clip.FadeInSeconds < 0 || clip.FadeOutSeconds < 0) return false;
        // A short slice may retain part of either original fade, including overlapping fade regions.
        // Looped media can have a fade extending across multiple repetitions of its immutable source.
        double originalExtent = clip.DurationSeconds + Math.Max(0, clip.FadeInOffsetSeconds) + Math.Max(0, clip.FadeOutOffsetSeconds);
        double limit = Math.Max(asset.DurationSeconds, originalExtent) + 1d / TimelineTime.SampleRate;
        return clip.FadeInSeconds <= limit && clip.FadeOutSeconds <= limit;
    }

    private static List<VolumeEnvelopePoint> SliceEnvelope(AudioClipDocument clip, double start, double end)
    {
        if (clip.Envelope.Count == 0 || end <= start) return [];
        List<VolumeEnvelopePoint> result = [new(0, GainAt(start))];
        result.AddRange(clip.Envelope.Where(p => p.OffsetSeconds > start && p.OffsetSeconds < end)
            .Select(p => p with { OffsetSeconds = p.OffsetSeconds - start }));
        result.Add(new(end - start, GainAt(end)));
        return result;

        double GainAt(double position)
        {
            if (position <= clip.Envelope[0].OffsetSeconds) return clip.Envelope[0].GainDb;
            for (int index = 1; index < clip.Envelope.Count; index++)
            {
                var right = clip.Envelope[index];
                if (position > right.OffsetSeconds) continue;
                var left = clip.Envelope[index - 1];
                return left.GainDb + (right.GainDb - left.GainDb) * (position - left.OffsetSeconds) / (right.OffsetSeconds - left.OffsetSeconds);
            }
            return clip.Envelope[^1].GainDb;
        }
    }
}
