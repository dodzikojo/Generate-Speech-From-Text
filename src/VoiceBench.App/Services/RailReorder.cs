using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.App.Services;

/// <summary>One chapter-rail row after a drop: its ids and the rows nested under it.</summary>
public sealed record RailNodeSnapshot(string ChapterId, string? SegmentId, IReadOnlyList<RailNodeSnapshot> Children);

public enum RailReorderKind
{
    None,
    Chapter,
    Segment,
    Rejected,
}

/// <summary>What a drag-and-drop in the chapter rail asks the project to do.</summary>
public sealed record RailReorderPlan(RailReorderKind Kind, string? ChapterId, string? SegmentId, int Offset, string? Reason)
{
    public static RailReorderPlan NoChange { get; } = new(RailReorderKind.None, null, null, 0, null);

    public static RailReorderPlan Rejected(string reason) => new(RailReorderKind.Rejected, null, null, 0, reason);
}

/// <summary>
/// Turns the rail's tree after a drop into one move of the project's chapter or segment order.
/// The rail hides archived chapters, so offsets are computed against the full stored order.
/// </summary>
public static class RailReorder
{
    public const string SegmentOutsideChapter = "Segments stay inside a chapter. Drop the segment between two segments of its chapter.";
    public const string ChapterInsideChapter = "Chapters can't go inside another chapter. Drop the chapter above or below another chapter.";
    public const string SegmentInsideSegment = "Drop the segment between two segments, not onto one.";
    public const string SegmentToOtherChapter = "Segments can only be reordered within their own chapter. Moving a segment to another chapter isn't supported yet.";
    public const string RailChanged = "The chapter rail changed while you were dragging. Try the move again.";

    /// <param name="draggedId">The dragged chapter or segment, when known; it settles adjacent swaps.</param>
    public static RailReorderPlan Plan(VoiceBenchProjectDocument project, IReadOnlyList<RailNodeSnapshot> roots, string? draggedId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(roots);

        foreach (RailNodeSnapshot root in roots)
        {
            if (root.SegmentId is not null) return RailReorderPlan.Rejected(SegmentOutsideChapter);
            foreach (RailNodeSnapshot child in root.Children)
            {
                if (child.SegmentId is null) return RailReorderPlan.Rejected(ChapterInsideChapter);
                if (child.Children.Count > 0) return RailReorderPlan.Rejected(SegmentInsideSegment);
                if (!string.Equals(child.ChapterId, root.ChapterId, StringComparison.Ordinal))
                    return RailReorderPlan.Rejected(SegmentToOtherChapter);
            }
        }

        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(item => item.ChapterId, StringComparer.Ordinal);
        HashSet<string> segmentIds = project.Segments.Select(item => item.SegmentId).ToHashSet(StringComparer.Ordinal);
        List<string> visibleChapters = project.ChapterOrder
            .Where(id => chapters.TryGetValue(id, out ChapterDocument? chapter) && chapter.Status != ChapterStatus.Archived)
            .ToList();
        List<string> droppedChapters = roots.Select(root => root.ChapterId).ToList();
        if (!SameItems(visibleChapters, droppedChapters)) return RailReorderPlan.Rejected(RailChanged);

        List<RailReorderPlan> moves = [];
        if (!visibleChapters.SequenceEqual(droppedChapters, StringComparer.Ordinal))
        {
            if (MovedItem(visibleChapters, droppedChapters, draggedId) is not string chapterId) return RailReorderPlan.Rejected(RailChanged);
            moves.Add(new(RailReorderKind.Chapter, chapterId, null, FullOrderOffset(project.ChapterOrder, droppedChapters, chapterId), null));
        }

        foreach (RailNodeSnapshot root in roots)
        {
            ChapterDocument chapter = chapters[root.ChapterId];
            List<string> visibleSegments = chapter.SegmentOrder.Where(segmentIds.Contains).ToList();
            List<string> droppedSegments = root.Children.Select(child => child.SegmentId!).ToList();
            if (!SameItems(visibleSegments, droppedSegments)) return RailReorderPlan.Rejected(RailChanged);
            if (visibleSegments.SequenceEqual(droppedSegments, StringComparer.Ordinal)) continue;
            if (MovedItem(visibleSegments, droppedSegments, draggedId) is not string segmentId) return RailReorderPlan.Rejected(RailChanged);
            moves.Add(new(RailReorderKind.Segment, chapter.ChapterId, segmentId, FullOrderOffset(chapter.SegmentOrder, droppedSegments, segmentId), null));
        }

        return moves.Count switch
        {
            0 => RailReorderPlan.NoChange,
            1 => moves[0].Offset == 0 ? RailReorderPlan.NoChange : moves[0],
            _ => RailReorderPlan.Rejected(RailChanged),
        };
    }

    /// <summary>
    /// The one item whose move turns <paramref name="before"/> into <paramref name="after"/>; null if none or
    /// several moved. Swapping neighbours can be read either way, so <paramref name="preferred"/> wins when it fits.
    /// </summary>
    public static string? MovedItem(IReadOnlyList<string> before, IReadOnlyList<string> after, string? preferred = null)
    {
        if (before.Count != after.Count) return null;
        if (preferred is not null && before.Contains(preferred) && !before.SequenceEqual(after)
            && before.Where(item => item != preferred).SequenceEqual(after.Where(item => item != preferred)))
            return preferred;
        int first = 0;
        while (first < before.Count && before[first] == after[first]) first++;
        if (first == before.Count) return null;
        int last = before.Count - 1;
        while (before[last] == after[last]) last--;

        // Moved later: before[first] now sits at `last` and everything between shifted up one.
        if (after[last] == before[first] && Enumerable.Range(first, last - first).All(index => after[index] == before[index + 1]))
            return before[first];
        // Moved earlier: before[last] now sits at `first` and everything between shifted down one.
        if (after[first] == before[last] && Enumerable.Range(first + 1, last - first).All(index => after[index] == before[index - 1]))
            return before[last];
        return null;
    }

    /// <summary>
    /// The offset, in a stored order that may hold ids the rail does not show, that places
    /// <paramref name="id"/> next to the same visible neighbours it has in <paramref name="visibleAfter"/>.
    /// </summary>
    public static int FullOrderOffset(IReadOnlyList<string> fullOrder, IReadOnlyList<string> visibleAfter, string id)
    {
        int current = IndexOf(fullOrder, id);
        if (current < 0) throw new ArgumentException($"'{id}' is not in the stored order.", nameof(id));
        List<string> without = fullOrder.Where(item => item != id).ToList();
        int position = IndexOf(visibleAfter, id);
        int target = position + 1 < visibleAfter.Count
            ? without.IndexOf(visibleAfter[position + 1])
            : position > 0 ? without.IndexOf(visibleAfter[position - 1]) + 1 : current;
        return target - current;
    }

    private static bool SameItems(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static int IndexOf(IReadOnlyList<string> items, string id)
    {
        for (int index = 0; index < items.Count; index++)
            if (items[index] == id) return index;
        return -1;
    }
}
