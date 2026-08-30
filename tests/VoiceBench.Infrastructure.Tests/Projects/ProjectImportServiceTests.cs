using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Projects;

public sealed class ProjectImportServiceTests
{
    [Fact]
    public async Task ImportAsync_SplitsHeadingsAndCreatesCanonicalFiles()
    {
        using TemporaryDirectory directory = new();
        string projectRoot = Path.Combine(directory.Root, "project");
        string sourcePath = Path.Combine(directory.Root, "lesson.md");
        await File.WriteAllTextAsync(sourcePath, "## Introduction\nWelcome.\n---\nSecond scene.");
        FileProjectRepository repository = new();
        await repository.CreateAsync(projectRoot, "Lesson");
        Queue<string> ids = new(["a1b2c3d4", "e5f6a7b8"]);
        ProjectImportService importer = new(
            repository,
            stableIdFactory: ids.Dequeue,
            chapterIdFactory: () => "chapter123");

        VoiceBenchProjectDocument result = await importer.ImportAsync(projectRoot, sourcePath);

        ChapterDocument chapter = Assert.Single(result.Chapters);
        Assert.Equal("lesson", chapter.Title);
        Assert.Equal(["a1b2c3d4", "e5f6a7b8"], chapter.SegmentOrder);
        Assert.Equal([chapter.ChapterId], result.ChapterOrder);
        Assert.Collection(
            result.Segments,
            first =>
            {
                Assert.Equal("Introduction", first.Title);
                Assert.Equal("02-scripts/001-lesson--chapte/001-introduction--a1b2c3.md", first.ScriptRelativePath);
            },
            second =>
            {
                Assert.Equal("lesson 2", second.Title);
                Assert.Equal("02-scripts/001-lesson--chapte/002-lesson-2--e5f6a7.md", second.ScriptRelativePath);
            });
        Assert.All(result.Segments, segment =>
            Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(projectRoot, segment.ScriptRelativePath))));
    }

    [Fact]
    public async Task ImportAsync_RejectsUnsupportedFileTypes()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await repository.CreateAsync(directory.Root, "Unsupported");
        string sourcePath = Path.Combine(directory.Root, "script.docx");
        await File.WriteAllBytesAsync(sourcePath, []);
        ProjectImportService importer = new(repository);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => importer.ImportAsync(directory.Root, sourcePath));
    }

    [Fact]
    public async Task ImportFilesAsync_CreatesOneOrderedChapterPerSelectedFile()
    {
        using TemporaryDirectory directory = new();
        string projectRoot = Path.Combine(directory.Root, "project");
        string first = Path.Combine(directory.Root, "one.md");
        string second = Path.Combine(directory.Root, "two.txt");
        await File.WriteAllTextAsync(first, "One");
        await File.WriteAllTextAsync(second, "Two");
        FileProjectRepository repository = new();
        await repository.CreateAsync(projectRoot, "Course");
        Queue<string> chapterIds = new(["chapter-one", "chapter-two"]);
        Queue<string> segmentIds = new(["segment-one", "segment-two"]);
        ProjectImportService importer = new(
            repository,
            stableIdFactory: segmentIds.Dequeue,
            chapterIdFactory: chapterIds.Dequeue);

        VoiceBenchProjectDocument project = await importer.ImportFilesAsync(projectRoot, [first, second]);

        Assert.Equal(["chapter-one", "chapter-two"], project.ChapterOrder);
        Assert.Equal(["one", "two"], project.Chapters.Select(chapter => chapter.Title));
        Assert.Equal("chapter-one", project.Segments[0].ChapterId);
        Assert.Equal("chapter-two", project.Segments[1].ChapterId);
    }
}
