using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Rendering;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Mastering;

public sealed class ProductionExportServiceTests
{
    [Fact]
    public async Task ExportAsync_writes_authoring_script_and_ordered_cues_without_timings()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Video training");
        const string relativePath = "02-scripts/chapter/segment.md";
        const string text = "@visual: Show the dashboard.\n\nWelcome to the matrix.\n\n@camera-note: Crop the project name.";
        string path = VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        SegmentDocument segment = new()
        {
            SegmentId = "segment",
            ChapterId = "chapter",
            Title = "Opening",
            ScriptRelativePath = relativePath,
            SourceSha256 = SourceTextHasher.Compute(parsed.NarrationText),
            AuthoringSha256 = SourceTextHasher.Compute(text),
        };
        project = project with
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", Title = "Introduction", SegmentOrder = ["segment"] }],
            Segments = [segment],
        };
        await repository.SaveAsync(directory.Root, project);
        ProductionExportService service = new(repository, new FixedTimeProvider());

        ProductionExportResult result = await service.ExportAsync(
            new ProductionExportRequest(directory.Root, "Review pack"));

        string production = await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, result.ProductionScriptRelativePath));
        string cues = await File.ReadAllTextAsync(
            VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, result.CueSheetMarkdownRelativePath));
        Assert.Contains("@visual: Show the dashboard.", production, StringComparison.Ordinal);
        Assert.Contains("@camera-note", cues, StringComparison.Ordinal);
        Assert.Contains("| 2 | Introduction | Opening | `@camera-note` | 4 |", cues, StringComparison.Ordinal);
        Assert.DoesNotContain("timestamp", cues, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.SegmentCount);
        Assert.Equal(2, result.CueCount);
    }

    [Fact]
    public async Task ExportAsync_allocates_a_new_version_when_the_clock_and_name_match()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new(new FixedTimeProvider());
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Versioned export");
        const string relativePath = "02-scripts/chapter/segment.md";
        const string text = "Narration.\n\n@visual: Show the overview.";
        string path = VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        ParsedScriptDocument parsed = new ProductionScriptParser().Parse(text);
        project = project with
        {
            ChapterOrder = ["chapter"],
            Chapters = [new ChapterDocument { ChapterId = "chapter", Title = "Chapter", SegmentOrder = ["segment"] }],
            Segments =
            [
                new SegmentDocument
                {
                    SegmentId = "segment",
                    ChapterId = "chapter",
                    Title = "Segment",
                    ScriptRelativePath = relativePath,
                    SourceSha256 = SourceTextHasher.Compute(parsed.NarrationText),
                    AuthoringSha256 = SourceTextHasher.Compute(text),
                },
            ],
        };
        await repository.SaveAsync(directory.Root, project);
        ProductionExportService service = new(repository, new FixedTimeProvider());

        ProductionExportResult first = await service.ExportAsync(new ProductionExportRequest(directory.Root, "Review"));
        ProductionExportResult second = await service.ExportAsync(new ProductionExportRequest(directory.Root, "Review"));

        Assert.NotEqual(first.ProductionScriptRelativePath, second.ProductionScriptRelativePath);
        Assert.Contains("--v001", first.ProductionScriptRelativePath, StringComparison.Ordinal);
        Assert.Contains("--v002", second.ProductionScriptRelativePath, StringComparison.Ordinal);
    }
}
