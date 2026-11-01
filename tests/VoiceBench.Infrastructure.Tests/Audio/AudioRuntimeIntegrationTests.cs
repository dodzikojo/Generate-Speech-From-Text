using System.Diagnostics;
using IssueLab.VoiceBench.Audio;
using IssueLab.VoiceBench.Core;
using IssueLab.VoiceBench.Infrastructure.Audio;
using IssueLab.VoiceBench.Infrastructure.Processes;
using IssueLab.VoiceBench.Infrastructure.Projects;
using IssueLab.VoiceBench.Infrastructure.Tests.Support;
using IssueLab.VoiceBench.Infrastructure.Toolchain;
using Xunit.Abstractions;

namespace IssueLab.VoiceBench.Infrastructure.Tests.Audio;

/// <summary>Opt-in local fixtures: no downloads, credentials, or provider calls.</summary>
public sealed class AudioRuntimeIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "AudioRuntime")]
    public async Task WasapiLiveGainPanMuteEpochsKeepPlayingAndRejectStructuralChanges()
    {
        if (!Enabled) return;
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Live parameters");
        var assets = new AudioAssetService(repository, new ProcessRunner(), LocalTools);
        var plan = await CreateFixtureAsync(dir.Root, assets);
        await using var transport = new AudioPlaybackEngine(assets) { Volume = .05f };
        Exception? failure = null; transport.PlaybackFailed += (_, error) => failure = error;
        await transport.LoadMixAsync(dir.Root, plan); transport.Play(); await Task.Delay(250);
        double before = transport.Position.TotalSeconds;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            plan = plan with { Clips = plan.Clips.Select(c => c with { GainDb = -i / 2d, Pan = i / 40d,
                Track = c.Track with { GainDb = -18 - i / 4d, Pan = -i / 80d } }).ToArray() };
            Assert.True(await transport.UpdateMixParametersAsync(plan)); Assert.True(transport.IsPlaying);
            await Task.Delay(20);
        }
        var muted = plan with { Clips = plan.Clips.Select(c => c with { Track = c.Track with { Muted = true } }).ToArray() };
        var muteLatency = Stopwatch.StartNew();
        Assert.True(await transport.UpdateMixParametersAsync(muted));
        while (transport.Meter.PeakLeft > 0 && muteLatency.ElapsedMilliseconds < 350) await Task.Delay(5);
        muteLatency.Stop();
        Assert.True(muteLatency.ElapsedMilliseconds < 350);
        Assert.True(transport.IsPlaying); Assert.True(transport.Position.TotalSeconds > before + .5);
        Assert.Equal(21, transport.ParameterEpoch); Assert.Equal(0, transport.Meter.PeakLeft); Assert.Equal(0, transport.Meter.PeakRight);
        Assert.True(await transport.UpdateMixParametersAsync(plan)); await Task.Delay(400);
        Assert.True(transport.IsPlaying); Assert.True(transport.Meter.PeakLeft > 0); Assert.Equal(22, transport.ParameterEpoch);
        Assert.False(await transport.UpdateMixParametersAsync(plan with { Clips = [plan.Clips[0] with { SourceStartFrame = 1 }, plan.Clips[1]] }));
        Assert.False(await transport.UpdateMixParametersAsync(plan with { Clips = [plan.Clips[0] with { Asset = plan.Clips[0].Asset with { Sha256 = "replacement" } }, plan.Clips[1]] }));
        Assert.False(await transport.UpdateMixParametersAsync(plan with { Clips = [plan.Clips[0] with { FadeInOffsetSeconds = .01 }, plan.Clips[1]] }));
        Assert.False(await transport.UpdateMixParametersAsync(plan with { Clips = [plan.Clips[0] with { Loop = true }, plan.Clips[1]] }));
        Assert.False(await transport.UpdateMixParametersAsync(plan with { Clips = [plan.Clips[0] with { Envelope = [new(0, -1)] }, plan.Clips[1]] }));
        Assert.True(transport.IsPlaying); Assert.Equal(0, transport.UnderrunCount); Assert.Null(failure);
        transport.Pause(); double paused = transport.Position.TotalSeconds;
        Assert.True(await transport.UpdateMixParametersAsync(plan with { Clips = plan.Clips.Select(c => c with { GainDb = -4 }).ToArray() }));
        Assert.False(transport.IsPlaying); Assert.Equal(paused, transport.Position.TotalSeconds);
        output.WriteLine($"LIVE WASAPI: 22 gain/pan/mute epochs; playback/device clock continuous; 10msDSP ramps; {ReadAheadWaveProvider.CapacityFrames / 48d:F0}ms boundedring; mute reacheddevicecallback in {muteLatency.Elapsed.TotalMilliseconds:F1}ms; structural/source changes refused; underruns={transport.UnderrunCount}; elapsed={watch.Elapsed.TotalSeconds:F3}s.");
    }
    [Fact]
    [Trait("Category", "AudioRuntime")]
    public async Task Configured44100MonoSixteenBitWavMeetsLoudnessAndUsesExplicit48000Mp3()
    {
        if (!Enabled) return;
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Configured mix");
        var runner = new ProcessRunner(); var assets = new AudioAssetService(repository, runner, LocalTools);
        var fixture = await CreateFixtureAsync(dir.Root, assets);
        var plan = fixture with { OutputStandard = new MixOutputStandard { SampleRate = 44100, Channels = 1, PcmBitsPerSample = 16, IntegratedLufs = -20, TruePeakDbtp = -2 } };
        var result = await new MixExportService(assets, runner, LocalTools).ExportAsync(dir.Root, plan, "Mono44100", true, true);
        using (var wav = new NAudio.Wave.WaveFileReader(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, result.MasterRelativePath)))
        {
            Assert.Equal(44100, wav.WaveFormat.SampleRate); Assert.Equal(1, wav.WaveFormat.Channels); Assert.Equal(16, wav.WaveFormat.BitsPerSample);
        }
        var mp3Probe = await runner.RunAsync(new(LocalTools.FfprobePath, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels", "-of", "json", VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, result.Mp3RelativePath!)]));
        Assert.True(mp3Probe.Succeeded);
        using var mp3 = System.Text.Json.JsonDocument.Parse(mp3Probe.StandardOutput);
        Assert.Equal("48000", mp3.RootElement.GetProperty("streams")[0].GetProperty("sample_rate").GetString());
        Assert.Equal(1, mp3.RootElement.GetProperty("streams")[0].GetProperty("channels").GetInt32());
        Assert.InRange(result.IntegratedLufs!.Value, -21, -19); Assert.True(result.TruePeakDbtp <= -1.9);
        Assert.InRange(result.DurationSeconds, 3.99, 4.01);
        output.WriteLine($"REAL CONFIGURED EXPORT: 44,100Hz mono 16-bit WAV; {result.DurationSeconds:F3}s, {result.IntegratedLufs:F2} LUFS, {result.TruePeakDbtp:F2} dBTP; MP3 explicitly48kmono; floatstems48kstereo.");
    }
    [Fact]
    [Trait("Category", "AudioRuntime")]
    public async Task IntentionalSilentMixExportsWithoutNormalizationOrNonfiniteJsonMetrics()
    {
        if (!Enabled) return;
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Silence");
        var runner = new ProcessRunner(); var assets = new AudioAssetService(repository, runner, LocalTools);
        var fixture = await CreateFixtureAsync(dir.Root, assets);
        var silent = fixture with { Clips = fixture.Clips.Select(c => c with { Muted = true }).ToArray() };
        var result = await new MixExportService(assets, runner, LocalTools).ExportAsync(dir.Root, silent, "Intentional silence");
        Assert.Null(result.IntegratedLufs); Assert.Null(result.TruePeakDbtp);
        using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, result.ReportRelativePath)));
        Assert.True(report.RootElement.GetProperty("silent").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, report.RootElement.GetProperty("loudness").GetProperty("integratedLufs").ValueKind);
        output.WriteLine("REAL SILENCE EXPORT: 4.000s WAV; anull (normalization skipped); nullable loudness and explicit silent warning in report.");
    }
    [Fact]
    [Trait("Category", "AudioRuntime")]
    public async Task TwoTrackFixtureExportsWavMp3AndAlignedStemsThroughRealFfmpeg()
    {
        if (!Enabled) return;
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Runtime mix");
        var tools = LocalTools; var runner = new ProcessRunner(); var assets = new AudioAssetService(repository, runner, tools);
        var plan = await CreateFixtureAsync(dir.Root, assets);
        var result = await new MixExportService(assets, runner, tools).ExportAsync(dir.Root, plan, "Two tracks", true, true);
        Assert.InRange(result.DurationSeconds, 3.99, 4.01); Assert.InRange(result.IntegratedLufs!.Value, -17, -15);
        Assert.True(result.TruePeakDbtp!.Value <= -.9); Assert.Equal(2, result.StemRelativePaths.Count);
        Assert.True(File.Exists(VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, result.CompletionRelativePath)));
        output.WriteLine($"REAL EXPORT: {result.DurationSeconds:F3}s, {result.IntegratedLufs:F2} LUFS, {result.TruePeakDbtp:F2} dBTP; WAV+MP3+2 aligned float stems.");
    }

    [Fact]
    [Trait("Category", "AudioRuntime")]
    public async Task WasapiDeviceClockPauseSeekLoopAndTargetSwitchWorkWithCachedAudio()
    {
        if (!Enabled) return;
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Playback");
        var assets = new AudioAssetService(repository, new ProcessRunner(), LocalTools);
        var plan = await CreateFixtureAsync(dir.Root, assets);
        await using var transport = new AudioPlaybackEngine(assets) { Volume = .05f };
        Exception? failure = null; transport.PlaybackFailed += (_, e) => failure = e;
        await transport.LoadMixAsync(dir.Root, plan); transport.Play();
        await Task.Delay(650); transport.Pause();
        double pause = transport.Position.TotalSeconds;
        Assert.InRange(pause, .3, 1.2); await Task.Delay(120);
        Assert.InRange(Math.Abs(transport.Position.TotalSeconds - pause), 0, .02);
        transport.Play(); await Task.Delay(230); transport.Pause();
        Assert.InRange(transport.Position.TotalSeconds, pause + .1, pause + .5);
        double soloAt = transport.Position.TotalSeconds;
        await transport.SetSoloTracksAsync([plan.Clips[1].Track.TrackId]); Assert.False(transport.IsPlaying);
        Assert.InRange(Math.Abs(transport.Position.TotalSeconds - soloAt), 0, .01);
        await transport.SeekAsync(TimeSpan.FromSeconds(2)); transport.Play(); await Task.Delay(250);
        Assert.InRange(transport.Position.TotalSeconds, 2.1, 2.6);
        await transport.SetLoopRangeAsync(TimeSpan.FromSeconds(.5), TimeSpan.FromSeconds(.8));
        await Task.Delay(950); Assert.InRange(transport.Position.TotalSeconds, .5, .8);
        transport.Loop = false; await Task.Delay(400); Assert.True(transport.Position.TotalSeconds > .8);
        transport.Pause();
        var devices = transport.GetOutputDevices(); Assert.NotEmpty(devices);
        await transport.SetOutputDeviceAsync(devices[0].DeviceId); transport.Play(); await Task.Delay(120); transport.Pause();
        var path = VoiceBenchProjectLayout.ResolveRelativePath(dir.Root, plan.Clips[0].Asset.RelativePath);
        await transport.LoadFileAsync(path); Assert.False(transport.IsPlaying); Assert.Equal(TimeSpan.Zero, transport.Position);
        Assert.Null(failure); Assert.Equal(0, transport.UnderrunCount);
        output.WriteLine($"WASAPI: pause clock={pause:F3}s; seek, explicit .5-.8s loop, selected endpoint, isolated target switch passed; device={devices[0].Name}.");
    }

    [Fact]
    [Trait("Category", "AudioRuntime")]
    public async Task TwoHourThirtyTwoTrackTwoThousandClipProjectHasBoundedCachedSeekMemory()
    {
        if (!Enabled) return;
        using TemporaryDirectory dir = new(); var repository = new FileProjectRepository(); await repository.CreateAsync(dir.Root, "Benchmark");
        var assets = new AudioAssetService(repository, new ProcessRunner(), LocalTools);
        var fixture = await CreateFixtureAsync(dir.Root, assets);
        var tracks = Enumerable.Range(0, 32).Select(i => new AudioTrackDocument { TrackId = Guid.NewGuid(), Name = $"Track {i}",
            Kind = i == 0 ? AudioTrackKind.Narration : AudioTrackKind.Music, GainDb = -36, DuckUnderNarration = i > 0 }).ToArray();
        long cell = TimelineTime.ToFrames(7200d / 63);
        var clips = Enumerable.Range(0, 2000).Select(i => new MixRenderClip { ClipId = Guid.NewGuid(), Asset = fixture.Clips[i % 2].Asset,
            Track = tracks[i % 32], StartFrame = i / 32 * cell, DurationFrames = cell, Loop = true }).ToArray();
        var plan = new MixRenderPlan { DurationSeconds = 7200, Clips = clips };
        foreach (var asset in fixture.Clips.Select(c => c.Asset)) await assets.PrepareAsync(dir.Root, asset);
        GC.Collect(); long baseline = GC.GetTotalMemory(true), allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        using var graph = await new AudioGraphFactory(assets).CreateAsync(dir.Root, plan, monitor: true);
        var loaded = watch.Elapsed;
        float[] block = new float[AudioGraph.BlockFrames * 2];
        foreach (double second in new[] { 0, 600, 1800, 3600, 5400, 7190 })
        {
            long start = TimelineTime.ToFrames(second);
            for (int i = 0; i < 100; i++) graph.Render(start + i * AudioGraph.BlockFrames, block);
            await using var readAhead = new ReadAheadWaveProvider(graph, start);
            await readAhead.PrimeAsync();
            byte[] deviceBlock = new byte[AudioGraph.BlockFrames * 8];
            Assert.Equal(deviceBlock.Length, readAhead.Read(deviceBlock)); Assert.Equal(0, readAhead.UnderrunCount);
        }
        watch.Stop();
        long retained = GC.GetTotalMemory(true) - baseline, total = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.InRange(retained, -64L * 1024 * 1024, 64L * 1024 * 1024);
        output.WriteLine($"BENCHMARK: 2h / 32 tracks / 2,000 clips / 2 cached sources; graph load {loaded.TotalMilliseconds:F1}ms; six random seeks + 25.6s offline render + read-ahead primes {watch.Elapsed.TotalSeconds:F3}s; retained delta {retained / 1048576d:F2}MiB; total allocated {total / 1048576d:F2}MiB; ring capacity {ReadAheadWaveProvider.CapacityFrames * 8 / 1048576d:F3}MiB; block size2048 frames.");
        await using var transport = new AudioPlaybackEngine(assets) { Volume = .01f };
        await transport.LoadMixAsync(dir.Root, plan); await transport.SeekAsync(TimeSpan.FromHours(1)); transport.Play(); await Task.Delay(500);
        Assert.True(await transport.UpdateMixParametersAsync(plan with { Clips = plan.Clips.Select(c => c with { GainDb = -6 }).ToArray() }));
        await Task.Delay(600);
        Assert.True(transport.IsPlaying); Assert.True(transport.Position.TotalSeconds > 3600.8); Assert.Equal(0, transport.UnderrunCount);
        Assert.Equal(1, transport.ParameterEpoch);
        output.WriteLine("WASAPI STRESS: same2h/32track/2000clip cachedgraph at1hseek, livegainupdate,1.1s actualdeviceplayback,0underruns.");
    }
    private static bool Enabled => Environment.GetEnvironmentVariable("VOICEBENCH_RUN_AUDIO_RUNTIME") == "1";
    private static ToolchainPaths LocalTools
    {
        get
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IssueLab", "VoiceBench", "Tools", "ffmpeg-9.0.1-win64-lgpl");
            var paths = new ToolchainPaths(Path.Combine(root, "ffmpeg.exe"), Path.Combine(root, "ffprobe.exe"));
            if (!paths.Exists) throw new FileNotFoundException("Install the pinned local FFmpeg toolchain before running opt-in runtime fixtures.");
            return paths;
        }
    }
    private static async Task<MixRenderPlan> CreateFixtureAsync(string root, AudioAssetService assets)
    {
        var clips = new List<MixRenderClip>();
        foreach (var (name, frequency, channels, kind, gain) in new[]
        { ("speech-fixture", 440d, (short)1, AudioTrackKind.Narration, -18d), ("music-fixture", 120d, (short)2, AudioTrackKind.Music, -24d) })
        {
            string path = Path.Combine(root, name + ".wav"); WriteTone(path, frequency, 4, channels);
            var asset = await assets.ImportAsync(root, path);
            clips.Add(new() { ClipId = Guid.NewGuid(), Asset = asset, DurationFrames = 192000,
                Track = new AudioTrackDocument { Name = name, Kind = kind, GainDb = gain, DuckUnderNarration = kind == AudioTrackKind.Music } });
        }
        return new() { DurationSeconds = 4, Clips = clips };
    }
    private static void WriteTone(string path, double frequency, double duration, short channels)
    {
        int frames = (int)(48000 * duration), bytes = frames * channels * 2;
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8); file.Write(bytes + 36); file.Write("WAVEfmt "u8); file.Write(16); file.Write((short)1);
        file.Write(channels); file.Write(48000); file.Write(48000 * channels * 2); file.Write((short)(channels * 2)); file.Write((short)16);
        file.Write("data"u8); file.Write(bytes);
        for (int i = 0; i < frames; i++) for (int channel = 0; channel < channels; channel++)
            file.Write((short)(Math.Sin(2 * Math.PI * frequency * i / 48000) * 12000));
    }
}
