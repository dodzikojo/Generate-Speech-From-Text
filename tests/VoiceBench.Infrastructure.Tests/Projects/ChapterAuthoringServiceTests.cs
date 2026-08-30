using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class ChapterAuthoringServiceTests
{
    [Fact]
    public async Task CreateChapterFromDraftAsync_CommitsChapterAndFilesTogether()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01", "segment02"]);
        ChapterAuthoringService service = new(repository, ids.Dequeue);

        VoiceBenchProjectDocument project = await service.CreateChapterFromDraftAsync(
            directory.Root,
            new ChapterDraft(
                "Welcome",
                [
                    new ChapterDraftSegment("Opening", "Hello there."),
                    new ChapterDraftSegment("Next steps", "Let us begin."),
                ],
                "Two-part welcome"),
            "Introduce the course",
            ["reference01"]);

        ChapterDocument chapter = Assert.Single(project.Chapters);
        Assert.Equal("chapter01", chapter.ChapterId);
        Assert.Equal(["segment01", "segment02"], chapter.SegmentOrder);
        Assert.Equal(["reference01"], chapter.ReferenceIds);
        Assert.All(project.Segments, segment => Assert.Equal(chapter.ChapterId, segment.ChapterId));
        Assert.All(project.Segments, segment =>
            Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, segment.ScriptRelativePath))));
        Assert.Contains("02-scripts/001-welcome--chapte", project.Segments[0].ScriptRelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateBlankChapterAsync_CreatesEditableOpeningSegment()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01"]);
        ChapterAuthoringService service = new(repository, ids.Dequeue);

        VoiceBenchProjectDocument project = await service.CreateBlankChapterAsync(directory.Root, "Chapter one");

        Assert.Equal(["chapter01"], project.ChapterOrder);
        Assert.Equal(["segment01"], project.Chapters.Single().SegmentOrder);
        SegmentDocument segment = Assert.Single(project.Segments);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, segment.ScriptRelativePath)));
    }

    [Fact]
    public async Task MoveAndArchiveChapterAsync_PersistsAssemblyChoices()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01", "chapter02", "segment02"]);
        ChapterAuthoringService service = new(repository, ids.Dequeue);
        await service.CreateBlankChapterAsync(directory.Root, "One");
        await service.CreateBlankChapterAsync(directory.Root, "Two");

        await service.MoveChapterAsync(directory.Root, "chapter02", -1);
        VoiceBenchProjectDocument project = await service.UpdateChapterAsync(
            directory.Root,
            "chapter01",
            includedInCompilation: false,
            status: ChapterStatus.Archived);

        Assert.Equal(["chapter02", "chapter01"], project.ChapterOrder);
        ChapterDocument archived = project.Chapters.Single(chapter => chapter.ChapterId == "chapter01");
        Assert.Equal(ChapterStatus.Archived, archived.Status);
        Assert.False(archived.IncludedInCompilation);
    }

    [Fact]
    public async Task DeleteSegmentAndChapterAsync_RemoveManifestEntriesAndKeepScriptsRecoverable()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01", "segment02"]);
        ChapterAuthoringService service = new(repository, ids.Dequeue);
        VoiceBenchProjectDocument project = await service.CreateChapterFromDraftAsync(
            directory.Root,
            new ChapterDraft(
                "Welcome",
                [new ChapterDraftSegment("Opening", "Hello"), new ChapterDraftSegment("Next", "Continue")],
                ""),
            null,
            []);

        project = await service.DeleteSegmentAsync(directory.Root, "segment01");

        Assert.DoesNotContain(project.Segments, item => item.SegmentId == "segment01");
        Assert.Equal(["segment02"], project.Chapters.Single().SegmentOrder);
        Assert.Single(Directory.EnumerateFiles(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, VoiceBenchProjectLayout.ScriptTrashDirectory)));

        project = await service.DeleteChapterAsync(directory.Root, "chapter01");

        Assert.Empty(project.Chapters);
        Assert.Empty(project.ChapterOrder);
        Assert.Empty(project.Segments);
        Assert.Equal(2, Directory.EnumerateFiles(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, VoiceBenchProjectLayout.ScriptTrashDirectory)).Count());
    }

    [Fact]
    public async Task DeleteSegmentAsync_SaveFailureRestoresCanonicalScript()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01"]);
        ChapterAuthoringService setup = new(repository, ids.Dequeue);
        VoiceBenchProjectDocument project = await setup.CreateBlankChapterAsync(directory.Root, "Welcome");
        SegmentDocument segment = project.Segments.Single();
        string scriptPath = VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, segment.ScriptRelativePath);
        ChapterAuthoringService failing = new(new SaveFailingRepository(repository));

        await Assert.ThrowsAsync<IOException>(() => failing.DeleteSegmentAsync(directory.Root, segment.SegmentId));

        Assert.True(File.Exists(scriptPath));
        Assert.Contains((await repository.LoadAsync(directory.Root)).Segments, item => item.SegmentId == segment.SegmentId);
        Assert.Empty(Directory.EnumerateFiles(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, VoiceBenchProjectLayout.ScriptTrashDirectory)));
    }

    [Fact]
    public async Task ApplyWritingProposalAsync_SavesRevisionAndInvalidatesApprovedTake()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01"]);
        ChapterAuthoringService service = new(repository, ids.Dequeue);
        VoiceBenchProjectDocument project = await service.CreateChapterFromDraftAsync(
            directory.Root,
            new ChapterDraft("One", [new ChapterDraftSegment("Opening", "Original text")], ""),
            null,
            []);
        SegmentDocument segment = project.Segments.Single();
        Guid renderId = Guid.NewGuid();
        RenderRecord render = new()
        {
            RenderId = renderId,
            TakeNumber = 1,
            Status = RenderStatus.Approved,
            SourceSha256 = segment.SourceSha256,
            ApprovalSourceSha256 = segment.SourceSha256,
            ProcessedAudioRelativePath = "04-generated-audio/segments/processed.wav",
        };
        segment = segment with
        {
            Status = SegmentStatus.Approved,
            ApprovedRenderId = renderId,
            RenderHistory = [render],
        };
        await repository.SaveAsync(directory.Root, project with { Segments = [segment] });

        project = await service.ApplyWritingProposalAsync(
            directory.Root,
            segment.SegmentId,
            segment.SourceSha256,
            new WritingProposal("Clearer text", "Improved clarity", []),
            WritingAction.ImproveClarity,
            "openai",
            "gpt-5.6-terra");

        SegmentDocument updated = project.Segments.Single();
        Assert.Equal(SegmentStatus.Stale, updated.Status);
        Assert.Null(updated.ApprovedRenderId);
        Assert.Equal(RenderStatus.Stale, updated.RenderHistory.Single().Status);
        WritingRevisionRecord revision = Assert.Single(updated.WritingRevisions);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, revision.PreviousTextRelativePath)));
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, revision.AcceptedTextRelativePath)));
        Assert.Equal("Clearer text", await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, updated.ScriptRelativePath)));

        VoiceBenchProjectDocument restored = await service.RestoreRevisionAsync(
            directory.Root,
            updated.SegmentId,
            revision.RevisionId,
            restorePreviousText: true);
        Assert.Equal("Original text", await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, restored.Segments.Single().ScriptRelativePath)));
        Assert.Equal(2, restored.Segments.Single().WritingRevisions.Count);
    }

    [Fact]
    public async Task ImportReferenceAsync_CopiesOnlySupportedLocalText()
    {
        using TemporaryDirectory directory = new();
        string source = Path.Combine(directory.Root, "source.md");
        await File.WriteAllTextAsync(source, "Reference text");
        string projectRoot = Path.Combine(directory.Root, "project");
        FileProjectRepository repository = new();
        await repository.CreateAsync(projectRoot, "Course");
        ChapterAuthoringService service = new(repository, () => "reference01");

        VoiceBenchProjectDocument project = await service.ImportReferenceAsync(projectRoot, source);

        SourceReferenceDocument reference = Assert.Single(project.SourceReferences);
        Assert.Equal(SourceTextHasher.Compute("Reference text"), reference.Sha256);
        Assert.Equal("Reference text", await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, reference.RelativePath)));
    }

    [Fact]
    public async Task SplitSegmentAsync_PreservesChapterOrderAndInvalidatesOriginalTake()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01", "segment02"]);
        ChapterAuthoringService service = new(repository, ids.Dequeue);
        VoiceBenchProjectDocument project = await service.CreateChapterFromDraftAsync(
            directory.Root,
            new ChapterDraft("One", [new ChapterDraftSegment("Opening", "First paragraph. Second paragraph.")], ""),
            null,
            []);
        SegmentDocument original = project.Segments.Single();

        project = await service.SplitSegmentAsync(
            directory.Root,
            original.SegmentId,
            "First paragraph. ".Length,
            original.SourceSha256);

        Assert.Equal(["segment01", "segment02"], project.Chapters.Single().SegmentOrder);
        Assert.Equal(2, project.Segments.Count);
        Assert.All(project.Segments, segment => Assert.Equal("chapter01", segment.ChapterId));
        Assert.Equal("First paragraph.", await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, project.Segments[0].ScriptRelativePath)));
        Assert.Equal("Second paragraph.", await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, project.Segments[1].ScriptRelativePath)));
    }

    [Fact]
    public async Task CreateChapterFromDraftAsync_SaveFailureLeavesNoPartialChapterFiles()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Course");
        Queue<string> ids = new(["chapter01", "segment01"]);
        ChapterAuthoringService service = new(new SaveFailingRepository(repository), ids.Dequeue);

        await Assert.ThrowsAsync<IOException>(() => service.CreateChapterFromDraftAsync(
            directory.Root,
            new ChapterDraft("Atomic", [new ChapterDraftSegment("Opening", "Draft")], ""),
            null,
            []));

        Assert.Empty(Directory.EnumerateDirectories(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, VoiceBenchProjectLayout.ScriptsDirectory),
            "001-atomic-*",
            SearchOption.TopDirectoryOnly));
        Assert.Empty((await repository.LoadAsync(directory.Root)).Chapters);
    }

    private sealed class SaveFailingRepository(IProjectRepository inner) : IProjectRepository
    {
        public Task<VoiceBenchProjectDocument> CreateAsync(string projectRoot, string name, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(projectRoot, name, cancellationToken);

        public Task<VoiceBenchProjectDocument> LoadAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(projectRoot, cancellationToken);

        public Task SaveAsync(string projectRoot, VoiceBenchProjectDocument document, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("Synthetic manifest failure"));

        public Task<ProjectValidationResult> ValidateAsync(string projectRoot, CancellationToken cancellationToken = default) =>
            inner.ValidateAsync(projectRoot, cancellationToken);
    }
}
