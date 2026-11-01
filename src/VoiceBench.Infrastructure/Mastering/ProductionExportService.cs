using System.Globalization;
using System.Text;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Mastering;

public sealed class ProductionExportService(
    IProjectRepository repository,
    TimeProvider? timeProvider = null) : IProductionExportService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProductionExportResult> ExportAsync(
        ProductionExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        string root = Path.GetFullPath(request.ProjectRoot);
        VoiceBenchProjectDocument project = await repository.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> orderedIds = ProjectOrdering.GetOrderedSegmentIds(project, request.Scope, request.ChapterId);
        Dictionary<string, SegmentDocument> segments = project.Segments.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        Dictionary<string, ChapterDocument> chapters = project.Chapters.ToDictionary(item => item.ChapterId, StringComparer.Ordinal);
        ProductionScriptParser parser = new();

        List<string> production = [$"# {project.Name} production script", ""];
        List<string> cueMarkdown =
        [
            $"# {project.Name} production cues",
            "",
            "| # | Chapter | Segment | Type | After words | Guidance |",
            "|---:|---|---|---|---:|---|",
        ];
        List<string> cueCsv = ["chapter_id,chapter_title,segment_id,segment_title,cue_order,type,after_narration_words,guidance"];
        string? currentChapterId = null;
        int cueOrdinal = 0;

        foreach (string segmentId in orderedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SegmentDocument segment = segments[segmentId];
            ChapterDocument chapter = chapters[segment.ChapterId];
            string authoringText = await File.ReadAllTextAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(root, segment.ScriptRelativePath),
                cancellationToken).ConfigureAwait(false);
            ParsedScriptDocument parsed = parser.Parse(authoringText);
            if (parsed.HasErrors)
            {
                throw new InvalidDataException(
                    $"Segment '{segment.Title}' has invalid production cues: " +
                    string.Join(" ", parsed.Diagnostics.Select(item => $"line {item.SourceLine}: {item.Message}")));
            }

            if (!string.Equals(currentChapterId, chapter.ChapterId, StringComparison.Ordinal))
            {
                currentChapterId = chapter.ChapterId;
                production.Add($"## {chapter.Title}");
                production.Add(string.Empty);
            }

            production.Add($"### {segment.Title}");
            production.Add(string.Empty);
            production.Add(authoringText.Trim());
            production.Add(string.Empty);

            foreach (ProductionCue cue in parsed.Cues)
            {
                cueOrdinal++;
                cueMarkdown.Add(
                    $"| {cueOrdinal} | {Md(chapter.Title)} | {Md(segment.Title)} | `@{cue.Type}` | " +
                    $"{cue.PrecedingNarrationWords} | {Md(cue.Content)} |");
                cueCsv.Add(string.Join(",",
                    Csv(chapter.ChapterId),
                    Csv(chapter.Title),
                    Csv(segment.SegmentId),
                    Csv(segment.Title),
                    cueOrdinal.ToString(CultureInfo.InvariantCulture),
                    Csv(cue.Type),
                    cue.PrecedingNarrationWords.ToString(CultureInfo.InvariantCulture),
                    Csv(cue.Content)));
            }
        }

        string timestamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string stem = ResolveAvailableStem(root, $"{SafeFilePart(request.Name)}--{timestamp}");
        string scriptRelative = $"{VoiceBenchProjectLayout.ProductionScriptsDirectory}/{stem}.production.md";
        string cueMarkdownRelative = $"{VoiceBenchProjectLayout.CueSheetsDirectory}/{stem}.production-cues.md";
        string cueCsvRelative = $"{VoiceBenchProjectLayout.CueSheetsDirectory}/{stem}.production-cues.csv";
        await WriteVersionedAsync(root, scriptRelative, production, cancellationToken).ConfigureAwait(false);
        await WriteVersionedAsync(root, cueMarkdownRelative, cueMarkdown, cancellationToken).ConfigureAwait(false);
        await WriteVersionedAsync(root, cueCsvRelative, cueCsv, cancellationToken).ConfigureAwait(false);
        return new ProductionExportResult(scriptRelative, cueMarkdownRelative, cueCsvRelative, orderedIds.Count, cueOrdinal);
    }

    private static string ResolveAvailableStem(string root, string baseStem)
    {
        for (int version = 1; version <= 999; version++)
        {
            string stem = $"{baseStem}--v{version:000}";
            string[] relativePaths =
            [
                $"{VoiceBenchProjectLayout.ProductionScriptsDirectory}/{stem}.production.md",
                $"{VoiceBenchProjectLayout.CueSheetsDirectory}/{stem}.production-cues.md",
                $"{VoiceBenchProjectLayout.CueSheetsDirectory}/{stem}.production-cues.csv",
            ];
            if (relativePaths.All(path => !File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(root, path))))
            {
                return stem;
            }
        }

        throw new IOException("VoiceBench could not allocate the next production-export version.");
    }

    private static async Task WriteVersionedAsync(
        string root,
        string relativePath,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        string path = VoiceBenchProjectLayout.ResolveRelativePath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false));
        foreach (string line in lines) await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

    private static string SafeFilePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Trim().Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : char.ToLowerInvariant(character)).ToArray());
        safe = safe.Trim('-', '.');
        return string.IsNullOrWhiteSpace(safe) ? "production" : safe;
    }
}
