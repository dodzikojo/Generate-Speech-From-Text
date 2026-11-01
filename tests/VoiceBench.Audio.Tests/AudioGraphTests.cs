using System.Runtime.InteropServices;
using Xunit;

namespace IssueLab.VoiceBench.Audio.Tests;

public sealed class AudioGraphTests
{
    [Fact]
    public void LiveMonitorParametersRampExactlyTenMillisecondsAndCannotChangeOfflineGraphs()
    {
        var clip = Clip(new ConstantSource(48000, .25f));
        using var graph = new AudioGraph([clip], 48000, true);
        float[] initial = new float[2048]; graph.Render(0, initial);
        var values = new Dictionary<Guid, AudioLiveParameters> { [clip.ClipId] = new(-6, 1, false) };
        long epoch = graph.UpdateLiveParameters(values);
        values[clip.ClipId] = new(0, 0, false); // Caller mutations cannot alter a published epoch.
        float[] ramp = new float[AudioGraph.ParameterRampFrames * 2]; graph.Render(1024, ramp);
        Assert.Equal(.25 * (479d / 480), ramp[0], 6);
        Assert.Equal(0, ramp[^2]); Assert.Equal(.25 * AudioGraph.Db(-6), ramp[^1], 6);
        Assert.Equal(epoch, graph.AppliedParameterEpoch);
        using var offline = new AudioGraph([clip], 48000);
        Assert.Throws<InvalidOperationException>(() => offline.UpdateLiveParameters(values));
        offline.Render(1024, ramp); Assert.All(ramp, value => Assert.Equal(.25f, value));
    }

    [Fact]
    public void LiveMuteAndUnmuteKeepContinuousRampsAndChangeDuckSidechain()
    {
        var voice = Clip(new ConstantSource(48000, 0)) with { Narration = true };
        var music = Clip(new ConstantSource(48000, .25f)) with { Duck = true };
        using var graph = new AudioGraph([voice, music], 48000, true);
        float[] initial = new float[2]; graph.Render(4800, initial);
        float old = initial[0];
        graph.UpdateLiveParameters(new Dictionary<Guid, AudioLiveParameters> { [voice.ClipId] = new(0, 0, true), [music.ClipId] = new(0, 0, false) });
        float[] ramp = new float[AudioGraph.ParameterRampFrames * 2]; graph.Render(4801, ramp);
        Assert.Equal(old + (.25 - old) / 480, ramp[0], 6);
        Assert.Equal(.25f, ramp[^1]);
        graph.UpdateLiveParameters(new Dictionary<Guid, AudioLiveParameters> { [voice.ClipId] = new(0, 0, true), [music.ClipId] = new(0, 0, true) });
        graph.Render(5281, ramp); Assert.Equal(.25 * 479 / 480, ramp[0], 6); Assert.Equal(0, ramp[^1]);
        graph.UpdateLiveParameters(new Dictionary<Guid, AudioLiveParameters> { [voice.ClipId] = new(0, 0, true), [music.ClipId] = new(0, 0, false) });
        graph.Render(5761, ramp); Assert.Equal(.25 / 480, ramp[0], 6); Assert.Equal(.25f, ramp[^1]);
    }

    [Fact]
    public async Task LiveParameterEpochsCoalesceConcurrentUpdatesWithoutACommandQueue()
    {
        var clip = Clip(new ConstantSource(48000, .25f)); using var graph = new AudioGraph([clip], 48000, true);
        var epochs = new System.Collections.Concurrent.ConcurrentDictionary<long, double>();
        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            double gain = -(index % 12);
            long epoch = graph.UpdateLiveParameters(new Dictionary<Guid, AudioLiveParameters> { [clip.ClipId] = new(gain, 0, false) });
            epochs[epoch] = gain;
        })));
        float[] samples = new float[960]; graph.Render(0, samples);
        Assert.Equal(epochs.Keys.Max(), graph.AppliedParameterEpoch);
        Assert.Equal(.25 * AudioGraph.Db(epochs[graph.AppliedParameterEpoch]), samples[^1], 6);
        Assert.Throws<ArgumentException>(() => graph.UpdateLiveParameters(new Dictionary<Guid, AudioLiveParameters> { [Guid.NewGuid()] = new(0, 0, false) }));
    }
    [Fact]
    public void PlacementAndSourceTrimUseLongFramesBeyondIntRange()
    {
        long start = (long)int.MaxValue + 200;
        using var graph = new AudioGraph([Clip(new RampSource(start + 1000)) with
            { StartFrame = start, SourceStartFrame = start + 7, DurationFrames = 3 }], start + 3);
        float[] output = new float[10];
        Assert.Equal(5, graph.Render(start - 2, output));
        Assert.Equal(0, output[0]); Assert.Equal(0, output[2]);
        Assert.Equal((float)(start + 7), output[4]); Assert.Equal((float)(start + 9), output[8]);
    }

    [Fact]
    public void ExplicitLoopRepeatsSourceWithoutStretch()
    {
        using var graph = new AudioGraph([Clip(new ArraySource([1, 1, 2, 2, 3, 3])) with
            { SourceStartFrame = 1, DurationFrames = 5, Loop = true }], 5);
        float[] output = new float[10]; graph.Render(0, output);
        Assert.Equal(new float[] { 2, 2, 3, 3, 2, 2, 3, 3, 2, 2 }, output);
    }

    [Fact]
    public void EndOfUnloopedSourceIsSilence()
    {
        using var graph = new AudioGraph([Clip(new ArraySource([1, 1])) with { DurationFrames = 3 }], 3);
        float[] output = new float[6]; graph.Render(0, output);
        Assert.Equal(new float[] { 1, 1, 0, 0, 0, 0 }, output);
    }

    [Theory]
    [InlineData(-1, 1, 0)]
    [InlineData(0, .70710678, .70710678)]
    [InlineData(1, 0, 1)]
    public void MonoPanUsesEqualPower(double pan, double left, double right)
    {
        using var graph = new AudioGraph([Clip(new ConstantSource(1, 1, 1)) with { Pan = pan }], 1);
        float[] output = new float[2]; graph.Render(0, output);
        Assert.Equal(left, output[0], 5); Assert.Equal(right, output[1], 5);
    }

    [Fact]
    public void StereoBalancePreservesOtherChannel()
    {
        using var graph = new AudioGraph([Clip(new ConstantSource(1, .25f)) with { Pan = .5 }], 1);
        float[] output = new float[2]; graph.Render(0, output);
        Assert.Equal(.125f, output[0]); Assert.Equal(.25f, output[1]);
    }

    [Fact]
    public void EnvelopesInterpolateDbAndFadesUseEqualPower()
    {
        using var graph = new AudioGraph([Clip(new ConstantSource(10, 1)) with { DurationFrames = 10,
            FadeInFrames = 4, FadeOutFrames = 4, Envelope = [new(0, 0), new(8, -8)] }], 10);
        float[] output = new float[20]; graph.Render(0, output);
        Assert.Equal(0, output[0]);
        Assert.Equal(Math.Sin(Math.PI / 4) * AudioGraph.Db(-2), output[4], 5);
        Assert.Equal(Math.Sin(Math.PI / 4) * AudioGraph.Db(-8), output[16], 5);
    }

    [Fact]
    public void CrossfadeHasEqualPowerAtMatchingOffsets()
    {
        using var graph = new AudioGraph([
            Clip(new ConstantSource(10, 1)) with { DurationFrames = 10, FadeOutFrames = 4 },
            Clip(new ConstantSource(10, 1)) with { StartFrame = 6, DurationFrames = 4, FadeInFrames = 4 }], 10);
        float[] output = new float[2]; graph.Render(8, output);
        Assert.Equal(Math.Sqrt(2), output[0], 5);
    }

    [Fact]
    public void MonitorSoloIsIgnoredByOfflineExportAndProtectsDevice()
    {
        var a = Clip(new ConstantSource(1, .8f)) with { Solo = true };
        var b = Clip(new ConstantSource(1, .8f));
        using var offline = new AudioGraph([a, b], 1);
        using var monitor = new AudioGraph([a, b], 1, true);
        float[] output = new float[2]; offline.Render(0, output);
        Assert.Equal(1.6f, output[0]); Assert.True(offline.Meter.Clipped);
        monitor.Render(0, output); Assert.Equal(.8f, output[0]);
        using var protectedGraph = new AudioGraph([a with { Solo = false }, b], 1, true);
        protectedGraph.Render(0, output); Assert.Equal(.999f, output[0]); Assert.True(protectedGraph.Meter.Clipped);
    }

    [Fact]
    public void DuckIsEightDbWithFiftyMsAttackAndThreeHundredMsRelease()
    {
        var narration = Clip(new ConstantSource(4800, 0)) with { StartFrame = 2400, DurationFrames = 4800, Narration = true };
        var music = Clip(new ConstantSource(48000, 1)) with { DurationFrames = 48000, Duck = true };
        using var graph = new AudioGraph([music, narration], 48000);
        float[] output = new float[2];
        graph.Render(0, output); Assert.Equal(1, output[0]);
        graph.Render(3600, output); Assert.Equal(AudioGraph.Db(-4), output[0], 5);
        graph.Render(4800, output); Assert.Equal(AudioGraph.Db(-8), output[0], 5);
        graph.Render(14400, output); Assert.Equal(AudioGraph.Db(-4), output[0], 5);
        graph.Render(21600, output); Assert.Equal(1, output[0]);
    }

    [Fact]
    public void RandomAccessAndDifferentBlockSizesProduceSameSamples()
    {
        var clip = Clip(new ConstantSource(20000, .5f)) with { DurationFrames = 20000, FadeInFrames = 4000, FadeOutFrames = 5000,
            Envelope = [new(0, -12), new(10000, 3), new(20000, -8)] };
        using var graph = new AudioGraph([clip], 20000);
        float[] full = new float[40000]; graph.Render(0, full);
        float[] slice = new float[14000]; graph.Render(6000, slice);
        Assert.Equal(full.AsSpan(12000, 14000).ToArray(), slice);
    }

    [Fact]
    public async Task ReadAheadDeviceCallbackNeverReadsTheDiskSource()
    {
        var source = new ThreadCheckingSource();
        using var graph = new AudioGraph([Clip(source) with { DurationFrames = 12000 }], 12000, true);
        await using var buffer = new ReadAheadWaveProvider(graph);
        await buffer.PrimeAsync();
        source.InDeviceCallback.Value = true;
        byte[] samples = new byte[2048 * 8];
        Assert.Equal(samples.Length, buffer.Read(samples));
        source.InDeviceCallback.Value = false;
        Assert.Equal(.25f, MemoryMarshal.Cast<byte, float>(samples)[0]);
        Assert.Equal(0, buffer.UnderrunCount);
    }

    private static AudioGraphClip Clip(IAudioFrameSource source) => new() { ClipId = Guid.NewGuid(), Source = source, DurationFrames = source.FrameCount };
    private sealed class ConstantSource(long frames, float value, int channels = 2) : IAudioFrameSource
    {
        public long FrameCount => frames; public int OriginalChannels => channels;
        public int ReadFrames(long frame, Span<float> stereo) { int count = (int)Math.Min(stereo.Length / 2, Math.Max(0, frames - frame)); stereo[..(count * 2)].Fill(value); return count; }
        public void Dispose() { }
    }
    private sealed class RampSource(long frames) : IAudioFrameSource
    {
        public long FrameCount => frames; public int OriginalChannels => 2;
        public int ReadFrames(long frame, Span<float> stereo) { int count = (int)Math.Min(stereo.Length / 2, frames - frame); for (int i = 0; i < count; i++) { stereo[i * 2] = frame + i; stereo[i * 2 + 1] = frame + i; } return count; }
        public void Dispose() { }
    }
    private sealed class ArraySource(float[] samples) : IAudioFrameSource
    {
        public long FrameCount => samples.Length / 2; public int OriginalChannels => 2;
        public int ReadFrames(long frame, Span<float> stereo) { int count = (int)Math.Min(stereo.Length / 2, FrameCount - frame); samples.AsSpan((int)frame * 2, count * 2).CopyTo(stereo); return count; }
        public void Dispose() { }
    }
    private sealed class ThreadCheckingSource : IAudioFrameSource
    {
        public AsyncLocal<bool> InDeviceCallback { get; } = new();
        public long FrameCount => 12000; public int OriginalChannels => 2;
        public int ReadFrames(long frame, Span<float> stereo) { Assert.False(InDeviceCallback.Value); int count = (int)Math.Min(stereo.Length / 2, FrameCount - frame); stereo[..(count * 2)].Fill(.25f); return count; }
        public void Dispose() { }
    }
}
