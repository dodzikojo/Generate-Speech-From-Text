using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>The Audio page and editor dock share this exact selection and arrangement.</summary>
public sealed class AudioTimelineWorkspace(AppServices services)
{
    private int _loadVersion;
    private string? _projectRoot;
    private Guid? _projectId;
    private const string ReadyNotice = "Arrangement ready · source audio stays immutable.";
    public AudioTimelineDocument? Timeline { get; private set; }
    public AudioTimelineScope Scope { get; private set; } = AudioTimelineScope.Compilation;
    public string? ChapterId { get; private set; }
    public HashSet<Guid> SelectedClipIds { get; } = [];
    public HashSet<Guid> MonitorSoloTrackIds { get; } = [];
    public Guid? SelectedTrackId { get; set; }
    public double ViewStart { get; set; }
    public double PlayheadSeconds { get; set; }
    public double VisibleSeconds { get; set; } = 40;
    public bool SnapEnabled { get; set; } = true;
    public double SnapInterval { get; set; } = .1;
    public IReadOnlyList<double> WordSnapPositions { get; private set; } = [];
    public IReadOnlyList<TimelineCuePlacement> CuePlacements { get; private set; } = [];
    public string Notice { get; private set; } = "Choose a chapter or compilation. Source audio stays immutable.";
    public event EventHandler? Changed;

    public async Task LoadAsync(AudioTimelineScope? scope = null, string? chapterId = null)
    {
        int version = ++_loadVersion;
        if (!services.Session.HasProject)
        {
            _projectRoot = null; _projectId = null;
            ResetProjectPresentation(); Notify(); return;
        }
        AudioTimelineScope requestedScope = scope ?? Scope;
        string? requestedChapter = scope.HasValue ? chapterId : ChapterId;
        string projectRoot = services.Session.RequireProjectRoot();
        VoiceBenchProjectDocument project = services.Session.RequireProject();
        if (!scope.HasValue) (requestedScope,requestedChapter) = TimelinePresentation.ResolveTimelineTarget(project,requestedScope,requestedChapter);
        bool changedProject = _projectId != project.ProjectId || !string.Equals(_projectRoot,projectRoot,StringComparison.OrdinalIgnoreCase);
        if (changedProject)
        {
            ResetProjectPresentation();
            _projectRoot = projectRoot; _projectId = project.ProjectId;
            Scope = requestedScope; ChapterId = requestedChapter;
            Notify();
        }
        var request = new TimelineLoadRequest(version,projectRoot,project.ProjectId);
        try
        {
            var timeline = await Task.Run(() => services.AudioTimeline.GetAsync(projectRoot, requestedScope, requestedChapter));
            if (!IsCurrent(request)) return;
            bool changedScope = requestedScope != Scope || requestedChapter != ChapterId;
            MixRenderPlan? parameterPlan = changedScope ? null : await services.TryRefreshMixParametersAsync(() => IsCurrent(request));
            if (!IsCurrent(request)) return;
            if (services.PlaybackTarget == "mix" && (changedScope || parameterPlan is null)) services.InvalidateMixPlayback();
            Scope = requestedScope; ChapterId = requestedChapter; Timeline = timeline; Notice = ReadyNotice;
            SelectedClipIds.RemoveWhere(id => !timeline.Clips.Any(clip => clip.ClipId == id));
            if (!timeline.Tracks.Any(track => track.TrackId == SelectedTrackId)) SelectedTrackId = timeline.Tracks.FirstOrDefault()?.TrackId;
            Notify();
            await LoadAnnotationsAsync(timeline,version,parameterPlan);
        }
        catch (Exception) when (!IsCurrent(request)) { }
    }

    public async Task RefreshFromSessionAsync()
    {
        var request = new TimelineLoadRequest(_loadVersion+1,services.Session.ProjectRoot,services.Session.Project?.ProjectId);
        try { await LoadAsync(); }
        catch (Exception error) { if (IsCurrent(request)) SetNotice(error.Message); }
    }

    private bool IsCurrent(TimelineLoadRequest request) => request.IsCurrent(_loadVersion,services.Session.ProjectRoot,services.Session.Project?.ProjectId);
    private bool IsCurrent(int version) => IsCurrent(new TimelineLoadRequest(version,_projectRoot,_projectId));

    private void ResetProjectPresentation()
    {
        services.InvalidateMixPlayback();
        Timeline = null; Scope = AudioTimelineScope.Compilation; ChapterId = null;
        SelectedClipIds.Clear(); MonitorSoloTrackIds.Clear(); SelectedTrackId = null;
        WordSnapPositions = []; CuePlacements = []; ViewStart = 0; PlayheadSeconds = 0;
        Notice = services.Session.HasProject ? ReadyNotice : "Open a project to mix audio.";
    }

    public async Task EditAsync(params TimelineEdit[] edits)
    {
        if (Timeline is null || edits.Length == 0) return;
        var origin = new TimelineLoadRequest(_loadVersion,services.Session.ProjectRoot,services.Session.Project?.ProjectId);
        var edited = await services.RunTimelineCommandAsync<AudioTimelineDocument>("mix.edit", new
        { scope = Scope, chapterId = ChapterId, expectedRevision = Timeline.Revision, edits });
        if (!string.Equals(origin.ProjectRoot,services.Session.ProjectRoot,StringComparison.OrdinalIgnoreCase) || origin.ProjectId != services.Session.Project?.ProjectId) return;
        Timeline = edited;
        int version = ++_loadVersion;
        MixRenderPlan? parameterPlan = await services.TryRefreshMixParametersAsync(() => IsCurrent(version));
        if (!IsCurrent(version)) return;
        if (services.PlaybackTarget == "mix" && parameterPlan is null) services.InvalidateMixPlayback();
        Notice = "Saved · one undoable timeline change";
        Notify();
        await LoadAnnotationsAsync(Timeline,version,parameterPlan);
    }

    public void Select(Guid clipId, bool extend)
    {
        if (!extend) SelectedClipIds.Clear();
        if (extend && SelectedClipIds.Contains(clipId)) SelectedClipIds.Remove(clipId); else SelectedClipIds.Add(clipId);
        SelectedTrackId = Timeline?.Clips.SingleOrDefault(clip => clip.ClipId == clipId)?.TrackId;
        Notify();
    }

    public double Snap(double position, double pixelsPerSecond, IEnumerable<Guid>? excluded = null)
    {
        if (!SnapEnabled || Timeline is null) return Math.Max(0, position);
        HashSet<Guid> omit = excluded?.ToHashSet() ?? [];
        var candidates = Timeline.Clips.Where(clip => !omit.Contains(clip.ClipId))
            .SelectMany(clip => new[] { clip.StartSeconds, clip.StartSeconds + clip.DurationSeconds })
            .Concat(Timeline.Markers.Where(marker => marker.Anchor.Placed).Select(marker => marker.PositionSeconds))
            .Concat(WordSnapPositions)
            .Append(Math.Round(position / SnapInterval) * SnapInterval);
        return TimelinePresentation.Snap(position, 7 / Math.Max(1, pixelsPerSecond), candidates);
    }

    public void SetNotice(string text) { Notice = text; Notify(); }
    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    private async Task LoadAnnotationsAsync(AudioTimelineDocument timeline,int version,MixRenderPlan? parameterPlan)
    {
        await LoadWordSnapPositionsAsync(timeline,version);
        if (!IsCurrent(version) || services.Session.Project is not { } project) return;
        string root = services.Session.RequireProjectRoot();
        AudioTimelineScope scope = Scope;
        string? chapterId = ChapterId;
        var cues = await Task.Run(async () =>
        {
            List<TimelineCuePlacement> result = [];
            List<MixRenderClip> clips = [];
            try
            {
                MixRenderPlan plan = parameterPlan is not null && parameterPlan.Scope == scope && parameterPlan.ChapterId == chapterId
                    ? parameterPlan : parameterPlan is not null
                        ? await services.AudioTimeline.BuildParameterUpdatePlanAsync(root,scope,chapterId)
                        : await services.AudioTimeline.BuildPreviewPlanAsync(root,scope,chapterId);
                clips.AddRange(plan.Clips);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) { }
            HashSet<string> represented = [];
            foreach (var clip in clips.Where(clip => clip.Asset.SegmentId is not null && !clip.Muted && !clip.Track.Muted))
            {
                string segmentId = clip.Asset.SegmentId!;
                represented.Add(segmentId);
                var segment = project.Segments.FirstOrDefault(item => item.SegmentId == segmentId);
                ParsedScriptDocument? latest = null;
                if (segment is not null)
                {
                    try { latest = new ProductionScriptParser().Parse(await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root,segment.ScriptRelativePath))); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                bool current = segment is not null && CanPlace(clip,segment,latest);
                var sourceCues = current && latest is not null ? latest.Cues : clip.ProductionCues;
                result.AddRange(sourceCues.Select(cue => TimelinePresentation.PlaceCue(clip,cue,current)));
            }
            var activeChapters = project.Chapters.Where(chapter => chapter.Status == ChapterStatus.Active && chapter.IncludedInCompilation).Select(chapter => chapter.ChapterId).ToHashSet();
            foreach (var segment in project.Segments.Where(segment => !represented.Contains(segment.SegmentId)
                && (scope == AudioTimelineScope.Chapter ? segment.ChapterId == chapterId : activeChapters.Contains(segment.ChapterId))))
            {
                try
                {
                    var parsed = new ProductionScriptParser().Parse(await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root,segment.ScriptRelativePath)));
                    result.AddRange(parsed.Cues.Select(cue => new TimelineCuePlacement(null,segment.SegmentId,cue.Type,cue.Content,cue.PrecedingNarrationWords,null,
                        timeline.OmittedSegmentIds.Contains(segment.SegmentId) ? "Omitted narration · unplaced" : "No retained narration clip · unplaced")));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return result;
        });
        if (IsCurrent(version)) { CuePlacements = cues; Notify(); }

        static bool CanPlace(MixRenderClip clip,SegmentDocument segment,ParsedScriptDocument? source) =>
            source is not null && TimelinePresentation.CanFollow(clip.Asset.SourceSha256,segment.SourceSha256,source);
    }

    private async Task LoadWordSnapPositionsAsync(AudioTimelineDocument timeline,int version)
    {
        VoiceBenchProjectDocument? project = services.Session.Project;
        if (project is null || !IsCurrent(version)) return;
        string root = services.Session.RequireProjectRoot();
        var assets = AudioTimelineRules.RegisterRenderAssets(project).AudioAssets.ToDictionary(asset => asset.AssetId);
        Dictionary<Guid,WordAlignmentDocument> alignmentCache = [];
        List<double> positions = [];
        foreach (var clip in timeline.Clips.Where(clip => clip.SegmentId is not null && clip.RenderId is not null))
        {
            var segment = project.Segments.FirstOrDefault(item => item.SegmentId == clip.SegmentId);
            var render = segment?.RenderHistory.FirstOrDefault(item => item.RenderId == clip.RenderId);
            if (render is null || segment!.SourceSha256 != clip.SourceSha256 || render.SourceSha256 != clip.SourceSha256) continue;
            var record = render.AlignmentHistory.LastOrDefault(item => (item.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial)
                && string.Equals(item.AudioSha256,clip.AssetSha256,StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.ArtifactRelativePath));
            if (record is null) continue;
            try
            {
                if (!alignmentCache.TryGetValue(record.AlignmentId,out var document))
                    alignmentCache[record.AlignmentId] = document = await FileWordAlignmentService.LoadDocumentAsync(root,record);
                if (!IsCurrent(version)) return;
                double duration = assets.TryGetValue(clip.AssetId,out var asset) ? asset.DurationSeconds : render.Duration.TotalSeconds;
                positions.AddRange(TimelinePresentation.ReliableWordSnapPositions(clip,document,duration));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        if (IsCurrent(version)) { WordSnapPositions = positions; Notify(); }
    }
}
