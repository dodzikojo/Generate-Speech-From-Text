namespace IssueLab.VoiceBench.Core;

public static class ProjectOrdering
{
    public static IReadOnlyList<ChapterDocument> GetOrderedChapters(
        VoiceBenchProjectDocument project,
        bool includedOnly = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(
            chapter => chapter.ChapterId,
            StringComparer.Ordinal);
        IEnumerable<ChapterDocument> ordered = project.ChapterOrder
            .Where(chapters.ContainsKey)
            .Select(id => chapters[id]);
        return ordered
            .Where(chapter => chapter.Status == ChapterStatus.Active)
            .Where(chapter => !includedOnly || chapter.IncludedInCompilation)
            .ToArray();
    }

    public static IReadOnlyList<string> GetOrderedSegmentIds(
        VoiceBenchProjectDocument project,
        MasterScope scope = MasterScope.Compilation,
        string? chapterId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        IEnumerable<ChapterDocument> chapters = scope switch
        {
            MasterScope.Chapter when !string.IsNullOrWhiteSpace(chapterId) =>
                GetOrderedChapters(project).Where(chapter => chapter.ChapterId == chapterId),
            MasterScope.Chapter => throw new ArgumentException(
                "A chapter ID is required for chapter ordering.",
                nameof(chapterId)),
            _ => GetOrderedChapters(project, includedOnly: true),
        };

        return chapters.SelectMany(chapter => chapter.SegmentOrder).ToArray();
    }

    public static IReadOnlyList<string> GetPlaybackOrderedSegmentIds(VoiceBenchProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return GetOrderedChapters(project)
            .SelectMany(chapter => chapter.SegmentOrder)
            .ToArray();
    }

    public static ChapterDocument RequireChapter(
        VoiceBenchProjectDocument project,
        string chapterId) => project.Chapters.SingleOrDefault(chapter => chapter.ChapterId == chapterId)
        ?? throw new InvalidOperationException($"Chapter '{chapterId}' does not exist.");
}
