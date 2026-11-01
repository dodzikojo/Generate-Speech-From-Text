namespace IssueLab.VoiceBench.Audio;

public interface IAudioFrameSource : IDisposable
{
    long FrameCount { get; }
    int OriginalChannels { get; }
    /// <summary>Reads interleaved stereo float at 48 kHz. Called only on the render worker.</summary>
    int ReadFrames(long frame, Span<float> stereo);
}

public sealed record AudioEnvelopePoint(long Frame, double GainDb);
public readonly record struct AudioLiveParameters(double GainDb, double Pan, bool Muted);
public sealed record AudioGraphClip
{
    public Guid ClipId { get; init; }
    public required IAudioFrameSource Source { get; init; }
    public Guid TrackId { get; init; }
    public long StartFrame { get; init; }
    public long SourceStartFrame { get; init; }
    public long DurationFrames { get; init; }
    public double GainDb { get; init; }
    public double Pan { get; init; }
    public long FadeInFrames { get; init; }
    public long FadeOutFrames { get; init; }
    public long FadeInOffsetFrames { get; init; }
    public long FadeOutOffsetFrames { get; init; }
    public bool Muted { get; init; }
    public bool SuppressOutput { get; init; }
    public bool Solo { get; init; }
    public bool Loop { get; init; }
    public bool Narration { get; init; }
    public bool Duck { get; init; }
    public double DuckDepthDb { get; init; } = 8;
    public long DuckAttackFrames { get; init; } = 2_400;
    public long DuckReleaseFrames { get; init; } = 14_400;
    public IReadOnlyList<AudioEnvelopePoint> Envelope { get; init; } = [];
}

public sealed record AudioMeter(float PeakLeft, float PeakRight, float RmsLeft, float RmsRight, bool Clipped)
{
    public static AudioMeter Silence { get; } = new(0, 0, 0, 0, false);
}

/// <summary>Deterministic, seekable mixer shared by offline and live rendering. Never used by a device callback.</summary>
public sealed class AudioGraph : IDisposable
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BlockFrames = 2_048;
    private readonly AudioGraphClip[] _clips;
    private readonly (long Start, long End)[] _narration;
    private readonly float[] _scratch = new float[BlockFrames * Channels];
    private readonly bool _monitor;
    private readonly bool _hasSolo;
    private readonly object _parameterGate = new();
    private readonly LiveState[] _liveStates;
    private LiveSnapshot? _liveSnapshot;
    private long _nextEpoch, _appliedEpoch;
    private (long Start, long End)[] _activeNarration;
    public long AppliedParameterEpoch => Interlocked.Read(ref _appliedEpoch);
    public const int ParameterRampFrames = 480;
    public long DurationFrames { get; }
    public AudioMeter Meter { get; private set; } = AudioMeter.Silence;

    public AudioGraph(IEnumerable<AudioGraphClip> clips, long durationFrames, bool monitor = false)
    {
        _clips = clips.OrderBy(c => c.StartFrame).ToArray();
        if (durationFrames < 0 || _clips.Any(c => c.StartFrame < 0 || c.SourceStartFrame < 0 || c.DurationFrames < 0
            || c.StartFrame > long.MaxValue - c.DurationFrames
            || !double.IsFinite(c.GainDb) || !double.IsFinite(c.Pan) || c.Pan is < -1 or > 1
            || c.FadeInFrames < 0 || c.FadeOutFrames < 0
            || c.Envelope.Any(p => p.Frame < 0 || !double.IsFinite(p.GainDb))))
            throw new ArgumentException("The graph contains invalid clip geometry or gain.", nameof(clips));
        DurationFrames = durationFrames;
        _monitor = monitor;
        _hasSolo = monitor && _clips.Any(c => c.Solo && !c.Muted);
        List<(long Start, long End)> speech = [];
        foreach (var clip in _clips.Where(c => c.Narration && Audible(c)))
        {
            long end = checked(clip.StartFrame + clip.DurationFrames);
            if (speech.Count > 0 && clip.StartFrame <= speech[^1].End)
                speech[^1] = (speech[^1].Start, Math.Max(speech[^1].End, end));
            else speech.Add((clip.StartFrame, end));
        }
        _narration = speech.ToArray();
        _activeNarration = _narration;
        _liveStates = _clips.Select(c =>
        {
            var (left, right) = Coefficients(c, new(c.GainDb, c.Pan, !Audible(c) || c.SuppressOutput));
            return new LiveState { Left = left, Right = right, TargetLeft = left, TargetRight = right };
        }).ToArray();
    }

    /// <summary>Publishes a complete immutable monitor parameter epoch. Only the render worker mutates ramp state.</summary>
    public long UpdateLiveParameters(IReadOnlyDictionary<Guid, AudioLiveParameters> parameters)
    {
        if (!_monitor) throw new InvalidOperationException("Offline graphs cannot accept monitoring parameter updates.");
        if (_clips.Select(c => c.ClipId).Distinct().Count() != _clips.Length || parameters.Count != _clips.Length
            || _clips.Any(c => !parameters.ContainsKey(c.ClipId))
            || parameters.Values.Any(p => !double.IsFinite(p.GainDb) || p.GainDb is < -192 or > 48 || !double.IsFinite(p.Pan) || p.Pan is < -1 or > 1))
            throw new ArgumentException("Supply one valid gain/pan/mute value for every exact clip ID.", nameof(parameters));
        var copied = new Dictionary<Guid, AudioLiveParameters>(parameters);
        List<(long Start, long End)> speech = [];
        foreach (var clip in _clips.Where(c => c.Narration && !copied[c.ClipId].Muted && (!_hasSolo || c.Solo)))
        {
            long end = checked(clip.StartFrame + clip.DurationFrames);
            if (speech.Count > 0 && clip.StartFrame <= speech[^1].End) speech[^1] = (speech[^1].Start, Math.Max(speech[^1].End, end));
            else speech.Add((clip.StartFrame, end));
        }
        lock (_parameterGate)
        {
            long epoch = ++_nextEpoch;
            Volatile.Write(ref _liveSnapshot, new(epoch, copied, speech.ToArray()));
            return epoch;
        }
    }

    public int Render(long frame, Span<float> destination)
    {
        if (frame < 0 || destination.Length % Channels != 0) throw new ArgumentOutOfRangeException(nameof(frame));
        destination.Clear();
        int total = (int)Math.Min(destination.Length / Channels, Math.Max(0, DurationFrames - frame));
        for (int offset = 0; offset < total; offset += BlockFrames)
        {
            int frames = Math.Min(BlockFrames, total - offset);
            RenderBlock(frame + offset, destination.Slice(offset * Channels, frames * Channels));
        }
        float pl = 0, pr = 0; double sl = 0, sr = 0;
        bool clipped = false;
        for (int i = 0; i < total * Channels; i += Channels)
        {
            float l = destination[i], r = destination[i + 1];
            pl = Math.Max(pl, Math.Abs(l)); pr = Math.Max(pr, Math.Abs(r));
            sl += l * (double)l; sr += r * (double)r;
            clipped |= Math.Abs(l) > 1 || Math.Abs(r) > 1;
            if (_monitor) { destination[i] = Math.Clamp(l, -.999f, .999f); destination[i + 1] = Math.Clamp(r, -.999f, .999f); }
        }
        Meter = new(pl, pr, total == 0 ? 0 : (float)Math.Sqrt(sl / total), total == 0 ? 0 : (float)Math.Sqrt(sr / total), clipped);
        return total;
    }

    private bool Audible(AudioGraphClip clip) => !clip.Muted && (!_hasSolo || clip.Solo);

    private void RenderBlock(long frame, Span<float> output)
    {
        long end = frame + output.Length / Channels;
        var snapshot = Volatile.Read(ref _liveSnapshot);
        if (_monitor && snapshot is not null && snapshot.Epoch != _appliedEpoch)
        {
            for (int index = 0; index < _clips.Length; index++)
            {
                var clip = _clips[index]; var value = snapshot.Values[clip.ClipId];
                var coefficients = Coefficients(clip, value with { Muted = value.Muted || clip.SuppressOutput || _hasSolo && !clip.Solo });
                var state = _liveStates[index];
                state.TargetLeft = coefficients.Left; state.TargetRight = coefficients.Right;
                bool active = clip.StartFrame < end && clip.StartFrame + clip.DurationFrames > frame;
                state.Remaining = active ? ParameterRampFrames : 0;
                state.DuckRemaining = active ? ParameterRampFrames : 0;
                if (!active) { state.Left = state.TargetLeft; state.Right = state.TargetRight; }
            }
            _activeNarration = snapshot.Narration;
            Interlocked.Exchange(ref _appliedEpoch, snapshot.Epoch);
        }
        for (int clipIndex = 0; clipIndex < _clips.Length; clipIndex++)
        {
            AudioGraphClip clip = _clips[clipIndex];
            LiveState state = _liveStates[clipIndex];
            if (clip.StartFrame >= end) break;
            bool silent = _monitor ? state.Left == 0 && state.Right == 0 && state.TargetLeft == 0 && state.TargetRight == 0 : !Audible(clip) || clip.SuppressOutput;
            if (silent || clip.StartFrame + clip.DurationFrames <= frame) continue;
            long begin = Math.Max(frame, clip.StartFrame), stop = Math.Min(end, clip.StartFrame + clip.DurationFrames);
            int frames = (int)(stop - begin);
            Span<float> samples = _scratch.AsSpan(0, frames * Channels);
            samples.Clear();
            long local = begin - clip.StartFrame;
            long available = clip.Source.FrameCount - clip.SourceStartFrame;
            if (available <= 0) continue;
            int done = 0;
            while (done < frames)
            {
                long position = local + done;
                if (clip.Loop) position %= available;
                if (position >= available) break;
                int read = (int)Math.Min(frames - done, available - position);
                int got = clip.Source.ReadFrames(clip.SourceStartFrame + position, samples.Slice(done * Channels, read * Channels));
                if (got <= 0) break;
                done += got;
            }
            var staticCoefficients = Coefficients(clip, new(clip.GainDb, clip.Pan, false));
            for (int i = 0; i < frames; i++)
            {
                long at = local + i;
                double left = staticCoefficients.Left, right = staticCoefficients.Right;
                if (_monitor)
                {
                    if (state.Remaining > 0)
                    {
                        state.Left += (state.TargetLeft - state.Left) / state.Remaining;
                        state.Right += (state.TargetRight - state.Right) / state.Remaining;
                        state.Remaining--;
                    }
                    left = state.Left; right = state.Right;
                }
                double gain = 1;
                if (clip.Envelope.Count > 0) gain *= Db(Envelope(clip.Envelope, at));
                double fadeInPosition = at + (double)clip.FadeInOffsetFrames;
                if (clip.FadeInFrames > 0 && fadeInPosition < clip.FadeInFrames)
                    gain *= Math.Sin(Math.Clamp(fadeInPosition / clip.FadeInFrames, 0, 1) * Math.PI / 2);
                double remaining = clip.DurationFrames - at + (double)clip.FadeOutOffsetFrames;
                if (clip.FadeOutFrames > 0 && remaining < clip.FadeOutFrames)
                    gain *= Math.Sin(Math.Clamp(remaining / clip.FadeOutFrames, 0, 1) * Math.PI / 2);
                if (clip.Duck)
                {
                    double duck = DuckGain(clip, begin + i, _monitor ? _activeNarration : _narration);
                    if (_monitor && state.DuckRemaining > 0)
                    {
                        state.Duck += (duck - state.Duck) / state.DuckRemaining;
                        state.DuckRemaining--;
                    }
                    else state.Duck = duck;
                    gain *= _monitor ? state.Duck : duck;
                }
                int target = checked((int)(begin - frame + i) * Channels);
                output[target] += (float)(samples[i * Channels] * gain * left);
                output[target + 1] += (float)(samples[i * Channels + 1] * gain * right);
            }
        }
    }

    private static double DuckGain(AudioGraphClip clip, long at, (long Start, long End)[] narration)
    {
        int low = 0, high = narration.Length - 1, found = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (narration[middle].Start <= at) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (found < 0) return 1;
        double depth = Amount(narration[found]);
        if (found > 0) depth = Math.Max(depth, Amount(narration[found - 1]));
        if (depth == 0) return 1;
        return Db(-clip.DuckDepthDb * depth);

        double Amount((long Start, long End) speech) => at < speech.End
            ? Math.Min(1, (at - speech.Start) / (double)Math.Max(1, clip.DuckAttackFrames))
            : Math.Max(0, 1 - (at - speech.End) / (double)Math.Max(1, clip.DuckReleaseFrames));
    }

    private static double Envelope(IReadOnlyList<AudioEnvelopePoint> points, long frame)
    {
        if (points.Count == 0) return 0;
        if (frame <= points[0].Frame) return points[0].GainDb;
        for (int i = 1; i < points.Count; i++)
        {
            if (frame > points[i].Frame) continue;
            var a = points[i - 1]; var b = points[i];
            return a.GainDb + (b.GainDb - a.GainDb) * (frame - a.Frame) / Math.Max(1d, b.Frame - a.Frame);
        }
        return points[^1].GainDb;
    }
    public static double Db(double value) => Math.Pow(10, value / 20);
    private static (double Left, double Right) Coefficients(AudioGraphClip clip, AudioLiveParameters parameters)
    {
        if (parameters.Muted) return (0, 0);
        double gain = Db(parameters.GainDb), pan = parameters.Pan;
        if (clip.Source.OriginalChannels == 1)
        { double angle = (pan + 1) * Math.PI / 4; return (gain * Math.Cos(angle), gain * Math.Sin(angle)); }
        return (gain * (pan > 0 ? 1 - pan : 1), gain * (pan < 0 ? 1 + pan : 1));
    }
    private sealed record LiveSnapshot(long Epoch, IReadOnlyDictionary<Guid, AudioLiveParameters> Values, (long Start, long End)[] Narration);
    private sealed class LiveState
    {
        public double Left, Right, TargetLeft, TargetRight;
        public int Remaining, DuckRemaining;
        public double Duck = 1;
    }
    public void Dispose() { foreach (var source in _clips.Select(c => c.Source).Distinct()) source.Dispose(); }
}
