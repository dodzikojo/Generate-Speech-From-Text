using IssueLab.VoiceBench.Audio;
using IssueLab.VoiceBench.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace IssueLab.VoiceBench.Infrastructure.Audio;

public sealed record AudioOutputDevice(string DeviceId, string Name);

/// <summary>One shared device-clock transport for mixes, takes, and auditions.</summary>
public sealed class AudioPlaybackEngine(AudioAssetService assets) : IAsyncDisposable
{
    private WasapiPlayer? _player;
    private MMDevice? _device;
    private ReadAheadWaveProvider? _buffer;
    private AudioGraph? _graph;
    private string? _deviceId;
    private long _startFrame;
    private long _stoppedFrame;
    private bool _loop;
    private long _loopStart;
    private long? _loopEnd;
    private float _volume = 1;
    private MixRenderPlan? _mixPlan;
    private string? _mixRoot;
    private readonly SemaphoreSlim _changes = new(1, 1);
    public event EventHandler<Exception>? PlaybackFailed;
    public bool IsPlaying => _player?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Duration => TimeSpan.FromSeconds((_graph?.DurationFrames ?? 0) / 48000d);
    public AudioMeter Meter => _buffer?.Meter ?? AudioMeter.Silence;
    public long UnderrunCount => _buffer?.UnderrunCount ?? 0;
    public long ParameterEpoch => _graph?.AppliedParameterEpoch ?? 0;
    public MixRenderPlan? CurrentMixPlan => _mixPlan;
    public bool Loop
    {
        get => _loop;
        set
        {
            if (_loop == value) return;
            long frame = TimelineTime.ToFrames(Position.TotalSeconds);
            _loop = value;
            if (_buffer is not null) _buffer.Loop = value;
            if (_player is not null) _ = ReprimeLoopAsync(_player, frame);
        }
    }
    public float Volume
    {
        get => _volume;
        set { if (!float.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value)); _volume = value; if (_buffer is not null) _buffer.Volume = value; }
    }
    public TimeSpan Position
    {
        get
        {
            long duration = _graph?.DurationFrames ?? 0;
            if (duration == 0) return TimeSpan.Zero;
            long frame = _stoppedFrame;
            var player = _player;
            if (player is not null && player.PlaybackState == PlaybackState.Playing)
            {
                try { frame = _startFrame + (long)(player.GetPosition() / (double)player.OutputWaveFormat.AverageBytesPerSecond * 48000); }
                catch (ObjectDisposedException) { }
            }
            long loopEnd = _loopEnd ?? duration;
            if (_loop && loopEnd > _loopStart && frame >= loopEnd) frame = _loopStart + (frame - loopEnd) % (loopEnd - _loopStart);
            return TimeSpan.FromSeconds(Math.Clamp(frame, 0, duration) / 48000d);
        }
    }

    public async Task LoadMixAsync(string root, MixRenderPlan plan, CancellationToken cancellationToken = default)
    {
        var graph = await new AudioGraphFactory(assets).CreateAsync(root, plan, monitor: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        _mixPlan = plan; _mixRoot = root; _loopStart = 0; _loopEnd = null;
        await ReplaceGraphAsync(graph, cancellationToken).ConfigureAwait(false);
    }
    public async Task LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var prepared = await assets.PrepareFileAsync(path, cancellationToken).ConfigureAwait(false);
        _mixPlan = null; _mixRoot = null; _loopStart = 0; _loopEnd = null;
        var graph = new AudioGraph([new AudioGraphClip { Source = new DiskPcmSource(prepared.PcmPath, prepared.OriginalChannels), DurationFrames = prepared.FrameCount }], prepared.FrameCount, monitor: true);
        await ReplaceGraphAsync(graph, cancellationToken).ConfigureAwait(false);
    }
    private async Task ReplaceGraphAsync(AudioGraph graph, CancellationToken token)
    {
        await _changes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await CloseDeviceAsync().ConfigureAwait(false);
            _graph?.Dispose(); _graph = graph; _stoppedFrame = 0;
            await OpenDeviceAsync(0, token).ConfigureAwait(false);
        }
        catch { if (_graph != graph) graph.Dispose(); throw; }
        finally { _changes.Release(); }
    }
    public void Play()
    {
        if (_player is null) throw new InvalidOperationException("Load audio before starting playback.");
        if (_player.PlaybackState == PlaybackState.Paused || (_graph is not null && _stoppedFrame >= _graph.DurationFrames))
        {
            _ = ResumeAsync(_player);
            return;
        }
        _player.Play();
    }
    private async Task ResumeAsync(WasapiPlayer expected)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_player != expected || _graph is null || IsPlaying) return;
            long frame = _stoppedFrame >= _graph.DurationFrames ? 0 : _stoppedFrame;
            // WasapiPlayer.Pause leaves its device clock running. Re-prime from the frozen audible
            // position rather than allowing paused wall time or drained endpoint padding to move the script.
            await CloseDeviceAsync().ConfigureAwait(false);
            await OpenDeviceAsync(frame, CancellationToken.None).ConfigureAwait(false);
            _player!.Play();
        }
        catch (Exception exception) { PlaybackFailed?.Invoke(this, exception); }
        finally { _changes.Release(); }
    }
    private async Task ReprimeLoopAsync(WasapiPlayer expected, long frame)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_player != expected || _graph is null) return;
            bool playing = IsPlaying;
            long end = _loopEnd ?? _graph.DurationFrames;
            if (_loop && (frame < _loopStart || frame >= end)) frame = _loopStart;
            await CloseDeviceAsync().ConfigureAwait(false);
            await OpenDeviceAsync(frame, CancellationToken.None).ConfigureAwait(false);
            if (playing) _player!.Play();
        }
        catch (Exception exception) { PlaybackFailed?.Invoke(this, exception); }
        finally { _changes.Release(); }
    }
    public void Pause()
    {
        _stoppedFrame = TimelineTime.ToFrames(Position.TotalSeconds);
        _player?.Pause();
    }
    public void Stop()
    {
        Pause();
    }
    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_graph is null) return;
            bool playing = IsPlaying;
            long frame = Math.Clamp(TimelineTime.ToFrames(position.TotalSeconds), 0, _graph.DurationFrames);
            await CloseDeviceAsync().ConfigureAwait(false);
            _stoppedFrame = frame;
            await OpenDeviceAsync(frame, cancellationToken).ConfigureAwait(false);
            if (playing) _player!.Play();
        }
        finally { _changes.Release(); }
    }
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        using var devices = new MMDeviceEnumerator();
        List<AudioOutputDevice> result = [];
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        { using (device) result.Add(new(device.ID, device.FriendlyName)); }
        return result;
    }
    public async Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool playing = IsPlaying;
            long frame = TimelineTime.ToFrames(Position.TotalSeconds);
            await CloseDeviceAsync().ConfigureAwait(false);
            _deviceId = deviceId;
            if (_graph is null) return;
            await OpenDeviceAsync(frame, cancellationToken).ConfigureAwait(false);
            if (playing) _player!.Play();
        }
        finally { _changes.Release(); }
    }
    public async Task SetLoopRangeAsync(TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
    {
        long first = TimelineTime.ToFrames(start.TotalSeconds), last = TimelineTime.ToFrames(end.TotalSeconds);
        if (_graph is null || first < 0 || last <= first || last > _graph.DurationFrames) throw new ArgumentOutOfRangeException(nameof(end));
        TimeSpan current = Position;
        _loopStart = first; _loopEnd = last; _loop = true;
        if (current < start || current >= end) current = start;
        await SeekAsync(current, cancellationToken).ConfigureAwait(false);
    }
    public async Task SetSoloTracksAsync(IReadOnlyCollection<Guid> trackIds, CancellationToken cancellationToken = default)
    {
        if (_mixPlan is null || _mixRoot is null) return;
        bool playing = IsPlaying;
        TimeSpan position = Position;
        var plan = _mixPlan with { Clips = _mixPlan.Clips.Select(c => c with { Track = c.Track with { Solo = trackIds.Contains(c.Track.TrackId) } }).ToArray() };
        var graph = await new AudioGraphFactory(assets).CreateAsync(_mixRoot, plan, monitor: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        await ReplaceGraphAsync(graph, cancellationToken).ConfigureAwait(false);
        await SeekAsync(position, cancellationToken).ConfigureAwait(false);
        if (playing) Play();
    }
    /// <summary>Applies only gain/pan/mute changes to the live monitor graph. False requires a structural reload.</summary>
    public async Task<bool> UpdateMixParametersAsync(MixRenderPlan plan, CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mixPlan is null || _graph is null || !SameParameterGraph(_mixPlan, plan)) return false;
            var values = plan.Clips.ToDictionary(c => c.ClipId,
                c => new AudioLiveParameters(c.GainDb + c.Track.GainDb, Math.Clamp(c.Pan + c.Track.Pan, -1, 1), c.Muted || c.Track.Muted));
            _graph.UpdateLiveParameters(values);
            _mixPlan = plan;
            return true;
        }
        finally { _changes.Release(); }
    }
    private static bool SameParameterGraph(MixRenderPlan previous, MixRenderPlan next)
    {
        if (previous.ProjectId != next.ProjectId || previous.Scope != next.Scope || previous.ChapterId != next.ChapterId
            || previous.DurationFrames != next.DurationFrames || previous.Clips.Count != next.Clips.Count
            || !previous.Markers.SequenceEqual(next.Markers)) return false;
        var indexed = previous.Clips.ToDictionary(c => c.ClipId);
        foreach (var clip in next.Clips)
        {
            if (!indexed.Remove(clip.ClipId, out var before)
                || before.Track with { GainDb = clip.Track.GainDb, Pan = clip.Track.Pan, Muted = clip.Track.Muted } != clip.Track
                || !before.Envelope.SequenceEqual(clip.Envelope) || !before.RetainedWordRanges.SequenceEqual(clip.RetainedWordRanges)
                || !before.AlignedWords.SequenceEqual(clip.AlignedWords) || !before.ProductionCues.SequenceEqual(clip.ProductionCues)) return false;
            var adjusted = before with { GainDb = clip.GainDb, Pan = clip.Pan, Muted = clip.Muted, Track = clip.Track,
                Envelope = clip.Envelope, RetainedWordRanges = clip.RetainedWordRanges, AlignedWords = clip.AlignedWords, ProductionCues = clip.ProductionCues };
            if (adjusted != clip) return false;
        }
        return indexed.Count == 0;
    }
    private async Task OpenDeviceAsync(long frame, CancellationToken token)
    {
        if (_graph is null) return;
        _startFrame = frame; _stoppedFrame = frame;
        var buffer = new ReadAheadWaveProvider(_graph, frame, _loop, _loopStart, _loopEnd) { Volume = _volume };
        try
        {
            await buffer.PrimeAsync(token).ConfigureAwait(false);
            var builder = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).WithEventSync().WithMmcssThreadPriority();
            if (_deviceId is null) builder.WithDefaultDeviceStreamRouting();
            else { using var enumerator = new MMDeviceEnumerator(); _device = enumerator.GetDevice(_deviceId); builder.WithDevice(_device); }
            _player = await builder.BuildAsync().ConfigureAwait(false);
            _player.Init(buffer);
            _buffer = buffer;
            _player.PlaybackStopped += PlayerStopped;
        }
        catch { await buffer.DisposeAsync().ConfigureAwait(false); await CloseDeviceAsync().ConfigureAwait(false); throw; }
    }
    private void PlayerStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null || _buffer?.Failure is not null)
        {
            PlaybackFailed?.Invoke(this, args.Exception ?? _buffer!.Failure!);
            return;
        }
        _stoppedFrame = _graph?.DurationFrames ?? 0;
    }
    private async Task CloseDeviceAsync()
    {
        if (_player is not null)
        {
            _player.PlaybackStopped -= PlayerStopped;
            await _player.DisposeAsync().ConfigureAwait(false); _player = null;
        }
        if (_buffer is not null) { await _buffer.DisposeAsync().ConfigureAwait(false); _buffer = null; }
        _device?.Dispose(); _device = null;
    }
    public async ValueTask DisposeAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try { await CloseDeviceAsync().ConfigureAwait(false); _graph?.Dispose(); _graph = null; }
        finally { _changes.Release(); }
        _changes.Dispose();
    }
}
