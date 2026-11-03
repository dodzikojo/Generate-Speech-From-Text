using IssueLab.VoiceBench.Core;

namespace IssueLab.VoiceBench.Core.Tests;

public sealed class ApprovedTakePackagingTests
{
    private static readonly Guid OpeningTake = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid ObserveTake = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid DemoTake = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    [Fact]
    public void Candidates_follow_rail_order_and_numbering_and_skip_archived_chapters()
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(Project());

        Assert.Equal(["opening", "intro-draft", "observe", "demo"], candidates.Select(item => item.SegmentId).ToArray());
        Assert.Equal("01 Introduction/01-001 Opening.wav", candidates[0].TargetRelativePath);
        Assert.Equal("01 Introduction/01-003 Observe- why it matters.wav", candidates[2].TargetRelativePath);
        Assert.Equal("02 Demo/02-001 Live walkthrough.wav", candidates[3].TargetRelativePath);
    }

    [Fact]
    public void Only_approved_segments_can_be_packaged_and_others_say_why()
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(Project());

        Assert.True(candidates.Single(item => item.SegmentId == "opening").CanPackage);
        Assert.Equal("04-generated-audio/segments/opening/opening--take-0001--processed-001.wav",
            candidates.Single(item => item.SegmentId == "opening").SourceRelativePath);
        PackageCandidate draft = candidates.Single(item => item.SegmentId == "intro-draft");
        Assert.False(draft.CanPackage);
        Assert.Equal("Not approved yet", draft.Problem);
    }

    [Fact]
    public void Stale_segments_and_deleted_approved_audio_are_not_packaged()
    {
        SegmentDocument stale = Approved("s", OpeningTake) with { Status = SegmentStatus.Stale };
        SegmentDocument deleted = Approved("d", OpeningTake);
        deleted = deleted with { RenderHistory = [deleted.RenderHistory[0] with { AudioDeletedAt = DateTimeOffset.UnixEpoch }] };

        Assert.Equal("Script changed since approval", ApprovedTakePackageRules.ApprovedTake(stale).Problem);
        Assert.Equal("Approved take audio was deleted", ApprovedTakePackageRules.ApprovedTake(deleted).Problem);
    }

    [Theory]
    [InlineData("Observe: why it matters?", "Observe- why it matters-")]
    [InlineData("  Two   spaces  ", "Two spaces")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("", "Untitled")]
    [InlineData("a/b\\c", "a-b-c")]
    public void Names_are_safe_for_windows(string title, string expected)
    {
        Assert.Equal(expected, ApprovedTakePackageRules.SafeName(title));
    }

    [Fact]
    public void A_first_package_copies_every_selected_approved_take()
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(Project());

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(candidates, PackageFolderState.Empty);

        Assert.Equal(3, plan.Count(PackageAction.Copy));
        Assert.Equal(3, plan.PackagedTakes);
        Assert.Equal("3 new", plan.Summary);
    }

    [Fact]
    public void Packaging_again_keeps_matching_files_and_adds_new_approvals()
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(candidates.Where(item => item.SegmentId is "opening" or "observe"));

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(candidates, folder);

        Assert.Equal(2, plan.Count(PackageAction.Keep));
        Assert.Equal("02 Demo/02-001 Live walkthrough.wav", plan.Steps.Single(step => step.Action == PackageAction.Copy).RelativePath);
        Assert.Equal("1 new · 2 unchanged", plan.Summary);
    }

    [Fact]
    public void A_moved_or_renamed_segment_has_its_file_renamed()
    {
        IReadOnlyList<PackageCandidate> before = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(before.Where(item => item.CanPackage));
        VoiceBenchProjectDocument renamed = Project();
        renamed = renamed with { Segments = renamed.Segments.Select(item => item.SegmentId == "opening" ? item with { Title = "Cold open" } : item).ToList() };

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(ApprovedTakePackageRules.Candidates(renamed), folder);

        PackageStep move = plan.Steps.Single(step => step.Action == PackageAction.Move);
        Assert.Equal("01 Introduction/01-001 Opening.wav", move.FromRelativePath);
        Assert.Equal("01 Introduction/01-001 Cold open.wav", move.RelativePath);
    }

    [Fact]
    public void Two_segments_swapping_places_both_move()
    {
        IReadOnlyList<PackageCandidate> before = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(before.Where(item => item.CanPackage));
        VoiceBenchProjectDocument swapped = Project();
        swapped = swapped with
        {
            Chapters = swapped.Chapters.Select(chapter => chapter.ChapterId == "intro"
                ? chapter with { SegmentOrder = ["observe", "intro-draft", "opening"] }
                : chapter).ToList(),
        };

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(ApprovedTakePackageRules.Candidates(swapped), folder);

        Assert.Equal(2, plan.Count(PackageAction.Move));
        Assert.Equal(0, plan.Count(PackageAction.Skip));
        Assert.Contains(plan.Steps, step => step.Action == PackageAction.Move && step.RelativePath == "01 Introduction/01-003 Opening.wav");
    }

    [Fact]
    public void A_newly_approved_take_replaces_the_old_file()
    {
        IReadOnlyList<PackageCandidate> before = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(before.Where(item => item.CanPackage));
        Guid newTake = Guid.Parse("00000000-0000-0000-0000-0000000000a9");
        VoiceBenchProjectDocument reapproved = Project();
        reapproved = reapproved with
        {
            Segments = reapproved.Segments.Select(item => item.SegmentId == "opening" ? Approved("opening", newTake, takeNumber: 2) with { ChapterId = "intro", Title = "Opening" } : item).ToList(),
        };

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(ApprovedTakePackageRules.Candidates(reapproved), folder);

        PackageStep copy = plan.Steps.Single(step => step.Action == PackageAction.Copy);
        Assert.Equal("01 Introduction/01-001 Opening.wav", copy.RelativePath);
        Assert.NotNull(copy.Previous);
        Assert.Equal("1 updated · 2 unchanged", plan.Summary);
    }

    [Fact]
    public void Files_for_segments_no_longer_selected_or_approved_are_removed()
    {
        IReadOnlyList<PackageCandidate> before = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(before.Where(item => item.CanPackage));

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(before.Where(item => item.SegmentId != "demo"), folder);

        Assert.Equal("02 Demo/02-001 Live walkthrough.wav", plan.Steps.Single(step => step.Action == PackageAction.Remove).RelativePath);
    }

    [Fact]
    public void Files_changed_outside_voicebench_are_never_replaced_or_removed()
    {
        IReadOnlyList<PackageCandidate> before = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(before.Where(item => item.CanPackage));
        Dictionary<string, string> edited = new(folder.FileHashes, StringComparer.OrdinalIgnoreCase)
        {
            ["02 Demo/02-001 Live walkthrough.wav"] = "edited-in-an-audio-editor",
        };

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(
            before.Where(item => item.SegmentId != "demo"), folder with { FileHashes = edited });

        PackageStep skipped = Assert.Single(plan.Skipped);
        Assert.Equal("02 Demo/02-001 Live walkthrough.wav", skipped.RelativePath);
        Assert.Equal(0, plan.Count(PackageAction.Remove));
    }

    [Fact]
    public void A_file_voicebench_did_not_write_is_never_overwritten()
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = new(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["01 Introduction/01-001 Opening.wav"] = "someone-elses-file",
        });

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(candidates, folder);

        PackageStep skipped = Assert.Single(plan.Skipped);
        Assert.Equal("opening", skipped.Candidate?.SegmentId);
        Assert.Equal(2, plan.Count(PackageAction.Copy));
    }

    [Fact]
    public void An_unrecorded_file_identical_to_the_approved_take_is_kept_and_recorded()
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(Project());
        PackageCandidate opening = candidates.Single(item => item.SegmentId == "opening");
        PackageFolderState folder = new(null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [opening.TargetRelativePath] = "same" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [opening.SourceRelativePath!] = "same" });

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(candidates, folder);

        PackageStep keep = plan.Steps.Single(step => step.Candidate?.SegmentId == "opening");
        Assert.Equal(PackageAction.Keep, keep.Action);
        Assert.Equal(OpeningTake, keep.Previous?.RenderId);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void A_reprocessed_approved_take_is_copied_again()
    {
        IReadOnlyList<PackageCandidate> before = ApprovedTakePackageRules.Candidates(Project());
        PackageFolderState folder = FolderWith(before.Where(item => item.CanPackage));
        VoiceBenchProjectDocument reprocessed = Project();
        reprocessed = reprocessed with
        {
            Segments = reprocessed.Segments.Select(item => item.SegmentId == "opening"
                ? item with { RenderHistory = [item.RenderHistory[0] with { ProcessedAudioRelativePath = "04-generated-audio/segments/opening/opening--take-0001--processed-002.wav" }] }
                : item).ToList(),
        };

        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(ApprovedTakePackageRules.Candidates(reprocessed), folder);

        Assert.Equal("01 Introduction/01-001 Opening.wav", plan.Steps.Single(step => step.Action == PackageAction.Copy).RelativePath);
    }

    private static PackageFolderState FolderWith(IEnumerable<PackageCandidate> packaged)
    {
        List<PackagedTakeFile> files = packaged.Select(candidate => new PackagedTakeFile
        {
            RelativePath = candidate.TargetRelativePath,
            ChapterId = candidate.ChapterId,
            SegmentId = candidate.SegmentId,
            RenderId = candidate.ApprovedTake!.RenderId,
            TakeNumber = candidate.ApprovedTake.TakeNumber,
            SourceRelativePath = candidate.SourceRelativePath!,
            Sha256 = "hash-" + candidate.SegmentId,
        }).ToList();
        return new PackageFolderState(
            new ApprovedTakePackageManifest { Files = files },
            files.ToDictionary(file => file.RelativePath, file => file.Sha256, StringComparer.OrdinalIgnoreCase));
    }

    private static SegmentDocument Approved(string segmentId, Guid takeId, int takeNumber = 1) => new()
    {
        SegmentId = segmentId,
        Title = segmentId,
        Status = SegmentStatus.Approved,
        ApprovedRenderId = takeId,
        RenderHistory =
        [
            new RenderRecord
            {
                RenderId = takeId,
                TakeNumber = takeNumber,
                Status = RenderStatus.Approved,
                RawAudioRelativePath = $"04-generated-audio/segments/{segmentId}/{segmentId}--take-{takeNumber:0000}.raw.wav",
                ProcessedAudioRelativePath = $"04-generated-audio/segments/{segmentId}/{segmentId}--take-{takeNumber:0000}--processed-001.wav",
            },
        ],
    };

    private static VoiceBenchProjectDocument Project() => new()
    {
        ChapterOrder = ["intro", "old", "demo"],
        Chapters =
        [
            new() { ChapterId = "intro", Title = "Introduction", SegmentOrder = ["opening", "intro-draft", "observe"] },
            new() { ChapterId = "old", Title = "Old", Status = ChapterStatus.Archived, SegmentOrder = ["archived"] },
            new() { ChapterId = "demo", Title = "Demo", SegmentOrder = ["demo"] },
        ],
        Segments =
        [
            Approved("opening", OpeningTake) with { ChapterId = "intro", Title = "Opening" },
            new() { SegmentId = "intro-draft", ChapterId = "intro", Title = "Draft", Status = SegmentStatus.Ready },
            Approved("observe", ObserveTake) with { ChapterId = "intro", Title = "Observe: why it matters" },
            Approved("archived", Guid.NewGuid()) with { ChapterId = "old", Title = "Archived" },
            Approved("demo", DemoTake) with { ChapterId = "demo", Title = "Live walkthrough" },
        ],
    };
}
