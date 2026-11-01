using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class TimelinePresentationTests
{
    [Fact]
    public void Audio_only_manifest_changes_preserve_the_live_script_workspace_but_source_changes_do_not()
    {
        SegmentDocument segment = new() { SegmentId = "narration",SourceSha256 = "script" };
        VoiceBenchProjectDocument before = new() { Segments = [segment] };
        var audioEdit = before with { UpdatedAt = DateTimeOffset.UtcNow,Segments = [segment with { }],AudioAssets = [new() { Name = "music" }],AudioTimelines = [new() { Revision = 2 }] };
        Assert.True(TimelinePresentation.SameScriptWorkspace(before,audioEdit));
        Assert.False(TimelinePresentation.SameScriptWorkspace(before,audioEdit with { Segments = [segment with { SourceSha256 = "changed" }] }));
        Assert.False(TimelinePresentation.SameScriptWorkspace(before,audioEdit with { Segments = [segment with { RenderHistory = [new()] }] }));
    }

    [Fact]
    public void Gain_pan_and_mute_edits_reuse_peak_geometry_while_source_changes_invalidate_it()
    {
        AudioClipDocument clip = new() { AssetId = Guid.NewGuid(),AssetSha256 = "pinned",DurationSeconds = 5 };
        AudioTimelineDocument original = new() { Clips = [clip] };
        Assert.True(TimelinePresentation.SameWaveformGeometry(original,original with { Revision = 1,Clips = [clip with { GainDb = -6,Pan = .5,Muted = true }] }));
        Assert.False(TimelinePresentation.SameWaveformGeometry(original,original with { Clips = [clip with { SourceStartSeconds = 1 }] }));
        Assert.False(TimelinePresentation.SameWaveformGeometry(original,original with { Clips = [clip with { AssetSha256 = "replacement" }] }));
    }

    [Fact]
    public void Word_snaps_require_the_pinned_artifact_and_skip_uncertain_gap_edges()
    {
        Guid renderId = Guid.NewGuid();
        AudioClipDocument clip = new() { RenderId = renderId,AssetSha256 = "audio",SourceSha256 = "source",StartSeconds = 10,DurationSeconds = 4 };
        WordAlignmentDocument alignment = new() { RenderId = renderId,AudioSha256 = "audio",SourceSha256 = "source",TotalWordCount = 3,
            Words = [new("first",TimeSpan.Zero,TimeSpan.FromSeconds(1),0,0),new("last",TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(4),2,2)] };
        Assert.Equal(new[] { 10d,14d },TimelinePresentation.ReliableWordSnapPositions(clip,alignment,4));
        Assert.Empty(TimelinePresentation.ReliableWordSnapPositions(clip,alignment with { AudioSha256 = "another-take" },4));
        Assert.Empty(TimelinePresentation.ReliableWordSnapPositions(clip,alignment with { SourceSha256 = "another-script" },4));
        Assert.Empty(TimelinePresentation.ReliableWordSnapPositions(clip,alignment with { RenderId = Guid.NewGuid() },4));
    }

    [Fact]
    public void Ruler_ticks_follow_visible_offset_and_share_grid_step()
    {
        var ticks = TimelinePresentation.RulerTicks(63,40);
        Assert.Equal(65,ticks[0]);
        Assert.All(ticks,time => Assert.InRange(time,63,103));
        Assert.All(ticks.Zip(ticks.Skip(1)),pair => Assert.Equal(TimelinePresentation.GridStep(40),pair.Second-pair.First));
    }

    [Fact]
    public void Ruler_reduces_label_density_for_narrow_and_long_duration_viewports()
    {
        foreach (double duration in new[] { .4,40,7200 })
        {
            double step = TimelinePresentation.GridStep(duration,300);
            Assert.True(step / duration * 300 >= 100);
            Assert.True(TimelinePresentation.RulerTicks(0,duration,300).Count <= 4);
        }
        Assert.True(TimelinePresentation.RulerTicks(0,40,300).Count < TimelinePresentation.RulerTicks(0,40,1000).Count);
    }

    [Fact]
    public void Cues_map_through_clip_source_offset_and_uncertain_removed_or_stale_anchors_stay_unplaced()
    {
        ProductionCue cue = new(0,ProductionCueKind.Visual,"visual","Show the mix",1,0,20,2);
        MixRenderClip clip = new() { StartSeconds = 20,SourceStartSeconds = 2,DurationSeconds = 5,
            RetainedWordRanges = [new(1,4)],AlignedWords = [new("mix",TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(3.5),2,2)] };
        Assert.Equal(21,TimelinePresentation.PlaceCue(clip,cue,true).PositionSeconds);
        Assert.Null(TimelinePresentation.PlaceCue(clip,cue,false).PositionSeconds);
        Assert.Contains("Stale",TimelinePresentation.PlaceCue(clip,cue,false).Status);
        Assert.Null(TimelinePresentation.PlaceCue(clip with { AlignedWords = [] },cue,true).PositionSeconds);
        Assert.Contains("Uncertain",TimelinePresentation.PlaceCue(clip with { AlignedWords = [] },cue,true).Status);
        Assert.Contains("Removed",TimelinePresentation.PlaceCue(clip with { RetainedWordRanges = [new(3,4)] },cue,true).Status);
    }

    [Fact]
    public void Follow_currency_retains_cue_only_drafts_and_blocks_changed_narration_or_saved_source()
    {
        var parser = new ProductionScriptParser();
        var original = parser.Parse("Welcome to the studio.");
        string pin = SourceTextHasher.Compute(original.NarrationText);
        Assert.True(TimelinePresentation.CanFollow(pin,pin,parser.Parse("Welcome to the studio.\n@visual: show the mixer")));
        Assert.False(TimelinePresentation.CanFollow(pin,pin,parser.Parse("Welcome to a changed studio.")));
        Assert.False(TimelinePresentation.CanFollow(pin,SourceTextHasher.Compute("A new saved source."),original));
    }

    [Fact]
    public void Moving_multiple_clips_preserves_spacing_and_clamps_group_at_zero()
    {
        AudioTrackDocument track = new();
        AudioClipDocument first = new() { TrackId = track.TrackId, StartSeconds = 2, DurationSeconds = 3 };
        AudioClipDocument second = new() { TrackId = track.TrackId, StartSeconds = 7, DurationSeconds = 2 };
        AudioTimelineDocument timeline = new() { Tracks = [track], Clips = [first, second] };
        var edits = TimelinePresentation.MoveSelection(timeline, new HashSet<Guid> { first.ClipId, second.ClipId }, -10);
        Assert.Equal(0, edits[0].StartSeconds);
        Assert.Equal(5, edits[1].StartSeconds);
    }

    [Fact]
    public void Locked_selection_cannot_move_and_snap_tolerance_does_not_attract_distant_edges()
    {
        AudioTrackDocument track = new() { Locked = true };
        AudioClipDocument clip = new() { TrackId = track.TrackId };
        Assert.Throws<InvalidOperationException>(() => TimelinePresentation.MoveSelection(
            new() { Tracks = [track], Clips = [clip] }, new HashSet<Guid> { clip.ClipId }, 1));
        Assert.Equal(5, TimelinePresentation.Snap(4.98, .03, [0, 5, 9]));
        Assert.Equal(4.8, TimelinePresentation.Snap(4.8, .03, [0, 5, 9]));
    }

    [Fact]
    public void Viewport_culls_clips_outside_visible_time_and_includes_trimmed_intersections()
    {
        AudioClipDocument clip = new() { StartSeconds = 10, DurationSeconds = 4 };
        Assert.True(TimelinePresentation.Intersects(clip, 12, 3));
        Assert.False(TimelinePresentation.Intersects(clip, 14, 3));
        Assert.False(TimelinePresentation.Intersects(clip, 0, 10));
    }
}
