using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Mastering;

public sealed class AssemblySnapshotServiceTests
{
    [Fact]
    public async Task ResolveAsync_BackfillsHistoricalTakeOnlyWhenTextAndDirectionsMatch()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await CreateLegacyApprovedProjectAsync(repository, directory.Root);
        AssemblySnapshotService service = new(repository);

        (VoiceBenchProjectDocument updated, IReadOnlyList<AlignedTakeSnapshot> takes) = await service.ResolveAsync(
            directory.Root,
            project,
            MasterScope.Compilation,
            null);

        AlignedTakeSnapshot take = Assert.Single(takes);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, take.CleanSourceRelativePath)));
        string submitted = await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, take.SubmittedTextRelativePath));
        Assert.Contains("Read warmly.", submitted, StringComparison.Ordinal);
        Assert.Contains("Say V B.", submitted, StringComparison.Ordinal);
        RenderRecord persisted = updated.Segments.Single().RenderHistory.Single();
        Assert.NotNull(persisted.CleanSourceRelativePath);
        Assert.NotNull(persisted.AuthoringSourceRelativePath);
        Assert.NotNull(persisted.SubmittedTextRelativePath);
    }

    [Fact]
    public async Task ResolveAsync_AllowsLatestGuidanceToDifferFromApprovedAuthoringSnapshot()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await CreateLegacyApprovedProjectAsync(repository, directory.Root);
        AssemblySnapshotService service = new(repository);
        (project, _) = await service.ResolveAsync(directory.Root, project, MasterScope.Compilation, null);
        SegmentDocument segment = Assert.Single(project.Segments);
        const string revisedAuthoring = "@visual: Show the VoiceBench project.\n\nSay VoiceBench.";
        await File.WriteAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, segment.ScriptRelativePath),
            revisedAuthoring);
        segment = SegmentStateRules.WithSourceHashes(
            segment,
            SourceTextHasher.Compute("Say VoiceBench."),
            SourceTextHasher.Compute(revisedAuthoring));
        project = project with { Segments = [segment] };
        await repository.SaveAsync(directory.Root, project);

        (VoiceBenchProjectDocument updated, IReadOnlyList<AlignedTakeSnapshot> takes) = await service.ResolveAsync(
            directory.Root,
            project,
            MasterScope.Compilation,
            null);

        Assert.Single(takes);
        Assert.Equal(SegmentStatus.Approved, Assert.Single(updated.Segments).Status);
        Assert.Equal(SourceTextHasher.Compute("Say VoiceBench."), updated.Segments[0].SourceSha256);
    }

    [Fact]
    public async Task ResolveAsync_BlocksHistoricalTakeWhenSubmittedTextCannotBeProven()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await CreateLegacyApprovedProjectAsync(repository, directory.Root);
        SegmentDocument segment = project.Segments.Single();
        RenderRecord render = segment.RenderHistory.Single() with
        {
            SubmittedTextSha256 = SourceTextHasher.Compute("Different provider input"),
        };
        segment = segment with { RenderHistory = [render] };
        project = project with { Segments = [segment] };
        await repository.SaveAsync(directory.Root, project);
        AssemblySnapshotService service = new(repository);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(
            directory.Root,
            project,
            MasterScope.Compilation,
            null));

        Assert.Contains("rerendered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<VoiceBenchProjectDocument> CreateLegacyApprovedProjectAsync(
        FileProjectRepository repository,
        string root)
    {
        VoiceBenchProjectDocument project = await repository.CreateAsync(root, "Legacy");
        const string text = "Say VoiceBench.";
        const string directions = "Read warmly.";
        string submitted = "Say V B.";
        string scriptPath = "02-scripts/chapter/segment.md";
        string processedPath = "04-generated-audio/segments/segment.processed.wav";
        Directory.CreateDirectory(Path.GetDirectoryName(VoiceBenchProjectLayout.ResolveRelativePath(root, scriptPath))!);
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, scriptPath), text);
        await File.WriteAllBytesAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, processedPath), [1]);
        string hash = SourceTextHasher.Compute(text);
        Guid renderId = Guid.NewGuid();
        SegmentDocument segment = new()
        {
            SegmentId = "segment",
            ChapterId = "chapter",
            Title = "Segment",
            ScriptRelativePath = scriptPath,
            SourceSha256 = hash,
            VoicePresetId = "preset",
            Status = SegmentStatus.Approved,
            ApprovedRenderId = renderId,
            RenderHistory =
            [
                new RenderRecord
                {
                    RenderId = renderId,
                    TakeNumber = 1,
                    Status = RenderStatus.Approved,
                    ProviderId = "openai",
                    ModelId = "gpt-4o-mini-tts",
                    Voice = "marin",
                    SourceSha256 = hash,
                    SubmittedTextSha256 = SourceTextHasher.Compute(submitted),
                    DirectionsSha256 = SourceTextHasher.Compute(directions),
                    ApprovalSourceSha256 = hash,
                    ProcessedAudioRelativePath = processedPath,
                },
            ],
        };
        project = project with
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", Title = "Chapter", SegmentOrder = ["segment"] }],
            Segments = [segment],
            VoicePresets =
            [
                new VoicePreset
                {
                    PresetId = "preset",
                    ProviderId = "openai",
                    ModelId = "gpt-4o-mini-tts",
                    Voice = "marin",
                    Directions = directions,
                },
            ],
            Lexicon = [new PronunciationEntry { Written = "VoiceBench", Spoken = "V B", MatchCase = true }],
        };
        await repository.SaveAsync(root, project);
        return project;
    }
}
