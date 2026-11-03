using IssueLab.VoiceBench.App.Services;
using IssueLab.VoiceBench.Core;
using Xunit;

namespace IssueLab.VoiceBench.App.Tests;

public sealed class ChapterPlaylistTests
{
    private static readonly Guid OpeningApproved = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid OpeningNewer = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid MiddleGenerated = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid ClosingApproved = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    [Fact]
    public void Playlist_follows_chapter_order_and_uses_only_each_segments_approved_take()
    {
        ChapterPlaylist playlist = ChapterPlaylistBuilder.Build(Project(), "chapter");

        Assert.Equal("Introduction", playlist.ChapterTitle);
        Assert.Equal(["closing", "opening"], playlist.Entries.Select(entry => entry.SegmentId).ToArray());
        Assert.Equal(ClosingApproved, playlist.Entries[0].RenderId);
        Assert.Equal(OpeningApproved, playlist.Entries[1].RenderId);
        Assert.Equal("04-generated-audio/segments/opening/take-0001.wav", playlist.Entries[1].AudioRelativePath);
        Assert.Equal(["Middle"], playlist.SkippedSegmentTitles);
        Assert.Equal(TimeSpan.FromSeconds(30), playlist.TotalDuration);
    }

    [Fact]
    public void Approved_takes_whose_script_changed_still_play_but_are_flagged()
    {
        ChapterPlaylist playlist = ChapterPlaylistBuilder.Build(Project(openingSource: "edited"), "chapter");

        Assert.True(playlist.Entries.Single(entry => entry.SegmentId == "opening").ScriptChanged);
        Assert.False(playlist.Entries.Single(entry => entry.SegmentId == "closing").ScriptChanged);
    }

    [Fact]
    public void Start_index_begins_at_the_segment_or_the_next_playable_one()
    {
        VoiceBenchProjectDocument project = Project();
        ChapterPlaylist playlist = ChapterPlaylistBuilder.Build(project, "chapter");

        Assert.Equal(0, playlist.StartIndexFor(project, null));
        Assert.Equal(1, playlist.StartIndexFor(project, "opening"));
        Assert.Equal(1, playlist.StartIndexFor(project, "middle-before-opening"));
        Assert.Equal(0, playlist.StartIndexFor(project, "unknown"));
    }

    [Fact]
    public void Unknown_chapter_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ChapterPlaylistBuilder.Build(Project(), "missing"));
    }

    [Theory]
    [InlineData(true, false, 1.0, 10.0, TransportPhase.Playing)]
    [InlineData(false, true, 9.95, 10.0, TransportPhase.Ended)]
    [InlineData(false, true, 4.0, 10.0, TransportPhase.Paused)]
    [InlineData(false, false, 10.0, 10.0, TransportPhase.Paused)]
    [InlineData(false, true, 0.0, 0.0, TransportPhase.Paused)]
    public void Transport_reports_ended_only_after_running_to_the_end(bool playing, bool wasRunning, double position, double duration, TransportPhase expected)
    {
        Assert.Equal(expected, TransportPhaseRules.Next(playing, wasRunning, TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(duration)));
    }

    private static VoiceBenchProjectDocument Project(string openingSource = "opening-source") => new()
    {
        ChapterOrder = ["chapter"],
        Chapters = [new() { ChapterId = "chapter", Title = "Introduction", SegmentOrder = ["closing", "middle-before-opening", "opening"] }],
        Segments =
        [
            new()
            {
                SegmentId = "opening", ChapterId = "chapter", Title = "Opening", SourceSha256 = openingSource,
                ApprovedRenderId = OpeningApproved,
                RenderHistory =
                [
                    Render(OpeningApproved, 1, RenderStatus.Approved, "opening-source", 20) with { ProcessedAudioRelativePath = "04-generated-audio/segments/opening/take-0001.wav" },
                    Render(OpeningNewer, 2, RenderStatus.Generated, "opening-source", 21),
                ],
            },
            new()
            {
                SegmentId = "middle-before-opening", ChapterId = "chapter", Title = "Middle", SourceSha256 = "middle",
                RenderHistory = [Render(MiddleGenerated, 1, RenderStatus.Generated, "middle", 5)],
            },
            new()
            {
                SegmentId = "closing", ChapterId = "chapter", Title = "Closing", SourceSha256 = "closing",
                ApprovedRenderId = ClosingApproved,
                RenderHistory = [Render(ClosingApproved, 4, RenderStatus.Approved, "closing", 10)],
            },
        ],
    };

    private static RenderRecord Render(Guid id, int take, RenderStatus status, string source, double seconds) => new()
    {
        RenderId = id,
        TakeNumber = take,
        Status = status,
        SourceSha256 = source,
        Duration = TimeSpan.FromSeconds(seconds),
        RawAudioRelativePath = $"04-generated-audio/segments/x/take-{take:0000}.raw.wav",
    };
}
