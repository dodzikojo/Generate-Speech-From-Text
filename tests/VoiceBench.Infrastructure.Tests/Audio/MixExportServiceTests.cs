using System.Text.Json;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Audio;

public sealed class MixExportServiceTests
{
    [Fact]
    public async Task ConfiguredMono44100SixteenBitOutputConvertsBeforeLoudnessMeasurementAndVerifiesPcm()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Configured");
        var runner = new AudioAssetServiceTests.AudioProcessFake(); var assets = new AudioAssetService(repository, runner, AudioAssetServiceTests.Tools);
        string file = Path.Combine(dir.Root, "source.wav"); await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var plan = Plan(await assets.ImportAsync(dir.Root, file)) with { OutputStandard = new MixOutputStandard { SampleRate = 44100, Channels = 1, PcmBitsPerSample = 16 } };
        var result = await new MixExportService(assets, runner, AudioAssetServiceTests.Tools).ExportAsync(dir.Root, plan, "Configured", true);
        var pcm = Assert.Single(runner.Requests, r => r.Arguments.Contains("pcm_s16le"));
        Assert.Equal("44100", pcm.Arguments[pcm.Arguments.ToList().LastIndexOf("-ar") + 1]);
        Assert.Equal("1", pcm.Arguments[pcm.Arguments.ToList().LastIndexOf("-ac") + 1]);
        Assert.Contains(runner.Requests, r => r.Arguments.Any(a => a.StartsWith("aresample=44100,aformat=channel_layouts=mono,loudnorm=", StringComparison.Ordinal)));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, result.ReportRelativePath)));
        Assert.Equal(44100, report.RootElement.GetProperty("outputFormat").GetProperty("sampleRate").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("outputFormat").GetProperty("channels").GetInt32());
        Assert.Equal(16, report.RootElement.GetProperty("outputFormat").GetProperty("pcmBitsPerSample").GetInt32());
        Assert.Equal(48000, report.RootElement.GetProperty("mp3Format").GetProperty("sampleRate").GetInt32());
    }
    [Fact]
    public async Task CueSheetsUseFrozenRetainedAlignedWordsAndKeepUncertainAnchorsUnplaced()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Cues");
        var runner = new AudioAssetServiceTests.AudioProcessFake(); var assets = new AudioAssetService(repository, runner, AudioAssetServiceTests.Tools);
        string file = Path.Combine(dir.Root, "source.wav"); await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var original = Plan(await assets.ImportAsync(dir.Root, file));
        var clip = original.Clips[0] with { Track = original.Clips[0].Track with { Kind = AudioTrackKind.Narration }, RetainedWordRanges = [new(0, 2)],
            AlignedWords = [new("Hello", TimeSpan.FromSeconds(.002), TimeSpan.FromSeconds(.006), 0, 0)],
            ProductionCues = [new(1, ProductionCueKind.Visual, "visual", "Known anchor", 1, 0, 1, 0),
                new(2, ProductionCueKind.Note, "note", "Uncertain anchor", 2, 0, 1, 1), new(3, ProductionCueKind.Action, "action", "Removed anchor", 3, 0, 1, 7)] };
        var result = await new MixExportService(assets, runner, AudioAssetServiceTests.Tools).ExportAsync(dir.Root, original with { Clips = [clip] }, "Cues");
        string csv = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, result.CueSheetRelativePath));
        Assert.Contains(",0,true,0.002,", csv); Assert.Contains(",1,false,,", csv); Assert.Contains(",7,false,,", csv);
        Assert.Contains("Known anchor", csv); Assert.Contains("Uncertain anchor", csv); Assert.Contains("Removed anchor", csv);
    }
    [Fact]
    public async Task ExportVersionsMasterAndTranscriptsAndPublishesCompletionLast()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Mix");
        var runner = new AudioAssetServiceTests.AudioProcessFake(); var assets = new AudioAssetService(repository, runner, AudioAssetServiceTests.Tools);
        string file = Path.Combine(dir.Root, "source.wav"); await File.WriteAllBytesAsync(file, [1, 2, 3]); var asset = await assets.ImportAsync(dir.Root, file);
        var plan = Plan(asset); var service = new MixExportService(assets, runner, AudioAssetServiceTests.Tools);
        var first = await service.ExportAsync(dir.Root, plan, "Mix", true, true);
        var second = await service.ExportAsync(dir.Root, plan, "Mix");
        Assert.NotEqual(first.MasterRelativePath, second.MasterRelativePath);
        Assert.Contains("Mix-v001", first.MasterRelativePath); Assert.Contains("Mix-v002", second.MasterRelativePath);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, first.MasterRelativePath)));
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, first.CompletionRelativePath)));
        Assert.Equal("Retained words only", await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, first.TranscriptRelativePath)));
        Assert.Single(first.StemRelativePaths); Assert.NotNull(first.Mp3RelativePath);
        string report = await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, first.ReportRelativePath));
        using var json = JsonDocument.Parse(report); Assert.Equal(plan.PlanId.ToString(), json.RootElement.GetProperty("plan").GetProperty("planId").GetString());
        Assert.Contains(runner.Requests, r => r.Arguments.Any(a => a.Contains("linear=true", StringComparison.Ordinal)));
        Assert.All(runner.PremasterBlocks, block => Assert.Equal(new float[] { .25f, -.5f }, block));
    }

    [Fact]
    public async Task FailureAndCancellationRetainPreviousCompletedExport()
    {
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Mix");
        var runner = new AudioAssetServiceTests.AudioProcessFake(); var assets = new AudioAssetService(repository, runner, AudioAssetServiceTests.Tools);
        string file = Path.Combine(dir.Root, "source.wav"); await File.WriteAllBytesAsync(file, [1, 2, 3]); var plan = Plan(await assets.ImportAsync(dir.Root, file));
        var service = new MixExportService(assets, runner, AudioAssetServiceTests.Tools);
        var first = await service.ExportAsync(dir.Root, plan, "Mix");
        byte[] original = await File.ReadAllBytesAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, first.MasterRelativePath));
        runner.FailNormalization = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(dir.Root, plan, "Mix"));
        Assert.Equal(original, await File.ReadAllBytesAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, first.MasterRelativePath)));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Root, "05-masters"), "complete.json", SearchOption.AllDirectories));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(dir.Root, plan, "Mix", cancellationToken: cancelled.Token));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Root, "05-masters"), "complete.json", SearchOption.AllDirectories));
    }
    private static MixRenderPlan Plan(AudioAssetRecord asset)
    {
        Guid id = Guid.NewGuid();
        return new() { DurationSeconds = .02, Clips = [new MixRenderClip { ClipId = id, Asset = asset,
            DurationFrames = 960, Track = new AudioTrackDocument { Name = "Effects", Kind = AudioTrackKind.Effects } }],
            Transcript = [new(id, "segment", Guid.NewGuid(), 0, [new(3, 6)], "Retained words only")] };
    }
}
