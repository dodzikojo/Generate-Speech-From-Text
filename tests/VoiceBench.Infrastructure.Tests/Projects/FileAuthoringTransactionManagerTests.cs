using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class FileAuthoringTransactionManagerTests
{
    [Fact]
    public async Task ExecuteUndoRedoAsync_PersistsHistoryAndNeverTouchesAudio()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Before");
        string audioPath = VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, "04-generated-audio/segments/keep.raw.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await File.WriteAllBytesAsync(audioPath, [1, 2, 3, 4]);
        FileAuthoringTransactionManager manager = new();

        await manager.ExecuteAsync(directory.Root, "Save script", async cancellationToken =>
        {
            await WriteSegmentAsync(repository, directory.Root, segment.SegmentId, "After", cancellationToken);
        });

        AuthoringTransactionState persisted = await new FileAuthoringTransactionManager().GetStateAsync(directory.Root);
        Assert.True(persisted.CanUndo);
        Assert.Equal("Save script", persisted.NextUndo!.Label);
        await new FileAuthoringTransactionManager().UndoAsync(directory.Root);
        Assert.Equal("Before", await File.ReadAllTextAsync(scriptPath));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(audioPath));

        await new FileAuthoringTransactionManager().RedoAsync(directory.Root);
        Assert.Equal("After", await File.ReadAllTextAsync(scriptPath));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(audioPath));
    }

    [Fact]
    public async Task ExecuteAsync_ActionFailureRestoresManifestAndCanonicalFiles()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Stable");
        string manifestBefore = await File.ReadAllTextAsync(Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName));
        FileAuthoringTransactionManager manager = new();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync(
            directory.Root,
            "Broken edit",
            async cancellationToken =>
            {
                await WriteSegmentAsync(repository, directory.Root, segment.SegmentId, "Partial", cancellationToken);
                throw new InvalidOperationException("stop");
            }));

        Assert.Equal("Stable", await File.ReadAllTextAsync(scriptPath));
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName)));
        Assert.Empty((await manager.GetStateAsync(directory.Root)).Transactions);
    }

    [Fact]
    public async Task UndoAuthoring_PreservesLaterRenderHistoryAndMarksItStaleWhenSourceChanges()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, _) = await CreateProjectWithSegmentAsync(directory.Root, "Before");
        FileAuthoringTransactionManager manager = new();
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "After", "Save script");
        VoiceBenchProjectDocument renderedProject = await repository.LoadAsync(directory.Root);
        int index = renderedProject.Segments.FindIndex(item => item.SegmentId == segment.SegmentId);
        SegmentDocument renderedSegment = renderedProject.Segments[index];
        Guid renderId = Guid.NewGuid();
        RenderRecord render = new()
        {
            RenderId = renderId,
            TakeNumber = 1,
            SourceSha256 = renderedSegment.SourceSha256,
            Status = RenderStatus.Approved,
            ApprovalSourceSha256 = renderedSegment.SourceSha256,
            Duration = TimeSpan.FromSeconds(12),
        };
        List<SegmentDocument> segments = [.. renderedProject.Segments];
        segments[index] = renderedSegment with
        {
            Status = SegmentStatus.Approved,
            ApprovedRenderId = renderId,
            RenderHistory = [render],
        };
        await repository.SaveAsync(directory.Root, renderedProject with { Segments = segments });

        await manager.UndoAsync(directory.Root);

        SegmentDocument restored = Assert.Single((await repository.LoadAsync(directory.Root)).Segments);
        RenderRecord preserved = Assert.Single(restored.RenderHistory);
        Assert.Equal(renderId, preserved.RenderId);
        Assert.Equal(RenderStatus.Stale, preserved.Status);
        Assert.Null(restored.ApprovedRenderId);
        Assert.Equal(SegmentStatus.Stale, restored.Status);
    }

    [Fact]
    public async Task ExecuteAfterUndo_TruncatesRedoBranch()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Start");
        FileAuthoringTransactionManager manager = new();
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "First", "First edit");
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "Second", "Second edit");
        await manager.UndoAsync(directory.Root);

        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "Branched", "Branch edit");

        AuthoringTransactionState state = await manager.GetStateAsync(directory.Root);
        Assert.Equal(2, state.Transactions.Count);
        Assert.Equal(2, state.Cursor);
        Assert.False(state.CanRedo);
        Assert.Equal("Branched", await File.ReadAllTextAsync(scriptPath));
    }

    [Fact]
    public async Task UndoAsync_ExternalDivergenceIsBlockedWithoutOverwriting()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Start");
        FileAuthoringTransactionManager manager = new();
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "Saved", "Save script");
        await File.WriteAllTextAsync(scriptPath, "External");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.UndoAsync(directory.Root));

        Assert.Contains("changed outside", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("External", await File.ReadAllTextAsync(scriptPath));
    }

    [Fact]
    public async Task DeleteSegment_UndoAndRedoRestoreManifestAndTrashTogether()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Keep me");
        ChapterAuthoringService authoring = new(repository);
        FileAuthoringTransactionManager manager = new();

        await manager.ExecuteAsync(
            directory.Root,
            "Delete segment",
            cancellationToken => authoring.DeleteSegmentAsync(directory.Root, segment.SegmentId, cancellationToken));
        Assert.Empty((await repository.LoadAsync(directory.Root)).Segments);
        Assert.False(File.Exists(scriptPath));

        await manager.UndoAsync(directory.Root);
        VoiceBenchProjectDocument restored = await repository.LoadAsync(directory.Root);
        Assert.Single(restored.Segments);
        Assert.True(File.Exists(scriptPath));
        Assert.Equal("Keep me", await File.ReadAllTextAsync(scriptPath));

        await manager.RedoAsync(directory.Root);
        Assert.Empty((await repository.LoadAsync(directory.Root)).Segments);
        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public async Task HistoryLimit_PrunesOldestTransactionAndKeepsRemainingUndoChain()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Start");
        FileAuthoringTransactionManager manager = new(historyLimit: 2);
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "One", "One");
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "Two", "Two");
        await ExecuteTextAsync(manager, repository, directory.Root, segment.SegmentId, "Three", "Three");

        AuthoringTransactionState state = await manager.GetStateAsync(directory.Root);
        Assert.Equal(["Two", "Three"], state.Transactions.Select(item => item.Label));
        await manager.UndoAsync(directory.Root);
        await manager.UndoAsync(directory.Root);
        Assert.Equal("One", await File.ReadAllTextAsync(scriptPath));
        Assert.False((await manager.GetStateAsync(directory.Root)).CanUndo);
    }

    [Fact]
    public async Task RecoverInterruptedAsync_RestoresPendingBeforeSnapshot()
    {
        using TemporaryDirectory directory = new();
        (FileProjectRepository repository, SegmentDocument segment, string scriptPath) = await CreateProjectWithSegmentAsync(directory.Root, "Before");
        FileAuthoringTransactionManager manager = new();
        string transactionRoot = VoiceBenchProjectLayout.ResolveRelativePath(
            directory.Root,
            VoiceBenchProjectLayout.AuthoringTransactionsDirectory);
        string capturedRoot = Path.Combine(Path.GetTempPath(), "IssueLab.VoiceBench.TransactionCapture", Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync(
                directory.Root,
                "Interrupted edit",
                async cancellationToken =>
                {
                    CopyDirectory(transactionRoot, capturedRoot);
                    await WriteSegmentAsync(repository, directory.Root, segment.SegmentId, "Partial", cancellationToken);
                    throw new InvalidOperationException("simulate interruption");
                }));

            CopyDirectory(capturedRoot, transactionRoot);
            await File.WriteAllTextAsync(scriptPath, "Partial");
            bool recovered = await new FileAuthoringTransactionManager().RecoverInterruptedAsync(directory.Root);

            Assert.True(recovered);
            Assert.Equal("Before", await File.ReadAllTextAsync(scriptPath));
            Assert.False(File.Exists(Path.Combine(transactionRoot, "pending.json")));
        }
        finally
        {
            if (Directory.Exists(capturedRoot)) Directory.Delete(capturedRoot, recursive: true);
        }
    }

    private static async Task<(FileProjectRepository Repository, SegmentDocument Segment, string ScriptPath)> CreateProjectWithSegmentAsync(
        string projectRoot,
        string text)
    {
        FileProjectRepository repository = new();
        await repository.CreateAsync(projectRoot, "Transactions");
        Queue<string> ids = new(["chapter", "segment"]);
        ChapterAuthoringService authoring = new(repository, ids.Dequeue);
        VoiceBenchProjectDocument project = await authoring.CreateChapterFromDraftAsync(
            projectRoot,
            new ChapterDraft("Chapter", [new ChapterDraftSegment("Segment", text)], "fixture"),
            null,
            []);
        SegmentDocument segment = Assert.Single(project.Segments);
        return (repository, segment, VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath));
    }

    private static Task ExecuteTextAsync(
        FileAuthoringTransactionManager manager,
        FileProjectRepository repository,
        string projectRoot,
        string segmentId,
        string text,
        string label) =>
        manager.ExecuteAsync(
            projectRoot,
            label,
            cancellationToken => WriteSegmentAsync(repository, projectRoot, segmentId, text, cancellationToken));

    private static async Task WriteSegmentAsync(
        FileProjectRepository repository,
        string projectRoot,
        string segmentId,
        string text,
        CancellationToken cancellationToken)
    {
        VoiceBenchProjectDocument project = await repository.LoadAsync(projectRoot, cancellationToken);
        int index = project.Segments.FindIndex(item => item.SegmentId == segmentId);
        SegmentDocument segment = project.Segments[index];
        await File.WriteAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath),
            text,
            cancellationToken);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        List<SegmentDocument> segments = [.. project.Segments];
        segments[index] = SegmentStateRules.WithSourceHashes(
            segment,
            SourceTextHasher.Compute(parsed.NarrationText),
            SourceTextHasher.Compute(text));
        await repository.SaveAsync(projectRoot, project with { Segments = segments }, cancellationToken);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
