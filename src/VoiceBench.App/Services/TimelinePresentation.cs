using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>Viewport calculations stay independent of drawing and authoring state.</summary>
public static class TimelinePresentation
{
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
