using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using System.Text.Json.Nodes;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class FileProjectRepositoryTests
{
    [Fact]
    public async Task CreateAsync_CreatesCompleteFolderLayoutAndAtomicManifest()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();

        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Training narration");

        Assert.Equal("Training narration", project.Name);
        Assert.True(File.Exists(Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName)));
        foreach (string relativePath in VoiceBenchProjectLayout.RequiredDirectories)
        {
            Assert.True(Directory.Exists(Path.Combine(directory.Root, relativePath)));
        }

        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Project_RemainsLoadableAfterRelocation()
    {
        using TemporaryDirectory directory = new();
        string firstRoot = Path.Combine(directory.Root, "first");
        string movedRoot = Path.Combine(directory.Root, "moved");
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await repository.CreateAsync(firstRoot, "Movable");
        string segmentId = "a1b2c3d4";
        string relativePath = "02-scripts/001-introduction--a1b2c3.md";
        string text = "A movable narration segment.";
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(firstRoot, relativePath), text);
        project = project with
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", Title = "Chapter", SegmentOrder = [segmentId] }],
            Segments =
            [
                new SegmentDocument
                {
                    SegmentId = segmentId,
                    ChapterId = "chapter",
                    Title = "Introduction",
                    ScriptRelativePath = relativePath,
                    SourceSha256 = SourceTextHasher.Compute(text),
                },
            ],
        };
        await repository.SaveAsync(firstRoot, project);

        Directory.Move(firstRoot, movedRoot);
        VoiceBenchProjectDocument loaded = await repository.LoadAsync(movedRoot);
        ProjectValidationResult validation = await repository.ValidateAsync(movedRoot);

        Assert.Equal(relativePath, loaded.Segments.Single().ScriptRelativePath);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public async Task SaveAsync_RejectsAbsoluteAndEscapingPaths()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Unsafe path");
        project = project with
        {
            Segments =
            [
                new SegmentDocument
                {
                    SegmentId = "segment",
                    ScriptRelativePath = "../outside.md",
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(directory.Root, project));
    }

    [Fact]
    public async Task ValidateAsync_ReportsChangedCanonicalSource()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Changed source");
        string relativePath = "02-scripts/001-script--abcdef.md";
        string fullPath = VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, relativePath);
        await File.WriteAllTextAsync(fullPath, "Original");
        project = project with
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", Title = "Chapter", SegmentOrder = ["abcdef01"] }],
            Segments =
            [
                new SegmentDocument
                {
                    SegmentId = "abcdef01",
                    ChapterId = "chapter",
                    Title = "Script",
                    ScriptRelativePath = relativePath,
                    SourceSha256 = SourceTextHasher.Compute("Original"),
                },
            ],
        };
        await repository.SaveAsync(directory.Root, project);
        await File.WriteAllTextAsync(fullPath, "External edit");

        ProjectValidationResult validation = await repository.ValidateAsync(directory.Root);

        Assert.Contains(validation.Issues, issue => issue.Code == "segment.source-changed");
    }

    [Fact]
    public async Task SaveAsync_WhenAtomicCommitFails_RetainsPreviousManifest()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository workingRepository = new();
        await workingRepository.CreateAsync(directory.Root, "Original name");
        string manifestPath = Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName);
        string originalManifest = await File.ReadAllTextAsync(manifestPath);
        VoiceBenchProjectDocument document = await workingRepository.LoadAsync(directory.Root);
        FileProjectRepository failingRepository = new(committer: new FailingCommitter());

        await Assert.ThrowsAsync<IOException>(
            () => failingRepository.SaveAsync(directory.Root, document with { Name = "Must not replace" }));

        Assert.Equal(originalManifest, await File.ReadAllTextAsync(manifestPath));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task LoadAsync_AtomicallyPersistsSchemaOneMigrationWithoutMovingScripts()
    {
        using TemporaryDirectory directory = new();
        VoiceBenchProjectLayout.Ensure(directory.Root);
        string scriptRelative = "02-scripts/existing.md";
        await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, scriptRelative), "Existing");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName),
            $$"""
            {
              "schemaVersion": 1,
              "projectId": "{{Guid.NewGuid()}}",
              "name": "Migrated",
              "segmentOrder": ["segment"],
              "segments": [{
                "segmentId": "segment",
                "title": "Existing",
                "scriptRelativePath": "{{scriptRelative}}",
                "sourceSha256": "{{SourceTextHasher.Compute("Existing")}}"
              }]
            }
            """);
        FileProjectRepository repository = new();

        VoiceBenchProjectDocument project = await repository.LoadAsync(directory.Root);

        Assert.Equal(2, project.SchemaVersion);
        Assert.Equal(scriptRelative, project.Segments.Single().ScriptRelativePath);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, scriptRelative)));
        JsonObject persisted = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory.Root, VoiceBenchProjectLayout.ManifestFileName)))!.AsObject();
        Assert.Equal(2, persisted["schemaVersion"]!.GetValue<int>());
        Assert.Null(persisted["segmentOrder"]);
    }

    private sealed class FailingCommitter : IAtomicFileCommitter
    {
        public void Commit(string temporaryPath, string destinationPath) =>
            throw new IOException("Synthetic atomic commit failure.");
    }
}
