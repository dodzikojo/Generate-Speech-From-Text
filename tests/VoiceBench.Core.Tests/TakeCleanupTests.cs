using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class TakeCleanupTests
{
    private static readonly Guid Take1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Take2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Take3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid Take4 = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid Review1 = Guid.Parse("00000000-0000-0000-0000-000000000011");

    [Fact]
    public void Only_extra_takes_of_approved_segments_are_removable()
    {
        IReadOnlyList<TakeCleanupSegment> plan = TakeCleanupRules.Plan(Project());

        TakeCleanupSegment approved = plan.Single(item => item.SegmentId == "approved");
        Assert.Equal(Take3, approved.ApprovedTake?.RenderId);
        Assert.Equal([Take1, Take2], approved.Removable.Select(take => take.RenderId).ToArray());
        Assert.Empty(plan.Single(item => item.SegmentId == "review").Removable);
    }

    [Fact]
    public void Nothing_is_removable_when_the_approved_take_has_no_audio_on_disk()
    {
        IReadOnlyList<TakeCleanupSegment> plan = TakeCleanupRules.Plan(Project(), audioOnDisk: take => take.RenderId != Take3);

        Assert.Empty(plan.Single(item => item.SegmentId == "approved").Removable);
    }

    [Fact]
    public void Takes_a_mix_uses_are_kept()
    {
        VoiceBenchProjectDocument project = Project() with
        {
            AudioTimelines = [new AudioTimelineDocument { Clips = [new AudioClipDocument { RenderId = Take2 }] }],
        };

        TakeCleanupSegment approved = TakeCleanupRules.Plan(project).Single(item => item.SegmentId == "approved");

        Assert.Equal([Take1], approved.Removable.Select(take => take.RenderId).ToArray());
        Assert.Equal([Take2], approved.KeptForMix.Select(take => take.RenderId).ToArray());
    }

    [Fact]
    public void Clips_pointing_at_a_take_through_its_asset_and_pending_replacements_also_protect_it()
    {
        Guid asset = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        VoiceBenchProjectDocument project = Project() with
        {
            AudioAssets = [new AudioAssetRecord { AssetId = asset, RenderId = Take1 }],
            AudioTimelines =
            [
                new AudioTimelineDocument
                {
                    Clips = [new AudioClipDocument { AssetId = asset }],
                    ReplacementProposals = [new TakeReplacementProposal { CurrentRenderId = Take3, ProposedRenderId = Take2 }],
                },
            ],
        };

        IReadOnlySet<Guid> used = TakeCleanupRules.TakesUsedByMixes(project);

        Assert.Contains(Take1, used);
        Assert.Contains(Take2, used);
        Assert.Empty(TakeCleanupRules.Plan(project).Single(item => item.SegmentId == "approved").Removable);
    }

    [Fact]
    public void Already_deleted_and_audio_less_takes_are_not_offered_again()
    {
        VoiceBenchProjectDocument project = Project();
        project = project with
        {
            Segments = project.Segments.Select(segment => segment.SegmentId != "approved" ? segment : segment with
            {
                RenderHistory =
                [
                    .. segment.RenderHistory.Select(take => take.RenderId == Take1 ? take with { AudioDeletedAt = DateTimeOffset.UnixEpoch } : take),
                    new RenderRecord { RenderId = Take4, TakeNumber = 4, Status = RenderStatus.Failed },
                ],
            }).ToList(),
        };

        Assert.Equal([Take2], TakeCleanupRules.Plan(project).Single(item => item.SegmentId == "approved").Removable.Select(take => take.RenderId).ToArray());
    }

    [Fact]
    public void Storage_is_measured_per_chapter_with_what_clean_up_would_free()
    {
        Dictionary<string, long> sizes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["approved-1.wav"] = 100,
            ["approved-2.wav"] = 200,
            ["approved-3.wav"] = 300,
            ["approved-3-processed.wav"] = 50,
            ["review-1.wav"] = 400,
        };
        TakeStorageReport report = TakeCleanupRules.Measure(Project(), Files, path => sizes.TryGetValue(path, out long size) ? size : null);

        ChapterTakeStorage chapter = Assert.Single(report.Chapters);
        Assert.Equal(1050, chapter.TotalBytes);
        Assert.Equal(350, chapter.ApprovedBytes);
        Assert.Equal(300, chapter.RemovableBytes);
        Assert.Equal(2, chapter.RemovableTakes);
        Assert.Equal(300, report.RemovableBytes);
    }

    [Fact]
    public void Marking_takes_deleted_keeps_their_records_and_drops_their_audio_assets()
    {
        VoiceBenchProjectDocument project = Project() with
        {
            AudioAssets = [new AudioAssetRecord { RenderId = Take1 }, new AudioAssetRecord { RenderId = Take3 }],
        };
        DateTimeOffset now = new(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

        VoiceBenchProjectDocument cleaned = TakeCleanupRules.MarkDeleted(project, [Take1, Take2], now);

        SegmentDocument segment = cleaned.Segments.Single(item => item.SegmentId == "approved");
        Assert.Equal(3, segment.RenderHistory.Count);
        Assert.Equal(now, segment.RenderHistory.Single(take => take.RenderId == Take1).AudioDeletedAt);
        Assert.Null(segment.RenderHistory.Single(take => take.RenderId == Take3).AudioDeletedAt);
        Assert.Equal([Take3], cleaned.AudioAssets.Select(asset => asset.RenderId!.Value).ToArray());
    }

    [Fact]
    public void Marking_never_touches_an_approved_take_or_one_a_mix_uses()
    {
        VoiceBenchProjectDocument used = Project() with
        {
            AudioTimelines = [new AudioTimelineDocument { Clips = [new AudioClipDocument { RenderId = Take1 }] }],
        };

        Assert.Throws<InvalidOperationException>(() => TakeCleanupRules.MarkDeleted(Project(), [Take3], DateTimeOffset.UnixEpoch));
        Assert.Same(used, TakeCleanupRules.MarkDeleted(used, [Take1], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Cleaned_up_takes_are_not_registered_as_mix_assets_again()
    {
        VoiceBenchProjectDocument project = TakeCleanupRules.MarkDeleted(Project(), [Take1], DateTimeOffset.UnixEpoch);

        VoiceBenchProjectDocument registered = AudioTimelineRules.RegisterRenderAssets(project);

        Assert.DoesNotContain(registered.AudioAssets, asset => asset.RenderId == Take1);
        Assert.Contains(registered.AudioAssets, asset => asset.RenderId == Take2);
    }

    [Fact]
    public void A_cleaned_up_take_cannot_be_approved()
    {
        SegmentDocument segment = TakeCleanupRules.MarkDeleted(Project(), [Take1], DateTimeOffset.UnixEpoch)
            .Segments.Single(item => item.SegmentId == "approved");

        Assert.Throws<InvalidOperationException>(() => SegmentStateRules.ApproveRender(segment, Take1, DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void Sizes_read_naturally(long bytes, string expected)
    {
        Assert.Equal(expected, TakeCleanupRules.FormatBytes(bytes));
    }

    private static IReadOnlyList<string> Files(RenderRecord take) =>
        new[] { take.RawAudioRelativePath, take.ProcessedAudioRelativePath }.Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).ToList();

    private static RenderRecord Take(Guid id, int number, RenderStatus status, string file, string? processed = null) => new()
    {
        RenderId = id,
        TakeNumber = number,
        Status = status,
        RawAudioRelativePath = file,
        ProcessedAudioRelativePath = processed,
    };

    private static VoiceBenchProjectDocument Project() => new()
    {
        ChapterOrder = ["chapter"],
        Chapters = [new() { ChapterId = "chapter", Title = "Chapter", SegmentOrder = ["approved", "review"] }],
        Segments =
        [
            new()
            {
                SegmentId = "approved", ChapterId = "chapter", Title = "Approved", Status = SegmentStatus.Approved, ApprovedRenderId = Take3,
                RenderHistory =
                [
                    Take(Take1, 1, RenderStatus.Generated, "approved-1.wav"),
                    Take(Take2, 2, RenderStatus.Approved, "approved-2.wav"),
                    Take(Take3, 3, RenderStatus.Approved, "approved-3.wav", "approved-3-processed.wav"),
                ],
            },
            new()
            {
                SegmentId = "review", ChapterId = "chapter", Title = "Review", Status = SegmentStatus.Rendered,
                RenderHistory = [Take(Review1, 1, RenderStatus.Generated, "review-1.wav")],
            },
        ],
    };
}
