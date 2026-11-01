using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Rendering;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>The Audio page and editor dock share this exact selection and arrangement.</summary>
public sealed class AudioTimelineWorkspace(AppServices services)
{
    private int _loadVersion;
    public AudioTimelineDocument? Timeline { get; private set; }
    public AudioTimelineScope Scope { get; private set; } = AudioTimelineScope.Compilation;
    public string? ChapterId { get; private set; }
    public HashSet<Guid> SelectedClipIds { get; } = [];
    public HashSet<Guid> MonitorSoloTrackIds { get; } = [];
    public Guid? SelectedTrackId { get; set; }
    public double ViewStart { get; set; }
    public double VisibleSeconds { get; set; } = 40;
    public bool SnapEnabled { get; set; } = true;
    public double SnapInterval { get; set; } = .1;
    public IReadOnlyList<double> WordSnapPositions { get; private set; } = [];
    public string Notice { get; private set; } = "Choose a chapter or compilation. Source audio stays immutable.";
    public event EventHandler? Changed;

    public async Task LoadAsync(AudioTimelineScope? scope = null, string? chapterId = null)
    {
        if (!services.Session.HasProject) { Timeline = null; Notify(); return; }
        int version = ++_loadVersion;
        AudioTimelineScope requestedScope = scope ?? Scope;
        string? requestedChapter = scope.HasValue ? chapterId : ChapterId;
        string projectRoot = services.Session.RequireProjectRoot();
        var timeline = await Task.Run(() => services.AudioTimeline.GetAsync(projectRoot, requestedScope, requestedChapter));
        if (version != _loadVersion) return;
        Scope = requestedScope;
        ChapterId = requestedChapter;
        if (Timeline is { } previous && (previous.Revision != timeline.Revision || previous.TimelineId != timeline.TimelineId)) services.InvalidateMixPlayback();
        Timeline = timeline;
        SelectedClipIds.RemoveWhere(id => !timeline.Clips.Any(clip => clip.ClipId == id));
        if (!timeline.Tracks.Any(track => track.TrackId == SelectedTrackId)) SelectedTrackId = timeline.Tracks.FirstOrDefault()?.TrackId;
        Notify();
        await LoadWordSnapPositionsAsync(timeline,version);
    }

    public async Task EditAsync(params TimelineEdit[] edits)
    {
        if (Timeline is null || edits.Length == 0) return;
        Timeline = await services.RunTimelineCommandAsync<AudioTimelineDocument>("mix.edit", new
        { scope = Scope, chapterId = ChapterId, expectedRevision = Timeline.Revision, edits });
        services.InvalidateMixPlayback();
        Notice = "Saved · one undoable timeline change";
        Notify();
        await LoadWordSnapPositionsAsync(Timeline,++_loadVersion);
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

    private async Task LoadWordSnapPositionsAsync(AudioTimelineDocument timeline,int version)
    {
        VoiceBenchProjectDocument? project = services.Session.Project;
        if (project is null) return;
        List<double> positions = [];
        foreach (var clip in timeline.Clips.Where(clip => clip.SegmentId is not null && clip.RenderId is not null))
        {
            var segment = project.Segments.FirstOrDefault(item => item.SegmentId == clip.SegmentId);
            var render = segment?.RenderHistory.FirstOrDefault(item => item.RenderId == clip.RenderId);
            if (render is null || segment!.SourceSha256 != clip.SourceSha256) continue;
            var record = render.AlignmentHistory.LastOrDefault(item => item.Status is WordAlignmentStatus.Aligned or WordAlignmentStatus.Partial && !string.IsNullOrWhiteSpace(item.ArtifactRelativePath));
            if (record is null) continue;
            try
            {
                var document = await FileWordAlignmentService.LoadDocumentAsync(services.Session.RequireProjectRoot(),record);
                if (version != _loadVersion) return;
                foreach (var word in document.Words)
                    foreach (double source in new[] { word.Start.TotalSeconds,word.End.TotalSeconds })
                    {
                        double time = clip.StartSeconds + source - clip.SourceStartSeconds;
                        if (time >= clip.StartSeconds && time <= clip.StartSeconds + clip.DurationSeconds) positions.Add(time);
                    }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        if (version == _loadVersion) { WordSnapPositions = positions; Notify(); }
    }
}
