using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>One approved take queued for chapter playback.</summary>
public sealed record ChapterPlaylistEntry(
    string SegmentId,
    string Title,
    Guid RenderId,
    int TakeNumber,
    string AudioRelativePath,
    TimeSpan Duration,
    bool ScriptChanged);

/// <summary>The approved takes of one chapter, in chapter order.</summary>
public sealed record ChapterPlaylist(
    string ChapterId,
    string ChapterTitle,
    IReadOnlyList<ChapterPlaylistEntry> Entries,
    IReadOnlyList<string> SkippedSegmentTitles)
{
    public TimeSpan TotalDuration => Entries.Aggregate(TimeSpan.Zero, (total, entry) => total + entry.Duration);

    /// <summary>Index of the segment, or of the next playable segment after it; 0 when unknown.</summary>
    public int StartIndexFor(VoiceBenchProjectDocument project, string? segmentId)
    {
        if (segmentId is null) return 0;
        int exact = Entries.ToList().FindIndex(entry => entry.SegmentId == segmentId);
        if (exact >= 0) return exact;
        ChapterDocument? chapter = project.Chapters.FirstOrDefault(item => item.ChapterId == ChapterId);
        int position = chapter?.SegmentOrder.IndexOf(segmentId) ?? -1;
        if (position < 0 || chapter is null) return 0;
        for (int index = 0; index < Entries.Count; index++)
        {
            if (chapter.SegmentOrder.IndexOf(Entries[index].SegmentId) > position) return index;
        }

        return Entries.Count;
    }
}

public static class ChapterPlaylistBuilder
{
    /// <summary>
    /// Collects each segment's approved take (the take named by <see cref="SegmentDocument.ApprovedRenderId"/>
    /// whose status is Approved). Segments without one are reported as skipped, never substituted.
    /// </summary>
    public static ChapterPlaylist Build(VoiceBenchProjectDocument project, string chapterId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ChapterDocument chapter = project.Chapters.FirstOrDefault(item => item.ChapterId == chapterId)
            ?? throw new ArgumentException("The chapter is not part of this project.", nameof(chapterId));
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        List<ChapterPlaylistEntry> entries = [];
        List<string> skipped = [];
        foreach (string segmentId in chapter.SegmentOrder)
        {
            if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
            RenderRecord? approved = segment.ApprovedRenderId is Guid id
                ? segment.RenderHistory.FirstOrDefault(render => render.RenderId == id && render.Status == RenderStatus.Approved)
                : null;
            string? audio = approved is null ? null : ProjectLocations.TakeAudioRelativePath(approved);
            if (approved is null || audio is null)
            {
                skipped.Add(segment.Title);
                continue;
            }

            entries.Add(new ChapterPlaylistEntry(
                segment.SegmentId,
                segment.Title,
                approved.RenderId,
                approved.TakeNumber,
                audio,
                approved.Duration,
                !string.IsNullOrEmpty(segment.SourceSha256)
                    && !string.Equals(approved.SourceSha256, segment.SourceSha256, StringComparison.OrdinalIgnoreCase)));
        }

        return new ChapterPlaylist(chapter.ChapterId, chapter.Title, entries, skipped);
    }
}

public enum TransportPhase
{
    Playing,
    Paused,
    Ended,
}

/// <summary>Decides when a stopped transport has reached the end of its audio (rather than being paused).</summary>
public static class TransportPhaseRules
{
    public static readonly TimeSpan EndTolerance = TimeSpan.FromMilliseconds(150);

    public static TransportPhase Next(bool playing, bool wasRunningOrEnded, TimeSpan position, TimeSpan duration)
    {
        if (playing) return TransportPhase.Playing;
        bool atEnd = duration > TimeSpan.Zero && position >= duration - EndTolerance;
        return wasRunningOrEnded && atEnd ? TransportPhase.Ended : TransportPhase.Paused;
    }
}
