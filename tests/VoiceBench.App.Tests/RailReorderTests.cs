using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class RailReorderTests
{
    [Fact]
    public void An_unchanged_rail_needs_no_move()
    {
        Assert.Equal(RailReorderKind.None, RailReorder.Plan(Project(), Rail(("alpha", ["a1", "a2", "a3"]), ("beta", ["b1"]))).Kind);
    }

    [Theory]
    [InlineData(new[] { "a2", "a1", "a3" }, "a2", -1)]
    [InlineData(new[] { "a3", "a1", "a2" }, "a3", -2)]
    [InlineData(new[] { "a2", "a3", "a1" }, "a1", 2)]
    [InlineData(new[] { "a1", "a3", "a2" }, "a3", -1)]
    public void Dragging_a_segment_within_its_chapter_moves_it_there(string[] dropped, string moved, int offset)
    {
        VoiceBenchProjectDocument project = Project();
        RailReorderPlan plan = RailReorder.Plan(project, Rail(("alpha", dropped), ("beta", ["b1"])), draggedId: moved);

        Assert.Equal(RailReorderKind.Segment, plan.Kind);
        Assert.Equal("alpha", plan.ChapterId);
        Assert.Equal(moved, plan.SegmentId);
        Assert.Equal(offset, plan.Offset);
        Assert.Equal(dropped, Move(project.Chapters[0].SegmentOrder, moved, offset));
    }

    [Fact]
    public void Dragging_a_chapter_skips_archived_chapters_the_rail_does_not_show()
    {
        // Stored order: alpha, archived, beta. The rail shows alpha, beta; beta is dragged above alpha.
        VoiceBenchProjectDocument project = Project();
        RailReorderPlan plan = RailReorder.Plan(project, Rail(("beta", ["b1"]), ("alpha", ["a1", "a2", "a3"])), draggedId: "beta");

        Assert.Equal(RailReorderKind.Chapter, plan.Kind);
        Assert.Equal("beta", plan.ChapterId);
        Assert.Equal(["beta", "alpha", "archived"], Move(project.ChapterOrder, "beta", plan.Offset));
    }

    [Fact]
    public void Swapping_neighbours_moves_the_dragged_one()
    {
        VoiceBenchProjectDocument project = Project();
        List<RailNodeSnapshot> rail = Rail(("beta", ["b1"]), ("alpha", ["a1", "a2", "a3"]));

        RailReorderPlan draggedAlpha = RailReorder.Plan(project, rail, draggedId: "alpha");

        Assert.Equal("alpha", draggedAlpha.ChapterId);
        Assert.Equal(["archived", "beta", "alpha"], Move(project.ChapterOrder, "alpha", draggedAlpha.Offset));
        Assert.Equal(RailReorderKind.Chapter, RailReorder.Plan(project, rail).Kind);
    }

    [Fact]
    public void Dragging_a_chapter_down_lands_after_its_new_neighbour()
    {
        VoiceBenchProjectDocument project = Project() with { ChapterOrder = ["beta", "archived", "alpha"] };
        RailReorderPlan plan = RailReorder.Plan(project, Rail(("alpha", ["a1", "a2", "a3"]), ("beta", ["b1"])), draggedId: "beta");

        Assert.Equal(RailReorderKind.Chapter, plan.Kind);
        Assert.Equal(["archived", "alpha", "beta"], Move(project.ChapterOrder, "beta", plan.Offset));
    }

    [Fact]
    public void Segments_moved_to_another_chapter_are_refused()
    {
        RailReorderPlan plan = RailReorder.Plan(Project(), Rail(("alpha", ["a1", "a2"]), ("beta", ["b1", "a3"])));

        Assert.Equal(RailReorderKind.Rejected, plan.Kind);
        Assert.Equal(RailReorder.SegmentToOtherChapter, plan.Reason);
    }

    [Fact]
    public void A_segment_dropped_at_the_top_level_is_refused()
    {
        List<RailNodeSnapshot> rail = [.. Rail(("alpha", ["a1", "a2"]), ("beta", ["b1"])), new("alpha", "a3", [])];

        Assert.Equal(RailReorder.SegmentOutsideChapter, RailReorder.Plan(Project(), rail).Reason);
    }

    [Fact]
    public void A_chapter_dropped_inside_another_chapter_is_refused()
    {
        RailNodeSnapshot beta = new("beta", null, [new("beta", "b1", [])]);
        List<RailNodeSnapshot> rail = [new("alpha", null, [new("alpha", "a1", []), new("alpha", "a2", []), new("alpha", "a3", []), beta])];

        Assert.Equal(RailReorder.ChapterInsideChapter, RailReorder.Plan(Project(), rail).Reason);
    }

    [Fact]
    public void A_segment_dropped_onto_another_segment_is_refused()
    {
        List<RailNodeSnapshot> rail =
        [
            new("alpha", null, [new("alpha", "a1", [new("alpha", "a2", [])]), new("alpha", "a3", [])]),
            new("beta", null, [new("beta", "b1", [])]),
        ];

        Assert.Equal(RailReorder.SegmentInsideSegment, RailReorder.Plan(Project(), rail).Reason);
    }

    [Fact]
    public void A_rail_that_no_longer_matches_the_project_is_refused()
    {
        Assert.Equal(RailReorder.RailChanged, RailReorder.Plan(Project(), Rail(("alpha", ["a1", "a2"]), ("beta", ["b1"]))).Reason);
    }

    [Theory]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "b", "c", "a", "d" }, "a")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "a", "d", "b", "c" }, "d")]
    [InlineData(new[] { "a", "b" }, new[] { "b", "a" }, "a")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "b", "a", "d", "c" }, null)]
    [InlineData(new[] { "a", "b" }, new[] { "a", "b" }, null)]
    public void Moved_item_is_found_only_for_a_single_move(string[] before, string[] after, string? expected)
    {
        Assert.Equal(expected, RailReorder.MovedItem(before, after));
    }

    [Fact]
    public void A_preferred_item_is_used_only_when_its_move_explains_the_change()
    {
        Assert.Equal("b", RailReorder.MovedItem(["a", "b"], ["b", "a"], preferred: "b"));
        Assert.Equal("a", RailReorder.MovedItem(["a", "b", "c"], ["b", "c", "a"], preferred: "b"));
        Assert.Null(RailReorder.MovedItem(["a", "b"], ["a", "b"], preferred: "a"));
    }

    private static List<string> Move(IReadOnlyList<string> order, string id, int offset)
    {
        // Mirrors ChapterAuthoringService.Move, which applies the plan.
        List<string> result = [.. order];
        int current = result.IndexOf(id);
        int target = Math.Clamp(current + offset, 0, result.Count - 1);
        result.RemoveAt(current);
        result.Insert(target, id);
        return result;
    }

    private static List<RailNodeSnapshot> Rail(params (string Chapter, string[] Segments)[] chapters) =>
        chapters.Select(chapter => new RailNodeSnapshot(chapter.Chapter, null,
            chapter.Segments.Select(segment => new RailNodeSnapshot(segment[0] == 'a' ? "alpha" : "beta", segment, [])).ToList())).ToList();

    private static VoiceBenchProjectDocument Project() => new()
    {
        ChapterOrder = ["alpha", "archived", "beta"],
        Chapters =
        [
            new() { ChapterId = "alpha", Title = "Alpha", SegmentOrder = ["a1", "a2", "a3"] },
            new() { ChapterId = "archived", Title = "Old", Status = ChapterStatus.Archived, SegmentOrder = ["x1"] },
            new() { ChapterId = "beta", Title = "Beta", SegmentOrder = ["b1"] },
        ],
        Segments =
        [
            new() { SegmentId = "a1", ChapterId = "alpha", Title = "One" },
            new() { SegmentId = "a2", ChapterId = "alpha", Title = "Two" },
            new() { SegmentId = "a3", ChapterId = "alpha", Title = "Three" },
            new() { SegmentId = "x1", ChapterId = "archived", Title = "Old" },
            new() { SegmentId = "b1", ChapterId = "beta", Title = "Four" },
        ],
    };
}
