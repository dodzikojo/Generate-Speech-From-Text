using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class RailRowActionsTests
{
    private static readonly Guid Take1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Take2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Take3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public void A_segment_without_takes_offers_nothing_to_play_or_approve()
    {
        SegmentDocument segment = Segment(SegmentStatus.Ready);

        Assert.Null(RailRowActions.TakeToPlay(segment));
        Assert.Null(RailRowActions.TakeToApprove(segment));
    }

    [Fact]
    public void The_newest_take_awaiting_review_is_both_played_and_approved()
    {
        SegmentDocument segment = Segment(SegmentStatus.Rendered, approved: Take1,
            Take(Take1, 1, RenderStatus.Approved), Take(Take2, 2, RenderStatus.Generated), Take(Take3, 3, RenderStatus.Failed, audio: false));

        Assert.Equal(Take2, RailRowActions.TakeToPlay(segment)!.RenderId);
        RailTakeChoice approve = RailRowActions.TakeToApprove(segment)!;
        Assert.Equal(Take2, approve.RenderId);
        Assert.Equal("Take 02", approve.Title);
        Assert.Equal("awaiting review", approve.Description);
    }

    [Fact]
    public void An_approved_segment_plays_its_approved_take_and_has_nothing_to_approve()
    {
        SegmentDocument segment = Segment(SegmentStatus.Approved, approved: Take2,
            Take(Take1, 1, RenderStatus.Stale), Take(Take2, 2, RenderStatus.Approved));

        Assert.Equal(Take2, RailRowActions.TakeToPlay(segment)!.RenderId);
        Assert.Equal("approved", RailRowActions.TakeToPlay(segment)!.Description);
        Assert.Null(RailRowActions.TakeToApprove(segment));
    }

    [Fact]
    public void Takes_made_from_an_older_script_are_not_offered_for_approval()
    {
        SegmentDocument segment = Segment(SegmentStatus.Stale, approved: null,
            Take(Take1, 1, RenderStatus.Generated, source: "old-script"));

        Assert.Null(RailRowActions.TakeToApprove(segment));
        RailTakeChoice play = RailRowActions.TakeToPlay(segment)!;
        Assert.Equal(Take1, play.RenderId);
        Assert.Equal("script changed since", play.Description);
    }

    [Fact]
    public void While_a_segment_renders_its_earlier_take_can_be_played_but_not_approved()
    {
        SegmentDocument segment = Segment(SegmentStatus.Rendering, approved: null, Take(Take1, 1, RenderStatus.Generated));

        Assert.Equal(Take1, RailRowActions.TakeToPlay(segment)!.RenderId);
        Assert.Null(RailRowActions.TakeToApprove(segment));
        Assert.Equal("Rendering now", RailRowActions.GenerateBlockedReason(segment, studioBusy: false));
    }

    [Fact]
    public void Cleaned_up_takes_are_never_offered_to_play_or_approve()
    {
        SegmentDocument segment = Segment(SegmentStatus.Rendered, approved: null,
            Take(Take1, 1, RenderStatus.Generated) with { AudioDeletedAt = DateTimeOffset.UnixEpoch });

        Assert.Null(RailRowActions.TakeToPlay(segment));
        Assert.Null(RailRowActions.TakeToApprove(segment));
    }

    [Fact]
    public void Failed_takes_have_nothing_to_play()
    {
        SegmentDocument segment = Segment(SegmentStatus.Ready, approved: null,
            Take(Take1, 1, RenderStatus.Failed, audio: false), Take(Take2, 2, RenderStatus.Cancelled, audio: false));

        Assert.Null(RailRowActions.TakeToPlay(segment));
    }

    [Fact]
    public void Imported_recordings_say_so_when_they_await_review()
    {
        SegmentDocument segment = Segment(SegmentStatus.Rendered, approved: null,
            Take(Take1, 1, RenderStatus.Generated) with { Origin = RenderOrigin.Imported });

        Assert.Equal("imported, awaiting review", RailRowActions.TakeToApprove(segment)!.Description);
    }

    [Fact]
    public void Generate_waits_for_a_busy_studio()
    {
        SegmentDocument segment = Segment(SegmentStatus.Ready);

        Assert.Null(RailRowActions.GenerateBlockedReason(segment, studioBusy: false));
        Assert.Equal("Wait for the current render to finish", RailRowActions.GenerateBlockedReason(segment, studioBusy: true));
    }

    [Fact]
    public void Chapter_play_label_counts_approved_takes_and_their_running_time()
    {
        ChapterPlaylistEntry entry = new("s", "One", Take1, 1, "a.wav", TimeSpan.FromSeconds(65), false);

        Assert.Equal("Play chapter · 2 approved takes · 02:10",
            RailRowActions.ChapterPlayLabel(new ChapterPlaylist("c", "Chapter", [entry, entry], [])));
        Assert.Equal("Play chapter · 1 approved take · 01:05",
            RailRowActions.ChapterPlayLabel(new ChapterPlaylist("c", "Chapter", [entry], [])));
        Assert.Equal("No approved takes to play yet", RailRowActions.ChapterPlayLabel(new ChapterPlaylist("c", "Chapter", [], [])));
    }

    [Fact]
    public void Approval_queue_runs_first_asked_first_and_holds_each_take_once()
    {
        TakeApprovalQueue queue = new();

        Assert.True(queue.Enqueue(new("a", "Alpha", Take1, 1, false)));
        Assert.True(queue.Enqueue(new("b", "Beta", Take2, 2, false)));
        Assert.False(queue.Enqueue(new("a", "Alpha", Take1, 1, false)));
        Assert.True(queue.Contains(Take2));
        Assert.Equal(2, queue.Count);

        Assert.True(queue.TryDequeue(out QueuedTakeApproval first));
        Assert.Equal(Take1, first.RenderId);
        Assert.True(queue.TryDequeue(out QueuedTakeApproval second));
        Assert.Equal(Take2, second.RenderId);
        Assert.False(queue.TryDequeue(out _));
        Assert.False(queue.Contains(Take2));
    }

    [Fact]
    public void Approval_queue_can_be_cleared_when_another_project_opens()
    {
        TakeApprovalQueue queue = new();
        queue.Enqueue(new("a", "Alpha", Take1, 1, false));

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));
    }

    private static SegmentDocument Segment(SegmentStatus status, Guid? approved = null, params RenderRecord[] takes) => new()
    {
        SegmentId = "segment",
        ChapterId = "chapter",
        Title = "Opening",
        Status = status,
        SourceSha256 = "current-script",
        ApprovedRenderId = approved,
        RenderHistory = [.. takes],
    };

    private static RenderRecord Take(Guid id, int number, RenderStatus status, bool audio = true, string source = "current-script") => new()
    {
        RenderId = id,
        TakeNumber = number,
        Status = status,
        SourceSha256 = source,
        RawAudioRelativePath = audio ? $"04-renders/raw/take-{number:00}.wav" : string.Empty,
    };
}
