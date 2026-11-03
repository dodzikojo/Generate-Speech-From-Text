namespace IssueLab.VoiceBench.Core;

/// <summary>A segment's takes as clean-up sees them.</summary>
public sealed record TakeCleanupSegment(
    string ChapterId,
    string SegmentId,
    string SegmentTitle,
    int SegmentNumber,
    RenderRecord? ApprovedTake,
    IReadOnlyList<RenderRecord> Removable,
    IReadOnlyList<RenderRecord> KeptForMix);

/// <summary>How much take audio one segment holds on disk.</summary>
public sealed record SegmentTakeStorage(
    string SegmentId,
    string SegmentTitle,
    int SegmentNumber,
    long TotalBytes,
    long ApprovedBytes,
    long RemovableBytes,
    int RemovableTakes,
    int KeptForMixTakes);

/// <summary>How much take audio one chapter holds on disk, and how much clean-up would free.</summary>
public sealed record ChapterTakeStorage(
    string ChapterId,
    string ChapterTitle,
    int ChapterNumber,
    IReadOnlyList<SegmentTakeStorage> Segments)
{
    public long TotalBytes => Segments.Sum(segment => segment.TotalBytes);

    public long ApprovedBytes => Segments.Sum(segment => segment.ApprovedBytes);

    public long RemovableBytes => Segments.Sum(segment => segment.RemovableBytes);

    public int RemovableTakes => Segments.Sum(segment => segment.RemovableTakes);

    public int KeptForMixTakes => Segments.Sum(segment => segment.KeptForMixTakes);
}

public sealed record TakeStorageReport(IReadOnlyList<ChapterTakeStorage> Chapters)
{
    public long TotalBytes => Chapters.Sum(chapter => chapter.TotalBytes);

    public long ApprovedBytes => Chapters.Sum(chapter => chapter.ApprovedBytes);

    public long RemovableBytes => Chapters.Sum(chapter => chapter.RemovableBytes);

    public int RemovableTakes => Chapters.Sum(chapter => chapter.RemovableTakes);

    public int KeptForMixTakes => Chapters.Sum(chapter => chapter.KeptForMixTakes);
}

/// <summary>
/// Clean-up removes the audio of takes nobody needs: every take of an approved segment except its
/// approved take. Segments still awaiting review keep all their takes, and a take a mix uses (a
/// clip or a pending replacement) is always kept. Records stay, marked with
/// <see cref="RenderRecord.AudioDeletedAt"/>, so the history is complete and numbers never repeat.
/// </summary>
public static class TakeCleanupRules
{
    /// <summary>Takes a chapter or compilation mix still points at.</summary>
    public static IReadOnlySet<Guid> TakesUsedByMixes(VoiceBenchProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<Guid, Guid?> assetRenders = project.AudioAssets
            .GroupBy(asset => asset.AssetId)
            .ToDictionary(group => group.Key, group => group.First().RenderId);
        HashSet<Guid> used = [];
        void AddAsset(Guid assetId)
        {
            if (assetRenders.TryGetValue(assetId, out Guid? renderId) && renderId is Guid id) used.Add(id);
        }

        foreach (AudioTimelineDocument timeline in project.AudioTimelines)
        {
            foreach (AudioClipDocument clip in timeline.Clips)
            {
                if (clip.RenderId is Guid renderId) used.Add(renderId);
                AddAsset(clip.AssetId);
            }

            foreach (TakeReplacementProposal proposal in timeline.ReplacementProposals)
            {
                used.Add(proposal.CurrentRenderId);
                used.Add(proposal.ProposedRenderId);
                AddAsset(proposal.ProposedAssetId);
            }
        }

        return used;
    }

    /// <summary>
    /// Approved segments in rail order with the takes clean-up may remove. With
    /// <paramref name="audioOnDisk"/>, a segment whose approved take has no audio on disk keeps every
    /// take: the others might be the only audio left.
    /// </summary>
    public static IReadOnlyList<TakeCleanupSegment> Plan(VoiceBenchProjectDocument project, Func<RenderRecord, bool>? audioOnDisk = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        IReadOnlySet<Guid> usedByMixes = TakesUsedByMixes(project);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        List<TakeCleanupSegment> result = [];
        foreach (ChapterDocument chapter in ProjectOrdering.GetOrderedChapters(project))
        {
            int number = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                number++;
                (RenderRecord? approved, string? problem) = ApprovedTakePackageRules.ApprovedTake(segment);
                if (approved is null || problem is not null || audioOnDisk?.Invoke(approved) == false)
                {
                    result.Add(new TakeCleanupSegment(chapter.ChapterId, segment.SegmentId, segment.Title, number, null, [], []));
                    continue;
                }

                List<RenderRecord> others = segment.RenderHistory
                    .Where(take => take.RenderId != approved.RenderId && take.AudioDeletedAt is null && HasAudio(take))
                    .OrderBy(take => take.TakeNumber)
                    .ToList();
                result.Add(new TakeCleanupSegment(
                    chapter.ChapterId, segment.SegmentId, segment.Title, number, approved,
                    others.Where(take => !usedByMixes.Contains(take.RenderId)).ToList(),
                    others.Where(take => usedByMixes.Contains(take.RenderId)).ToList()));
            }
        }

        return result;
    }

    /// <summary>
    /// Measures take audio per chapter. <paramref name="takeFiles"/> lists a live take's audio files
    /// (project-relative) and <paramref name="fileSize"/> returns a file's size, or null if it is gone.
    /// A file shared by several takes is counted once per segment.
    /// </summary>
    public static TakeStorageReport Measure(
        VoiceBenchProjectDocument project,
        Func<RenderRecord, IReadOnlyList<string>> takeFiles,
        Func<string, long?> fileSize)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(takeFiles);
        ArgumentNullException.ThrowIfNull(fileSize);
        Dictionary<string, TakeCleanupSegment> cleanup = Plan(project, take => takeFiles(take).Count > 0)
            .ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        List<ChapterTakeStorage> chapters = [];
        int chapterNumber = 0;
        foreach (ChapterDocument chapter in ProjectOrdering.GetOrderedChapters(project))
        {
            chapterNumber++;
            List<SegmentTakeStorage> rows = [];
            int segmentNumber = 0;
            foreach (string segmentId in chapter.SegmentOrder)
            {
                if (!segments.TryGetValue(segmentId, out SegmentDocument? segment)) continue;
                segmentNumber++;
                long Size(IEnumerable<RenderRecord> takes) => takes
                    .SelectMany(takeFiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Sum(path => fileSize(path) ?? 0);
                List<RenderRecord> live = segment.RenderHistory.Where(take => take.AudioDeletedAt is null).ToList();
                TakeCleanupSegment plan = cleanup[segment.SegmentId];
                rows.Add(new SegmentTakeStorage(
                    segment.SegmentId,
                    segment.Title,
                    segmentNumber,
                    Size(live),
                    plan.ApprovedTake is null ? 0 : Size([plan.ApprovedTake]),
                    Size(plan.Removable),
                    plan.Removable.Count,
                    plan.KeptForMix.Count));
            }

            chapters.Add(new ChapterTakeStorage(chapter.ChapterId, chapter.Title, chapterNumber, rows));
        }

        return new TakeStorageReport(chapters);
    }

    /// <summary>Marks takes as cleaned up and forgets their audio assets (a mix never uses them).</summary>
    public static VoiceBenchProjectDocument MarkDeleted(
        VoiceBenchProjectDocument project,
        IReadOnlyCollection<Guid> renderIds,
        DateTimeOffset deletedAt)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(renderIds);
        IReadOnlySet<Guid> usedByMixes = TakesUsedByMixes(project);
        HashSet<Guid> removing = renderIds.Where(id => !usedByMixes.Contains(id)).ToHashSet();
        if (removing.Count == 0) return project;
        List<SegmentDocument> segments = project.Segments.Select(segment =>
        {
            if (!segment.RenderHistory.Any(take => removing.Contains(take.RenderId))) return segment;
            if (segment.ApprovedRenderId is Guid approved && removing.Contains(approved))
                throw new InvalidOperationException($"The approved take of “{segment.Title}” cannot be cleaned up.");
            return segment with
            {
                RenderHistory = segment.RenderHistory
                    .Select(take => removing.Contains(take.RenderId) && take.AudioDeletedAt is null ? take with { AudioDeletedAt = deletedAt } : take)
                    .ToList(),
            };
        }).ToList();
        return project with
        {
            Segments = segments,
            AudioAssets = project.AudioAssets.Where(asset => asset.RenderId is not Guid id || !removing.Contains(id)).ToList(),
        };
    }

    public static string FormatBytes(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / KB:0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / MB:0.0} MB",
            _ => $"{bytes / GB:0.00} GB",
        };
    }

    private static bool HasAudio(RenderRecord take) =>
        !string.IsNullOrWhiteSpace(take.RawAudioRelativePath) || !string.IsNullOrWhiteSpace(take.ProcessedAudioRelativePath);
}
