using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Packaging;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Packaging;

public sealed class ApprovedTakePackageServiceTests
{
    [Fact]
    public void A_new_package_goes_into_a_named_folder_and_an_existing_one_is_reused()
    {
        using TemporaryDirectory directory = new();

        Assert.Equal(Path.Combine(directory.Root, "Rail smoke - approved takes"),
            ApprovedTakePackageService.ResolvePackageRoot(directory.Root, "Rail smoke"));
        File.WriteAllText(Path.Combine(directory.Root, ApprovedTakePackageManifest.FileName), "{}");
        Assert.Equal(directory.Root, ApprovedTakePackageService.ResolvePackageRoot(directory.Root, "Rail smoke"));
    }

    [Fact]
    public async Task First_package_copies_approved_processed_takes_in_chapter_order_and_records_them()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);

        ApprovedTakePackageResult result = await PackageAsync(service, project.Root, output.Root, document);

        Assert.Equal(2, result.PackagedTakes);
        Assert.Equal(2, result.Copied);
        Assert.Equal(
            File.ReadAllBytes(PackagingFixture.Full(project.Root, "04-generated-audio/segments/opening/opening--take-0002--processed-001.wav")),
            File.ReadAllBytes(Path.Combine(output.Root, "01 Introduction", "01-001 Opening.wav")));
        Assert.True(File.Exists(Path.Combine(output.Root, "01 Introduction", "01-002 Observe.wav")));
        ApprovedTakePackageManifest manifest = (await ApprovedTakePackageService.ReadManifestAsync(output.Root))!;
        Assert.Equal(document.ProjectId, manifest.ProjectId);
        Assert.Equal(["01 Introduction/01-001 Opening.wav", "01 Introduction/01-002 Observe.wav"], manifest.Files.Select(file => file.RelativePath));
        Assert.All(manifest.Files, file => Assert.Equal(64, file.Sha256.Length));
    }

    [Fact]
    public async Task Packaging_again_after_a_reorder_and_rename_renames_files_and_tidies_empty_folders()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);
        await PackageAsync(service, project.Root, output.Root, document);
        VoiceBenchProjectDocument changed = document with
        {
            Chapters = [document.Chapters[0] with { Title = "Intro", SegmentOrder = ["observe", "opening", "review"] }],
        };

        ApprovedTakePackageResult result = await PackageAsync(service, project.Root, output.Root, changed);

        Assert.Equal(2, result.Moved);
        Assert.Equal(0, result.Copied);
        Assert.True(File.Exists(Path.Combine(output.Root, "01 Intro", "01-001 Observe.wav")));
        Assert.True(File.Exists(Path.Combine(output.Root, "01 Intro", "01-002 Opening.wav")));
        Assert.False(Directory.Exists(Path.Combine(output.Root, "01 Introduction")));
    }

    [Fact]
    public async Task Unselected_segments_are_removed_but_foreign_and_edited_files_stay()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);
        await PackageAsync(service, project.Root, output.Root, document);
        string notes = Path.Combine(output.Root, "01 Introduction", "notes.txt");
        File.WriteAllText(notes, "mine");
        string observe = Path.Combine(output.Root, "01 Introduction", "01-002 Observe.wav");
        File.WriteAllBytes(observe, [1, 2, 3]);

        ApprovedTakePackageResult result = await PackageAsync(service, project.Root, output.Root, document, onlySegment: "opening");

        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(observe));
        Assert.Contains(result.LeftUntouched, entry => entry.Contains("01-002 Observe.wav", StringComparison.Ordinal));
        ApprovedTakePackageManifest manifest = (await ApprovedTakePackageService.ReadManifestAsync(output.Root))!;
        Assert.Equal(["01 Introduction/01-001 Opening.wav"], manifest.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public async Task A_folder_holding_another_projects_package_is_refused()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);
        await PackageAsync(service, project.Root, output.Root, document);

        VoiceBenchProjectDocument other = document with { ProjectId = Guid.NewGuid() };

        await Assert.ThrowsAsync<PackageFolderConflictException>(() => PackageAsync(service, project.Root, output.Root, other));
    }

    [Fact]
    public async Task A_run_that_fails_part_way_still_records_what_it_wrote()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);
        await PackageAsync(service, project.Root, output.Root, document, onlySegment: "opening");
        File.Delete(PackagingFixture.Full(project.Root, "04-generated-audio/segments/observe/observe--take-0001--processed-001.wav"));

        await Assert.ThrowsAnyAsync<IOException>(() => PackageAsync(service, project.Root, output.Root, document));

        ApprovedTakePackageManifest manifest = (await ApprovedTakePackageService.ReadManifestAsync(output.Root))!;
        Assert.Equal(["01 Introduction/01-001 Opening.wav"], manifest.Files.Select(file => file.RelativePath));
        Assert.Empty(Directory.EnumerateFiles(output.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_swap_interrupted_between_renames_is_completed_not_stranded()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);
        await PackageAsync(service, project.Root, output.Root, document);
        VoiceBenchProjectDocument swapped = document with
        {
            Chapters = [document.Chapters[0] with { SegmentOrder = ["observe", "opening", "review"] }],
        };
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(swapped);
        PackageFolderState folder = await service.ReadFolderAsync(project.Root, output.Root, swapped, candidates);
        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(candidates, folder);
        using CancellationTokenSource cancel = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ApplyAsync(project.Root, output.Root, swapped, plan, folder, new CancelOnFirstReport(cancel), cancel.Token));

        Assert.True(File.Exists(Path.Combine(output.Root, "01 Introduction", "01-001 Observe.wav")));
        Assert.True(File.Exists(Path.Combine(output.Root, "01 Introduction", "01-002 Opening.wav")));
        Assert.Empty(Directory.EnumerateFiles(output.Root, "*.tmp", SearchOption.AllDirectories));
        ApprovedTakePackageManifest manifest = (await ApprovedTakePackageService.ReadManifestAsync(output.Root))!;
        Assert.Equal(["01 Introduction/01-001 Observe.wav", "01 Introduction/01-002 Opening.wav"], manifest.Files.Select(file => file.RelativePath));
    }

    [Fact]
    public async Task An_unrecorded_file_that_is_exactly_the_approved_take_is_adopted()
    {
        using TemporaryDirectory project = new();
        using TemporaryDirectory output = new();
        (VoiceBenchProjectDocument document, ApprovedTakePackageService service) = await SetupAsync(project.Root);
        Directory.CreateDirectory(Path.Combine(output.Root, "01 Introduction"));
        File.Copy(PackagingFixture.Full(project.Root, "04-generated-audio/segments/opening/opening--take-0002--processed-001.wav"),
            Path.Combine(output.Root, "01 Introduction", "01-001 Opening.wav"));

        ApprovedTakePackageResult result = await PackageAsync(service, project.Root, output.Root, document);

        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, result.Copied);
        Assert.Empty(result.LeftUntouched);
    }

    [Theory]
    [InlineData("{\"projectId\":\"00000000-0000-0000-0000-000000000000\",\"files\":[{\"relativePath\":\"../outside.wav\"}]}")]
    [InlineData("{\"files\":[{\"relativePath\":\"C:/Windows/win.ini\"}]}")]
    public async Task A_package_record_pointing_outside_the_folder_is_refused(string manifest)
    {
        using TemporaryDirectory output = new();
        File.WriteAllText(Path.Combine(output.Root, ApprovedTakePackageManifest.FileName), manifest);

        await Assert.ThrowsAsync<PackageFolderConflictException>(() => ApprovedTakePackageService.ReadManifestAsync(output.Root));
    }

    [Fact]
    public async Task A_package_record_without_files_reads_as_empty()
    {
        using TemporaryDirectory output = new();
        File.WriteAllText(Path.Combine(output.Root, ApprovedTakePackageManifest.FileName), "{\"files\":null}");

        ApprovedTakePackageManifest manifest = (await ApprovedTakePackageService.ReadManifestAsync(output.Root))!;

        Assert.Empty(manifest.Files);
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cancel) : IProgress<PackageProgress>
    {
        public void Report(PackageProgress value) => cancel.Cancel();
    }

    private static async Task<(VoiceBenchProjectDocument, ApprovedTakePackageService)> SetupAsync(string root)
    {
        FileProjectRepository repository = new(new FixedTimeProvider());
        VoiceBenchProjectDocument document = await PackagingFixture.CreateAsync(repository, root);
        return (document, new ApprovedTakePackageService(new FixedTimeProvider()));
    }

    private static async Task<ApprovedTakePackageResult> PackageAsync(
        ApprovedTakePackageService service, string projectRoot, string packageRoot, VoiceBenchProjectDocument project, string? onlySegment = null)
    {
        IReadOnlyList<PackageCandidate> candidates = ApprovedTakePackageRules.Candidates(project);
        PackageFolderState folder = await service.ReadFolderAsync(projectRoot, packageRoot, project, candidates);
        ApprovedTakePackagePlan plan = ApprovedTakePackageRules.Plan(
            candidates.Where(candidate => onlySegment is null || candidate.SegmentId == onlySegment), folder);
        return await service.ApplyAsync(projectRoot, packageRoot, project, plan, folder);
    }
}
