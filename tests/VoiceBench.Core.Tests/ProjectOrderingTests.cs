using IssueLab.VoiceBench.Core;

namespace VoiceBench.Core.Tests;

public sealed class ProjectOrderingTests
{
    [Fact]
    public void Compilation_order_uses_included_active_chapters_then_their_segment_order()
    {
        VoiceBenchProjectDocument project = new()
        {
            ChapterOrder = ["chapter-b", "chapter-a", "archive"],
            Chapters =
            [
                new ChapterDocument { ChapterId = "chapter-a", Title = "A", SegmentOrder = ["a1"] },
                new ChapterDocument { ChapterId = "chapter-b", Title = "B", SegmentOrder = ["b1", "b2"] },
                new ChapterDocument { ChapterId = "archive", Title = "Old", Status = ChapterStatus.Archived, SegmentOrder = ["x"] },
            ],
        };

        IReadOnlyList<string> result = ProjectOrdering.GetOrderedSegmentIds(project);

        Assert.Equal(["b1", "b2", "a1"], result);
    }

    [Fact]
    public void Compilation_order_omits_chapters_not_included()
    {
        VoiceBenchProjectDocument project = new()
        {
            ChapterOrder = ["yes", "no"],
            Chapters =
            [
                new ChapterDocument { ChapterId = "yes", Title = "Yes", SegmentOrder = ["one"] },
                new ChapterDocument { ChapterId = "no", Title = "No", IncludedInCompilation = false, SegmentOrder = ["two"] },
            ],
        };

        Assert.Equal(["one"], ProjectOrdering.GetOrderedSegmentIds(project));
        Assert.Equal(["two"], ProjectOrdering.GetOrderedSegmentIds(project, MasterScope.Chapter, "no"));
        Assert.Equal(["one", "two"], ProjectOrdering.GetPlaybackOrderedSegmentIds(project));
    }
}
