using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Packaging;
using IssueLab.VoiceBench.Infrastructure.Projects;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Packaging;

/// <summary>A small project on disk: chapter "Introduction" with two approved segments and one awaiting review.</summary>
internal static class PackagingFixture
{
    public static readonly Guid OpeningOld = Guid.Parse("00000000-0000-0000-0000-000000000101");
    public static readonly Guid OpeningApproved = Guid.Parse("00000000-0000-0000-0000-000000000102");
    public static readonly Guid ObserveApproved = Guid.Parse("00000000-0000-0000-0000-000000000201");
    public static readonly Guid ReviewTake = Guid.Parse("00000000-0000-0000-0000-000000000301");

    public static async Task<VoiceBenchProjectDocument> CreateAsync(FileProjectRepository repository, string root)
    {
        VoiceBenchProjectDocument project = await repository.CreateAsync(root, "Rail smoke");
        project = project with
        {
            ChapterOrder = ["intro"],
            Chapters = [new ChapterDocument { ChapterId = "intro", Title = "Introduction", SegmentOrder = ["opening", "observe", "review"] }],
            Segments =
            [
                Segment("opening", "Opening", SegmentStatus.Approved, OpeningApproved,
                    await TakeAsync(root, "opening", OpeningOld, 1, RenderStatus.Generated, 1000, processedVersions: 2),
                    await TakeAsync(root, "opening", OpeningApproved, 2, RenderStatus.Approved, 2000, processedVersions: 1)),
                Segment("observe", "Observe", SegmentStatus.Approved, ObserveApproved,
                    await TakeAsync(root, "observe", ObserveApproved, 1, RenderStatus.Approved, 3000, processedVersions: 1)),
                Segment("review", "Review", SegmentStatus.Rendered, null,
                    await TakeAsync(root, "review", ReviewTake, 1, RenderStatus.Generated, 4000, processedVersions: 0)),
            ],
        };
        await repository.SaveAsync(root, project);
        return await repository.LoadAsync(root);
    }

    public static string Full(string root, string relative) => VoiceBenchProjectLayout.ResolveRelativePath(root, relative);

    private static SegmentDocument Segment(string id, string title, SegmentStatus status, Guid? approved, params RenderRecord[] takes) => new()
    {
        SegmentId = id,
        ChapterId = "intro",
        Title = title,
        Status = status,
        ApprovedRenderId = approved,
        ScriptRelativePath = $"02-scripts/intro/{id}.md",
        RenderHistory = [.. takes],
    };

    /// <summary>Writes a raw file and <paramref name="processedVersions"/> processed versions filled with distinct bytes.</summary>
    private static async Task<RenderRecord> TakeAsync(string root, string segmentId, Guid id, int number, RenderStatus status, int bytes, int processedVersions)
    {
        string folder = $"04-generated-audio/segments/{segmentId}";
        string raw = $"{folder}/{segmentId}--take-{number:0000}.raw.wav";
        await WriteAsync(root, raw, bytes, (byte)(number + segmentId.Length));
        string? processed = null;
        for (int version = 1; version <= processedVersions; version++)
        {
            processed = $"{folder}/{segmentId}--take-{number:0000}--processed-{version:000}.wav";
            await WriteAsync(root, processed, bytes / 2, (byte)(version * 7 + number + segmentId.Length));
        }

        return new RenderRecord
        {
            RenderId = id,
            TakeNumber = number,
            Status = status,
            RawAudioRelativePath = raw,
            ProcessedAudioRelativePath = processed,
            Duration = TimeSpan.FromSeconds(number),
        };
    }

    public static async Task WriteAsync(string root, string relative, int bytes, byte fill)
    {
        string path = Full(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat(fill, bytes).ToArray());
    }
}

/// <summary>Moves "recycled" files into a folder the test can inspect, or fails on demand.</summary>
internal sealed class FakeRecycler(string bin) : IFileRecycler
{
    public List<string> Recycled { get; } = [];

    public Func<string, bool> FailFor { get; set; } = _ => false;

    public void Recycle(IReadOnlyList<string> fullPaths)
    {
        if (fullPaths.Any(FailFor)) throw new IOException("The file is in use.");
        Directory.CreateDirectory(bin);
        foreach (string path in fullPaths)
        {
            File.Move(path, Path.Combine(bin, Guid.NewGuid().ToString("N") + Path.GetFileName(path)));
            Recycled.Add(path);
        }
    }
}
