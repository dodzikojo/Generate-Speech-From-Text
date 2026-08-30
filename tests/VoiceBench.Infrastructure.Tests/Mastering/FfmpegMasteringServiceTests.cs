using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Mastering;

public sealed class FfmpegMasteringServiceTests
{
    [Fact]
    public async Task BuildAsync_NormalizesConcatenatesVerifiesAndExportsInManifestOrder()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await CreateApprovedProjectAsync(repository, directory.Root);
        RecordingProcessRunner runner = new();
        FfmpegMasteringService service = new(
            repository,
            runner,
            new ToolchainPaths("ffmpeg.exe", "ffprobe.exe"));

        MasteringResult result = await service.BuildAsync(new MasteringRequest(directory.Root, "Training Master"));

        Assert.Equal("05-masters/training-master-v001.master.wav", result.MasterRelativePath);
        Assert.Equal(48_000, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.Equal(-16, result.IntegratedLufs);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, result.MasterRelativePath)));
        string transcript = await File.ReadAllTextAsync(Path.Combine(
            directory.Root,
            VoiceBenchProjectLayout.TranscriptsDirectory,
            "training-master-v001.transcript.md"));
        Assert.True(transcript.IndexOf("First words", StringComparison.Ordinal) < transcript.IndexOf("Second words", StringComparison.Ordinal));
        Assert.Contains(runner.Requests, request => request.Arguments.Any(argument => argument.Contains("measured_I=-16", StringComparison.Ordinal)));
        Assert.Contains(runner.Requests, request => request.Arguments.SequenceEqual(
            ["-v", "error", "-show_entries", "stream=sample_rate,channels:format=duration", "-of", "json", request.Arguments.Last()]));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(directory.Root, VoiceBenchProjectLayout.ReportsDirectory),
            ".*-work-*"));
    }

    [Fact]
    public async Task BuildAsync_UsesImmutableVersionedMasterNames()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await CreateApprovedProjectAsync(repository, directory.Root);
        RecordingProcessRunner runner = new();
        FfmpegMasteringService service = new(
            repository,
            runner,
            new ToolchainPaths("ffmpeg.exe", "ffprobe.exe"));

        MasteringResult first = await service.BuildAsync(new MasteringRequest(directory.Root, "Master"));
        MasteringResult second = await service.BuildAsync(new MasteringRequest(directory.Root, "Master"));

        Assert.EndsWith("master-v001.master.wav", first.MasterRelativePath, StringComparison.Ordinal);
        Assert.EndsWith("master-v002.master.wav", second.MasterRelativePath, StringComparison.Ordinal);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, first.MasterRelativePath)));
    }

    [Fact]
    public async Task BuildAsync_PreservesWorkFilesWhenFfmpegFails()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await CreateApprovedProjectAsync(repository, directory.Root);
        RecordingProcessRunner runner = new(failConcat: true);
        FfmpegMasteringService service = new(
            repository,
            runner,
            new ToolchainPaths("ffmpeg.exe", "ffprobe.exe"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BuildAsync(new MasteringRequest(directory.Root, "Failed")));

        Assert.NotEmpty(Directory.EnumerateDirectories(
            Path.Combine(directory.Root, VoiceBenchProjectLayout.ReportsDirectory),
            ".*-work-*"));
    }

    [Fact]
    public async Task BuildAsync_UsesChapterPauseAndSupportsChapterScope()
    {
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        await CreateApprovedProjectAsync(repository, directory.Root);
        VoiceBenchProjectDocument project = await repository.LoadAsync(directory.Root);
        SegmentDocument first = project.Segments[0] with { ChapterId = "chapter-a" };
        SegmentDocument second = project.Segments[1] with { ChapterId = "chapter-b" };
        project = project with
        {
            Segments = [first, second],
            ChapterOrder = ["chapter-a", "chapter-b"],
            Chapters =
            [
                new ChapterDocument { ChapterId = "chapter-a", Title = "A", SegmentOrder = [first.SegmentId] },
                new ChapterDocument { ChapterId = "chapter-b", Title = "B", SegmentOrder = [second.SegmentId] },
            ],
        };
        await repository.SaveAsync(directory.Root, project);
        RecordingProcessRunner runner = new();
        FfmpegMasteringService service = new(repository, runner, new ToolchainPaths("ffmpeg.exe", "ffprobe.exe"));

        await service.BuildAsync(new MasteringRequest(directory.Root, "Compilation", MasterScope.Compilation));

        Assert.Contains(runner.Requests, request =>
            request.Arguments.Contains("anullsrc=r=48000:cl=mono", StringComparer.Ordinal) &&
            request.Arguments.Contains("1", StringComparer.Ordinal));

        runner.Requests.Clear();
        await service.BuildAsync(new MasteringRequest(directory.Root, "Chapter A", MasterScope.Chapter, "chapter-a"));
        Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("anullsrc=r=48000:cl=mono", StringComparer.Ordinal));
    }

    private static async Task CreateApprovedProjectAsync(FileProjectRepository repository, string root)
    {
        VoiceBenchProjectDocument project = await repository.CreateAsync(root, "Fixture");
        List<SegmentDocument> segments = [];
        foreach ((string id, string title, string text, int duration) in new[]
        {
            ("segment-a", "First", "First words", 1),
            ("segment-b", "Second", "Second words", 2),
        })
        {
            string scriptRelative = $"02-scripts/{id}.md";
            string processedRelative = $"04-generated-audio/segments/{id}-v001.processed.wav";
            string cleanRelative = $"03-render-copies/{id}-v001.clean.md";
            string submittedRelative = $"03-render-copies/{id}-v001.submitted.md";
            await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, scriptRelative), text);
            await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, cleanRelative), text);
            await File.WriteAllTextAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(root, submittedRelative),
                $"## Directions\n\n(none)\n\n## Exact submitted text\n\n{text}");
            await File.WriteAllBytesAsync(VoiceBenchProjectLayout.ResolveRelativePath(root, processedRelative), [1, 2, 3]);
            string hash = SourceTextHasher.Compute(text);
            Guid renderId = Guid.NewGuid();
            segments.Add(new SegmentDocument
            {
                SegmentId = id,
                ChapterId = "chapter",
                Title = title,
                ScriptRelativePath = scriptRelative,
                SourceSha256 = hash,
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
                        SubmittedTextSha256 = hash,
                        DirectionsSha256 = SourceTextHasher.Compute(string.Empty),
                        ApprovalSourceSha256 = hash,
                        RawAudioRelativePath = $"04-generated-audio/segments/{id}-v001.raw.wav",
                        ProcessedAudioRelativePath = processedRelative,
                        CleanSourceRelativePath = cleanRelative,
                        SubmittedTextRelativePath = submittedRelative,
                        Duration = TimeSpan.FromSeconds(duration),
                    },
                ],
            });
        }

        await repository.SaveAsync(root, project with
        {
            Segments = segments,
            ChapterOrder = ["chapter"],
            Chapters =
            [
                new ChapterDocument
                {
                    ChapterId = "chapter",
                    Title = "Chapter",
                    SegmentOrder = segments.Select(segment => segment.SegmentId).ToList(),
                },
            ],
        });
    }

    private sealed class RecordingProcessRunner(bool failConcat = false) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (failConcat && request.Arguments.Contains("concat", StringComparer.Ordinal))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "synthetic concat failure"));
            }

            if (string.Equals(Path.GetFileName(request.FileName), "ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    "{\"streams\":[{\"sample_rate\":\"48000\",\"channels\":1}],\"format\":{\"duration\":\"3.500\"}}",
                    string.Empty));
            }

            if (request.Arguments.Contains("null", StringComparer.Ordinal))
            {
                return Task.FromResult(new ProcessResult(
                    0,
                    string.Empty,
                    "analysis\n{\"input_i\":\"-16.00\",\"input_tp\":\"-1.10\",\"input_lra\":\"0.10\",\"input_thresh\":\"-26.00\",\"target_offset\":\"0.00\"}"));
            }

            string output = request.Arguments.Last();
            if (output.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, [82, 73, 70, 70]);
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
