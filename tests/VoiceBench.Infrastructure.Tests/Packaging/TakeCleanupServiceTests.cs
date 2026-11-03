using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Packaging;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Packaging;

public sealed class TakeCleanupServiceTests
{
    [Fact]
    public async Task Take_files_include_every_processed_version_and_nothing_from_other_takes()
    {
        using TemporaryDirectory directory = new();
        VoiceBenchProjectDocument project = await PackagingFixture.CreateAsync(new FileProjectRepository(new FixedTimeProvider()), directory.Root);
        RenderRecord old = project.Segments.Single(item => item.SegmentId == "opening").RenderHistory.Single(take => take.TakeNumber == 1);

        IReadOnlyList<string> files = TakeAudioFiles.List(directory.Root, old);

        Assert.Equal(
            [
                "04-generated-audio/segments/opening/opening--take-0001.raw.wav",
                "04-generated-audio/segments/opening/opening--take-0001--processed-002.wav",
                "04-generated-audio/segments/opening/opening--take-0001--processed-001.wav",
            ],
            files);
    }

    [Fact]
    public async Task Storage_report_counts_each_chapter_and_what_clean_up_frees()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        await PackagingFixture.CreateAsync(repository, directory.Root);
        TakeCleanupService service = new(repository, new FakeRecycler(Path.Combine(directory.Root, "..", "bin-" + Guid.NewGuid().ToString("N"))));

        TakeStorageReport report = await service.MeasureAsync(directory.Root);

        ChapterTakeStorage chapter = Assert.Single(report.Chapters);
        // Opening: take 1 = 1000 raw + 2 × 500 processed; take 2 = 2000 + 1000. Observe: 3000 + 1500. Review: 4000.
        Assert.Equal(2000 + 3000 + 4500 + 4000, chapter.TotalBytes);
        Assert.Equal(3000 + 4500, chapter.ApprovedBytes);
        Assert.Equal(2000, chapter.RemovableBytes);
        Assert.Equal(1, chapter.RemovableTakes);
    }

    [Fact]
    public async Task Clean_up_recycles_extra_takes_of_approved_segments_and_marks_them_deleted()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        await PackagingFixture.CreateAsync(repository, directory.Root);
        FakeRecycler recycler = new(Path.Combine(directory.Root, "recycled"));
        TakeCleanupService service = new(repository, recycler, new FixedTimeProvider());

        TakeCleanupResult result = await service.CleanUpAsync(directory.Root,
            [PackagingFixture.OpeningOld, PackagingFixture.OpeningApproved, PackagingFixture.ReviewTake]);

        Assert.Equal(1, result.TakesRemoved);
        Assert.Equal(2000, result.BytesRecycled);
        Assert.Empty(result.Failures);
        Assert.Equal(3, recycler.Recycled.Count);
        Assert.False(File.Exists(PackagingFixture.Full(directory.Root, "04-generated-audio/segments/opening/opening--take-0001.raw.wav")));
        Assert.True(File.Exists(PackagingFixture.Full(directory.Root, "04-generated-audio/segments/opening/opening--take-0002--processed-001.wav")));
        Assert.True(File.Exists(PackagingFixture.Full(directory.Root, "04-generated-audio/segments/review/review--take-0001.raw.wav")));
        VoiceBenchProjectDocument saved = await repository.LoadAsync(directory.Root);
        SegmentDocument opening = saved.Segments.Single(item => item.SegmentId == "opening");
        Assert.Equal(2, opening.RenderHistory.Count);
        Assert.NotNull(opening.RenderHistory.Single(take => take.RenderId == PackagingFixture.OpeningOld).AudioDeletedAt);
        Assert.Null(opening.RenderHistory.Single(take => take.RenderId == PackagingFixture.OpeningApproved).AudioDeletedAt);
    }

    [Fact]
    public async Task A_take_whose_files_could_not_be_recycled_is_not_marked_deleted()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        await PackagingFixture.CreateAsync(repository, directory.Root);
        FakeRecycler recycler = new(Path.Combine(directory.Root, "recycled")) { FailFor = _ => true };
        TakeCleanupService service = new(repository, recycler);

        TakeCleanupResult result = await service.CleanUpAsync(directory.Root, [PackagingFixture.OpeningOld]);

        Assert.Equal(0, result.TakesRemoved);
        Assert.Single(result.Failures);
        VoiceBenchProjectDocument saved = await repository.LoadAsync(directory.Root);
        Assert.Null(saved.Segments.Single(item => item.SegmentId == "opening").RenderHistory.Single(take => take.TakeNumber == 1).AudioDeletedAt);
    }

    [Fact]
    public async Task A_segment_whose_approved_audio_is_missing_keeps_its_other_takes()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        await PackagingFixture.CreateAsync(repository, directory.Root);
        File.Delete(PackagingFixture.Full(directory.Root, "04-generated-audio/segments/opening/opening--take-0002.raw.wav"));
        File.Delete(PackagingFixture.Full(directory.Root, "04-generated-audio/segments/opening/opening--take-0002--processed-001.wav"));
        FakeRecycler recycler = new(Path.Combine(directory.Root, "recycled"));
        TakeCleanupService service = new(repository, recycler);

        TakeCleanupResult result = await service.CleanUpAsync(directory.Root, [PackagingFixture.OpeningOld]);

        Assert.Equal(0, result.TakesRemoved);
        Assert.Empty(recycler.Recycled);
        Assert.True(File.Exists(PackagingFixture.Full(directory.Root, "04-generated-audio/segments/opening/opening--take-0001.raw.wav")));
    }

    [Fact]
    public async Task A_take_placed_in_a_mix_after_confirmation_is_kept()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        VoiceBenchProjectDocument project = await PackagingFixture.CreateAsync(repository, directory.Root);
        await repository.SaveAsync(directory.Root, project with
        {
            AudioTimelines = [new AudioTimelineDocument { Clips = [new AudioClipDocument { RenderId = PackagingFixture.OpeningOld }] }],
        });
        FakeRecycler recycler = new(Path.Combine(directory.Root, "recycled"));
        TakeCleanupService service = new(repository, recycler);

        TakeCleanupResult result = await service.CleanUpAsync(directory.Root, [PackagingFixture.OpeningOld]);

        Assert.Equal(0, result.TakesRemoved);
        Assert.Empty(recycler.Recycled);
    }
}
