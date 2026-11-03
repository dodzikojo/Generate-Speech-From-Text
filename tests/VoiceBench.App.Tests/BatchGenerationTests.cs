using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class BatchGenerationTests
{
    [Fact]
    public void From_here_runs_to_the_end_of_the_segments_chapter()
    {
        VoiceBenchProjectDocument project = Project();

        Assert.Equal(["b2", "b3"], BatchGenerationPlanner.FromHereToChapterEnd(project, "b2"));
        Assert.Equal(["a1", "a2"], BatchGenerationPlanner.FromHereToChapterEnd(project, "a1"));
        Assert.Equal(["b3"], BatchGenerationPlanner.FromHereToChapterEnd(project, "b3"));
        Assert.Throws<ArgumentException>(() => BatchGenerationPlanner.FromHereToChapterEnd(project, "missing"));
    }

    [Fact]
    public void Whole_chapter_lists_every_segment_in_chapter_order()
    {
        Assert.Equal(["b1", "b2", "b3"], BatchGenerationPlanner.WholeChapter(Project(), "chapter-b"));
        Assert.Throws<ArgumentException>(() => BatchGenerationPlanner.WholeChapter(Project(), "missing"));
    }

    [Fact]
    public void Candidates_follow_rail_order_whatever_order_they_were_picked_in()
    {
        IReadOnlyList<BatchCandidate> candidates = BatchGenerationPlanner.Candidates(
            Project(), ["b3", "a2", "b1", "unknown", "b1"]);

        Assert.Equal(["a2", "b1", "b3"], candidates.Select(item => item.SegmentId).ToArray());
        Assert.Equal([2, 1, 3], candidates.Select(item => item.Number).ToArray());
        Assert.Equal("002  Second", candidates[0].DisplayTitle);
        Assert.Equal("Beta", candidates[1].ChapterTitle);
    }

    [Fact]
    public void Segments_in_archived_chapters_are_not_offered()
    {
        VoiceBenchProjectDocument project = Project();
        project = project with
        {
            Chapters = project.Chapters.Select(chapter => chapter.ChapterId == "chapter-a" ? chapter with { Status = ChapterStatus.Archived } : chapter).ToList(),
        };

        Assert.Equal(["b1"], BatchGenerationPlanner.Candidates(project, ["a1", "b1"]).Select(item => item.SegmentId).ToArray());
    }

    [Fact]
    public void Approved_segments_are_offered_but_not_checked_so_approval_is_not_lost_by_accident()
    {
        BatchCandidate approved = BatchGenerationPlanner.Candidates(Project(), ["a1"]).Single();

        Assert.True(approved.CanGenerate);
        Assert.False(approved.CheckedByDefault);
        Assert.Contains("approved", approved.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("a2", true, "Ready")]
    [InlineData("b1", true, "Take 02 awaiting review")]
    [InlineData("b2", true, "Script changed since approval")]
    public void Unapproved_segments_are_checked_by_default(string segmentId, bool expected, string detail)
    {
        BatchCandidate candidate = BatchGenerationPlanner.Candidates(Project(), [segmentId]).Single();

        Assert.Equal(expected, candidate.CheckedByDefault);
        Assert.StartsWith(detail, candidate.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Segments_already_rendering_or_with_script_problems_cannot_be_checked()
    {
        IReadOnlyList<BatchCandidate> candidates = BatchGenerationPlanner.Candidates(
            Project(),
            ["b3", "a2"],
            scriptProblem: segment => segment.SegmentId == "a2" ? "No narration to speak" : null);

        BatchCandidate rendering = candidates.Single(item => item.SegmentId == "b3");
        BatchCandidate empty = candidates.Single(item => item.SegmentId == "a2");
        Assert.False(rendering.CanGenerate);
        Assert.False(rendering.CheckedByDefault);
        Assert.False(empty.CanGenerate);
        Assert.Equal("No narration to speak", empty.Problem);
        Assert.StartsWith("No narration to speak", empty.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Segments_with_their_own_voice_are_flagged_because_they_switch_to_the_project_voice()
    {
        IReadOnlyList<BatchCandidate> candidates = BatchGenerationPlanner.Candidates(Project(), ["a2", "b1"]);

        Assert.True(candidates.Single(item => item.SegmentId == "b1").UsesOtherVoice);
        Assert.False(candidates.Single(item => item.SegmentId == "a2").UsesOtherVoice);
        Assert.Contains("project voice", candidates.Single(item => item.SegmentId == "b1").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Estimates_are_shown_and_totalled_for_checked_candidates()
    {
        Dictionary<string, TimeSpan> estimates = new(StringComparer.Ordinal)
        {
            ["a2"] = TimeSpan.FromSeconds(42),
            ["b1"] = TimeSpan.FromSeconds(80),
        };
        IReadOnlyList<BatchCandidate> candidates = BatchGenerationPlanner.Candidates(Project(), ["a2", "b1"], estimates);

        Assert.EndsWith("00:42 est.", candidates[0].Detail, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(122), BatchGenerationPlanner.TotalEstimate(candidates));
    }

    [Theory]
    [InlineData("Plain narration.", null)]
    [InlineData("", "No narration to speak")]
    [InlineData("   \n  ", "No narration to speak")]
    public void Script_check_reports_scripts_that_cannot_be_rendered(string script, string? expected)
    {
        Assert.Equal(expected, BatchGenerationPlanner.ScriptProblem(script));
    }

    [Theory]
    [InlineData("provider.validation", 1, false)]
    [InlineData("provider.policy", 1, false)]
    [InlineData("operation.invalid", 1, false)]
    [InlineData("render_invalid", 2, false)]
    [InlineData("render_invalid", 3, true)]
    [InlineData("provider.credentials", 1, true)]
    [InlineData("provider.authentication", 1, true)]
    [InlineData("provider.ratelimited", 1, true)]
    [InlineData("provider.transient", 1, true)]
    [InlineData("external_conflict", 1, true)]
    [InlineData("filesystem.error", 1, true)]
    [InlineData(null, 1, true)]
    public void Account_and_system_failures_stop_the_batch_but_segment_problems_do_not(string? code, int consecutive, bool stops)
    {
        Assert.Equal(stops, BatchGenerationRules.ShouldStop(code, consecutive));
    }

    [Theory]
    [InlineData("provider.credentials", "Settings")]
    [InlineData("provider.authentication", "Replace")]
    [InlineData("provider.ratelimited", "rate")]
    [InlineData("provider.transient", "connection")]
    public void Account_failures_get_an_actionable_message(string code, string expected)
    {
        Assert.Contains(expected, BatchGenerationRules.FailureMessage(code, "The speech provider rejected the operation."), StringComparison.Ordinal);
    }

    [Fact]
    public void Other_failures_keep_the_job_message()
    {
        Assert.Equal("Segment 'x' contains no narration to render.", BatchGenerationRules.FailureMessage("operation.invalid", "Segment 'x' contains no narration to render."));
    }

    [Fact]
    public void Summary_for_a_clean_run_points_to_review()
    {
        BatchGenerationSummary summary = new BatchGenerationOutcome(3, 3, [], NotStarted: 0, Cancelled: false, StopFailure: null).Summarize();

        Assert.Equal(BatchGenerationSeverity.Success, summary.Severity);
        Assert.Equal("Generated 3 takes", summary.Title);
        Assert.Contains("approve", summary.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_lists_segment_failures_that_did_not_stop_the_run()
    {
        BatchGenerationSummary summary = new BatchGenerationOutcome(
            3, 2, [new BatchGenerationFailure("002  Second", "Too long for the model.")], NotStarted: 0, Cancelled: false, StopFailure: null).Summarize();

        Assert.Equal(BatchGenerationSeverity.Warning, summary.Severity);
        Assert.Equal("Generated 2 of 3 takes", summary.Title);
        Assert.Contains("002  Second: Too long for the model.", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_explains_a_stopped_run_and_what_was_not_started()
    {
        BatchGenerationFailure failure = new("001  First", "Add the provider key in Settings.");
        BatchGenerationSummary summary = new BatchGenerationOutcome(4, 1, [failure], NotStarted: 2, Cancelled: false, StopFailure: failure).Summarize();

        Assert.Equal(BatchGenerationSeverity.Error, summary.Severity);
        Assert.Equal("Generation stopped after 1 of 4 takes", summary.Title);
        Assert.Contains("Add the provider key in Settings.", summary.Message, StringComparison.Ordinal);
        Assert.Contains("2 segments were not generated", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_for_a_cancelled_run_keeps_generated_takes()
    {
        BatchGenerationSummary summary = new BatchGenerationOutcome(5, 2, [], NotStarted: 3, Cancelled: true, StopFailure: null).Summarize();

        Assert.Equal(BatchGenerationSeverity.Warning, summary.Severity);
        Assert.Equal("Generation stopped", summary.Title);
        Assert.Contains("Generated 2 of 5 takes", summary.Message, StringComparison.Ordinal);
        Assert.Contains("3 segments were not generated", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_uses_singular_wording_for_one_take()
    {
        Assert.Equal("Generated 1 take", new BatchGenerationOutcome(1, 1, [], 0, false, null).Summarize().Title);
        Assert.Contains("1 segment was not generated", new BatchGenerationOutcome(2, 1, [], 1, true, null).Summarize().Message, StringComparison.Ordinal);
    }

    private static VoiceBenchProjectDocument Project() => new()
    {
        DefaultVoicePresetId = "project-voice",
        ChapterOrder = ["chapter-a", "chapter-b"],
        Chapters =
        [
            new() { ChapterId = "chapter-a", Title = "Alpha", SegmentOrder = ["a1", "a2"] },
            new() { ChapterId = "chapter-b", Title = "Beta", SegmentOrder = ["b1", "b2", "b3"] },
        ],
        Segments =
        [
            new() { SegmentId = "a1", ChapterId = "chapter-a", Title = "First", Status = SegmentStatus.Approved },
            new() { SegmentId = "a2", ChapterId = "chapter-a", Title = "Second", Status = SegmentStatus.Ready, VoicePresetId = "project-voice" },
            new()
            {
                SegmentId = "b1", ChapterId = "chapter-b", Title = "Third", Status = SegmentStatus.Rendered, VoicePresetId = "other-voice",
                RenderHistory = [new() { TakeNumber = 1 }, new() { TakeNumber = 2 }],
            },
            new() { SegmentId = "b2", ChapterId = "chapter-b", Title = "Fourth", Status = SegmentStatus.Stale },
            new() { SegmentId = "b3", ChapterId = "chapter-b", Title = "Fifth", Status = SegmentStatus.Rendering },
        ],
    };
}
