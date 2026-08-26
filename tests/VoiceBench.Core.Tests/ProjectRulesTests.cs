using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ProjectRulesTests
{
    private static readonly DateTimeOffset ApprovalTime = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Source_hash_is_stable_across_line_endings()
    {
        Assert.Equal(SourceTextHasher.Compute("one\ntwo"), SourceTextHasher.Compute("one\r\ntwo"));
    }

    [Fact]
    public void Changed_approved_source_marks_take_and_segment_stale()
    {
        var originalHash = SourceTextHasher.Compute("Original");
        var segment = CreateApprovedSegment(originalHash);

        var changed = SegmentStateRules.WithSourceHash(segment, SourceTextHasher.Compute("Changed"));

        Assert.Equal(SegmentStatus.Stale, changed.Status);
        Assert.Null(changed.ApprovedRenderId);
        var render = Assert.Single(changed.RenderHistory);
        Assert.Equal(RenderStatus.Stale, render.Status);
        Assert.Null(render.ApprovalSourceSha256);
        Assert.Null(render.ApprovedAt);
    }

    [Fact]
    public void Same_source_hash_preserves_the_document_instance()
    {
        var hash = SourceTextHasher.Compute("Original");
        var segment = CreateApprovedSegment(hash);

        Assert.Same(segment, SegmentStateRules.WithSourceHash(segment, hash.ToUpperInvariant()));
    }

    [Fact]
    public void Stale_take_requires_explicit_reapproval()
    {
        var originalHash = SourceTextHasher.Compute("Original");
        var changed = SegmentStateRules.WithSourceHash(
            CreateApprovedSegment(originalHash),
            SourceTextHasher.Compute("Changed"));
        var renderId = Assert.Single(changed.RenderHistory).RenderId;

        Assert.Throws<InvalidOperationException>(() =>
            SegmentStateRules.ApproveRender(changed, renderId, ApprovalTime));

        var approved = SegmentStateRules.ApproveRender(
            changed,
            renderId,
            ApprovalTime,
            explicitlyApproveStaleSource: true);

        var render = Assert.Single(approved.RenderHistory);
        Assert.Equal(originalHash, render.SourceSha256);
        Assert.Equal(changed.SourceSha256, render.ApprovalSourceSha256);
        Assert.Equal(SegmentStatus.Approved, approved.Status);
    }

    [Fact]
    public void Mastering_is_blocked_after_source_change_and_allowed_after_explicit_reapproval()
    {
        var originalHash = SourceTextHasher.Compute("Original");
        var project = CreateProject(CreateApprovedSegment(originalHash));
        Assert.True(MasterEligibilityEvaluator.Evaluate(project).CanBuild);

        var stale = SegmentStateRules.WithSourceHash(
            project.Segments[0],
            SourceTextHasher.Compute("Changed"));
        var staleProject = CreateProject(stale);

        var blocked = MasterEligibilityEvaluator.Evaluate(staleProject);
        Assert.False(blocked.CanBuild);
        Assert.Contains(blocked.Issues, issue => issue.Code == "master.segment-unapproved");

        var reapproved = SegmentStateRules.ApproveRender(
            stale,
            stale.RenderHistory[0].RenderId,
            ApprovalTime,
            explicitlyApproveStaleSource: true);
        Assert.True(MasterEligibilityEvaluator.Evaluate(CreateProject(reapproved)).CanBuild);
    }

    [Fact]
    public void Default_preset_requires_two_distinct_auditions()
    {
        var audition = Guid.NewGuid();

        Assert.False(VoicePresetRules.CanSelectAsProjectDefault(
            new VoicePreset { AuditionRenderIds = [audition, audition] }));
        Assert.True(VoicePresetRules.CanSelectAsProjectDefault(
            new VoicePreset { AuditionRenderIds = [audition, Guid.NewGuid()] }));
    }

    private static SegmentDocument CreateApprovedSegment(string hash)
    {
        var renderId = Guid.Parse("c738ff2f-4791-431a-9799-14275ab71045");
        return new SegmentDocument
        {
            SegmentId = "intro",
            Title = "Introduction",
            ScriptRelativePath = "02-scripts/001-introduction--a1b2c3.md",
            SourceSha256 = hash,
            Status = SegmentStatus.Approved,
            ApprovedRenderId = renderId,
            RenderHistory =
            [
                new RenderRecord
                {
                    RenderId = renderId,
                    TakeNumber = 1,
                    Status = RenderStatus.Approved,
                    SourceSha256 = hash,
                    ApprovalSourceSha256 = hash,
                    ApprovedAt = ApprovalTime,
                    RawAudioRelativePath = "04-generated-audio/segments/intro-take-001.raw.wav",
                    ProcessedAudioRelativePath = "04-generated-audio/segments/intro-take-001.wav",
                },
            ],
        };
    }

    private static VoiceBenchProjectDocument CreateProject(SegmentDocument segment) => new()
    {
        Name = "Test",
        SegmentOrder = [segment.SegmentId],
        Segments = [segment],
    };
}
