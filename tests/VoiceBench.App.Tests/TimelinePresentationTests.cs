using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class TimelinePresentationTests
{
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
