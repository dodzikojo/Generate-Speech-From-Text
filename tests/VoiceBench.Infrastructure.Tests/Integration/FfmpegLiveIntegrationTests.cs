using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Mastering;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using IssueLab.VoiceBench.Infrastructure.Toolchain;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Integration;

public sealed class FfmpegLiveIntegrationTests
{
    [Fact]
    [Trait("Category", "LiveFfmpeg")]
    public async Task PinnedToolchain_BuildsAndVerifiesFixtureMaster()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VOICEBENCH_RUN_FFMPEG_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using HttpClient httpClient = new();
        ToolchainPaths tools = await new FfmpegToolchainManager(httpClient).EnsureAsync();
        using TemporaryDirectory directory = new();
        FileProjectRepository repository = new();
        VoiceBenchProjectDocument project = await repository.CreateAsync(directory.Root, "Live FFmpeg fixture");
        List<SegmentDocument> segments = [];
        foreach ((string id, string title, string text, double frequency, double seconds) in new[]
        {
            ("tone-a", "First tone", "The first fixture segment.", 440d, 0.80d),
            ("tone-b", "Second tone", "The second fixture segment.", 660d, 1.00d),
        })
        {
            string scriptRelative = $"02-scripts/{id}.md";
            string audioRelative = $"04-generated-audio/segments/{id}.processed.wav";
            string cleanRelative = $"03-render-copies/{id}.clean.md";
            string submittedRelative = $"03-render-copies/{id}.submitted.md";
            await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, scriptRelative), text);
            await File.WriteAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, cleanRelative), text);
            await File.WriteAllTextAsync(
                VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, submittedRelative),
                $"## Directions\n\n(none)\n\n## Exact submitted text\n\n{text}");
            WriteTone(
                VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, audioRelative),
                frequency,
                seconds);
            Guid renderId = Guid.NewGuid();
            string hash = SourceTextHasher.Compute(text);
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
                        ProviderId = "fixture",
                        ModelId = "sine",
                        Voice = "tone",
                        SourceSha256 = hash,
                        SubmittedTextSha256 = hash,
                        DirectionsSha256 = SourceTextHasher.Compute(string.Empty),
                        ApprovalSourceSha256 = hash,
                        RawAudioRelativePath = audioRelative,
                        ProcessedAudioRelativePath = audioRelative,
                        Duration = TimeSpan.FromSeconds(seconds),
                        SampleRate = 48_000,
                        Channels = 1,
                        CleanSourceRelativePath = cleanRelative,
                        SubmittedTextRelativePath = submittedRelative,
                    },
                ],
            });
        }

        await repository.SaveAsync(directory.Root, project with
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
        FfmpegMasteringService mastering = new(repository, new ProcessRunner(), tools);

        MasteringResult result = await mastering.BuildAsync(new MasteringRequest(directory.Root, "Fixture master"));

        Assert.Equal(48_000, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.InRange(result.IntegratedLufs, -17, -15);
        Assert.True(result.TruePeakDbtp <= -0.95, $"True peak was {result.TruePeakDbtp} dBTP.");
        Assert.InRange(result.Duration.TotalSeconds, 2.25, 2.35);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(directory.Root, result.MasterRelativePath)));
    }

    private static void WriteTone(string path, double frequency, double durationSeconds)
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        int sampleCount = checked((int)(sampleRate * durationSeconds));
        int dataLength = sampleCount * sizeof(short);
        using BinaryWriter writer = new(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        for (int index = 0; index < sampleCount; index++)
        {
            double envelope = Math.Min(1, Math.Min(index / 480d, (sampleCount - index) / 480d));
            short sample = (short)(Math.Sin(2 * Math.PI * frequency * index / sampleRate) * short.MaxValue * 0.12 * envelope);
            writer.Write(sample);
        }
    }
}
