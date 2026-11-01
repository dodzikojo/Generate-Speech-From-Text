using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

public sealed record TimelineCuePlacement(Guid? ClipId,string? SegmentId,string Type,string Content,int SourceWordOrdinal,double? PositionSeconds,string Status);
public sealed record PendingNarrationEstimate(string SegmentId,string Title,double EstimatedDurationSeconds,string Status);

public readonly record struct TimelineLoadRequest(int Version,string? ProjectRoot,Guid? ProjectId)
{
    public bool IsCurrent(int currentVersion,string? currentRoot,Guid? currentProjectId) => ProjectRoot is not null
        && Version == currentVersion && ProjectId == currentProjectId && string.Equals(ProjectRoot,currentRoot,StringComparison.OrdinalIgnoreCase);
}

/// <summary>Viewport calculations stay independent of drawing and authoring state.</summary>
public static class TimelinePresentation
{
    public static (double Left,double Width)? ClipChapterRegion(MixChapterRegion region,double viewStart,double visibleSeconds,double viewportWidth)
    {
        if (visibleSeconds <= 0 || viewportWidth <= 0) return null;
        double first = Math.Max(viewStart,region.StartSeconds), last = Math.Min(viewStart+visibleSeconds,region.StartSeconds+region.DurationSeconds);
        if (last <= first) return null;
        return ((first-viewStart)/visibleSeconds*viewportWidth,(last-first)/visibleSeconds*viewportWidth);
    }

    public static PendingNarrationEstimate EstimateUnplacedNarration(SegmentDocument segment,string authoringText)
    {
        var parsed = new ProductionScriptParser().Parse(authoringText);
        double estimate = IssueLab.VoiceBench.Infrastructure.Rendering.SegmentRenderService.EstimateDuration(parsed.NarrationText).TotalSeconds;
        bool takeAvailable = segment.RenderHistory.Any(render => render.Status is not (RenderStatus.Failed or RenderStatus.Cancelled) && !string.IsNullOrWhiteSpace(render.RawAudioRelativePath));
        return new(segment.SegmentId,segment.Title,estimate,takeAvailable ? "Unplaced · choose / approve a take" : "Unplaced · render required");
    }

    public static (AudioTimelineScope Scope,string? ChapterId) ResolveTimelineTarget(VoiceBenchProjectDocument project,AudioTimelineScope preferredScope,string? preferredChapterId)
    {
        if (preferredScope == AudioTimelineScope.Compilation) return (AudioTimelineScope.Compilation,null);
        var active = project.Chapters.Where(chapter => chapter.Status == ChapterStatus.Active).ToArray();
        if (active.Any(chapter => chapter.ChapterId == preferredChapterId)) return (AudioTimelineScope.Chapter,preferredChapterId);
        string? chapterId = project.ChapterOrder.FirstOrDefault(id => active.Any(chapter => chapter.ChapterId == id)) ?? active.FirstOrDefault()?.ChapterId;
        return chapterId is null ? (AudioTimelineScope.Compilation,null) : (AudioTimelineScope.Chapter,chapterId);
    }

    public static bool SameScriptWorkspace(VoiceBenchProjectDocument before,VoiceBenchProjectDocument after)
    {
        VoiceBenchProjectDocument WithoutAudio(VoiceBenchProjectDocument project) => project with
        {
            UpdatedAt = default,AudioAssets = [],AudioTimelines = [],AudioTimelineRevisionCounters = [],MixOutputStandard = MixOutputStandard.Default,
        };
        return string.Equals(System.Text.Json.JsonSerializer.Serialize(WithoutAudio(before),ProjectJson.SerializerOptions),
            System.Text.Json.JsonSerializer.Serialize(WithoutAudio(after),ProjectJson.SerializerOptions),StringComparison.Ordinal);
    }

    public static bool SameWaveformGeometry(AudioTimelineDocument? previous,AudioTimelineDocument next)
    {
        if (previous is null || previous.TimelineId != next.TimelineId || previous.Clips.Count != next.Clips.Count) return false;
        var clips = previous.Clips.ToDictionary(clip => clip.ClipId);
        return next.Clips.All(clip => clips.TryGetValue(clip.ClipId,out var before)
            && before.AssetId == clip.AssetId && before.AssetSha256 == clip.AssetSha256 && before.TrackId == clip.TrackId
            && before.StartFrame == clip.StartFrame && before.SourceStartFrame == clip.SourceStartFrame && before.DurationFrames == clip.DurationFrames && before.Loop == clip.Loop);
    }

    public static IReadOnlyList<double> ReliableWordSnapPositions(AudioClipDocument clip,WordAlignmentDocument document,double sourceDuration)
    {
        if (clip.RenderId != document.RenderId || !string.Equals(clip.AssetSha256,document.AudioSha256,StringComparison.OrdinalIgnoreCase)
            || !string.Equals(clip.SourceSha256,document.SourceSha256,StringComparison.OrdinalIgnoreCase)) return [];
        return document.Words.SelectMany(word => new[] { word.Start.TotalSeconds,word.End.TotalSeconds })
            .Where(source => source >= clip.SourceStartSeconds && source <= clip.SourceStartSeconds+clip.DurationSeconds)
            .Where(source => AudioTimelineRules.CanCutAt(document,source,sourceDuration))
            .Select(source => clip.StartSeconds+source-clip.SourceStartSeconds)
            .Where(time => time >= clip.StartSeconds && time <= clip.StartSeconds+clip.DurationSeconds).Distinct().ToArray();
    }
    public static double GridStep(double visibleSeconds, double viewportWidth = 800)
    {
        double divisions = Math.Clamp(Math.Floor(viewportWidth / 100),1,8);
        double rough = Math.Max(.01,visibleSeconds / divisions);
        double magnitude = Math.Pow(10,Math.Floor(Math.Log10(rough)));
        double scaled = rough / magnitude;
        return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * magnitude;
    }

    public static IReadOnlyList<double> RulerTicks(double start,double visibleSeconds,double viewportWidth = 800)
    {
        double step = GridStep(visibleSeconds,viewportWidth);
        List<double> ticks = [];
        for (double time = Math.Ceiling(start / step) * step; time <= start + visibleSeconds + step * .0001; time += step) ticks.Add(time);
        return ticks;
    }

    public static TimelineCuePlacement PlaceCue(MixRenderClip clip,ProductionCue cue,bool current)
    {
        int ordinal = cue.PrecedingNarrationWords;
        bool retained = clip.RetainedWordRanges.Any(range => ordinal >= range.FirstWordOrdinal && ordinal <= range.LastWordOrdinal);
        var timing = clip.AlignedWords.FirstOrDefault(word => word.NarrationWordOrdinal == ordinal);
        bool inside = timing is not null && timing.Start.TotalSeconds >= clip.SourceStartSeconds && timing.Start.TotalSeconds < clip.SourceStartSeconds + clip.DurationSeconds;
        double? position = current && retained && inside ? clip.StartSeconds + timing!.Start.TotalSeconds - clip.SourceStartSeconds : null;
        string status = position.HasValue ? "Placed" : !current ? "Stale source · unplaced" : !retained ? "Removed anchor · unplaced" : "Uncertain word · unplaced";
        return new(clip.ClipId,clip.Asset.SegmentId,cue.Type,cue.Content,ordinal,position,status);
    }

    public static bool CanFollow(string pinnedSourceSha256,string savedSourceSha256,ParsedScriptDocument? liveDraft = null) =>
        string.Equals(pinnedSourceSha256,savedSourceSha256,StringComparison.OrdinalIgnoreCase)
        && (liveDraft is null || WordFollowPlaybackRules.IsNarrationCurrent(pinnedSourceSha256,liveDraft.NarrationText));

    public static double Duration(AudioTimelineDocument? timeline) => Math.Max(1,
        timeline?.EndMarkerSeconds ?? timeline?.Clips.Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max() ?? 0);

    public static double Snap(double requested, double tolerance, IEnumerable<double> candidates)
    {
        double bounded = Math.Max(0, requested);
        double closest = candidates.Where(double.IsFinite).OrderBy(value => Math.Abs(value - bounded)).FirstOrDefault(double.NaN);
        return double.IsFinite(closest) && Math.Abs(closest - bounded) <= tolerance ? Math.Max(0, closest) : bounded;
    }

    public static IReadOnlyList<TimelineEdit> MoveSelection(AudioTimelineDocument timeline, IReadOnlySet<Guid> selected, double delta)
    {
        AudioClipDocument[] clips = timeline.Clips.Where(clip => selected.Contains(clip.ClipId)).ToArray();
        if (clips.Length == 0) return [];
        if (clips.Any(clip => timeline.Tracks.Any(track => track.TrackId == clip.TrackId && track.Locked)))
            throw new InvalidOperationException("Unlock the selected tracks before moving clips.");
        double bounded = Math.Max(delta, -clips.Min(clip => clip.StartSeconds));
        return clips.Select(clip => new TimelineEdit { Operation = TimelineEditOperation.MoveClip,
            ClipId = clip.ClipId, StartSeconds = clip.StartSeconds + bounded }).ToArray();
    }

    public static bool Intersects(AudioClipDocument clip, double start, double visibleSeconds) =>
        clip.StartSeconds < start + visibleSeconds && clip.StartSeconds + clip.DurationSeconds > start;

    public static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff");
}
