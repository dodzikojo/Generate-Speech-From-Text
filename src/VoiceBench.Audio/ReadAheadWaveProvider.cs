using System.Runtime.InteropServices;
using NAudio.Wave;

namespace IssueLab.VoiceBench.Audio;

/// <summary>A bounded 250 ms disk/DSP read-ahead queue. Read performs memory copies and smoothed monitor gain only.</summary>
public sealed class ReadAheadWaveProvider : IWaveProvider, IAsyncDisposable
{
    private readonly AudioGraph _graph;
    public const int CapacityFrames = AudioGraph.SampleRate / 4;
    public const int PrimeFrames = AudioGraph.SampleRate / 8;
    private readonly float[] _ring = new float[CapacityFrames * 2];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _read, _write, _count;
    private volatile bool _complete;
    private volatile bool _loop;
    private Exception? _failure;
    private long _underruns;
    private float _volume = 1, _currentVolume = 1;
    private volatile bool _started;
    private long _loopStart, _loopEnd;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioGraph.SampleRate, 2);
    public AudioMeter Meter { get; private set; } = AudioMeter.Silence;
    public long UnderrunCount => Interlocked.Read(ref _underruns);
    public bool Loop { get => _loop; set { _loop = value; if (value) _complete = false; } }
    public float Volume { get => Volatile.Read(ref _volume); set { value = Math.Clamp(value, 0, 1); if (!_started) _currentVolume = value; Volatile.Write(ref _volume, value); } }
    public Exception? Failure => _failure;

    public ReadAheadWaveProvider(AudioGraph graph, long startFrame = 0, bool loop = false, long loopStart = 0, long? loopEnd = null)
    {
        _graph = graph;
        _loop = loop;
        _loopStart = loopStart;
        _loopEnd = loopEnd ?? graph.DurationFrames;
        if (_loopStart < 0 || _loopEnd > graph.DurationFrames || _loopEnd < _loopStart)
            throw new ArgumentOutOfRangeException(nameof(loopStart));
        _worker = Task.Run(() => ProduceAsync(startFrame));
    }

    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        while (!_complete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) { if (_count >= PrimeFrames * 2) return; }
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        if (_failure is not null) throw new IOException("Audio read-ahead failed.", _failure);
    }

    private async Task ProduceAsync(long position)
    {
        float[] block = new float[AudioGraph.BlockFrames * 2];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int space;
                lock (_gate) space = _ring.Length - _count;
                if (space < block.Length) { await Task.Delay(3, _stop.Token).ConfigureAwait(false); continue; }
                long end = _loop ? _loopEnd : _graph.DurationFrames;
                if (position >= end)
                {
                    if (!_loop || _loopEnd <= _loopStart)
                    {
                        _complete = true;
                        await Task.Delay(3, _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    _complete = false;
                    position = _loopStart;
                }
                int requested = (int)Math.Min(AudioGraph.BlockFrames, end - position);
                int frames = _graph.Render(position, block.AsSpan(0, requested * 2));
                if (frames == 0) break;
                int samples = frames * 2;
                lock (_gate)
                {
                    int first = Math.Min(samples, _ring.Length - _write);
                    block.AsSpan(0, first).CopyTo(_ring.AsSpan(_write));
                    block.AsSpan(first, samples - first).CopyTo(_ring);
                    _write = (_write + samples) % _ring.Length;
                    _count += samples;
                }
                position += frames;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) { _failure = exception; }
        finally { _complete = true; }
    }

    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<byte> buffer)
    {
        _started = true;
        Span<float> target = MemoryMarshal.Cast<byte, float>(buffer[..(buffer.Length - buffer.Length % 8)]);
        int got;
        lock (_gate)
        {
            got = Math.Min(target.Length, _count);
            int first = Math.Min(got, _ring.Length - _read);
            _ring.AsSpan(_read, first).CopyTo(target);
            _ring.AsSpan(0, got - first).CopyTo(target[first..]);
            _read = (_read + got) % _ring.Length;
            _count -= got;
        }
        if (got == 0 && _complete) { Meter = AudioMeter.Silence; return 0; }
        if (got < target.Length && !_complete)
        {
            target[got..].Clear();
            Interlocked.Increment(ref _underruns);
            got = target.Length;
        }
        float pl = 0, pr = 0; double sl = 0, sr = 0;
        for (int i = 0; i < got; i += 2)
        {
            _currentVolume += (Volume - _currentVolume) / 480;
            float l = target[i] * _currentVolume, r = target[i + 1] * _currentVolume;
            target[i] = l; target[i + 1] = r;
            pl = Math.Max(pl, Math.Abs(l)); pr = Math.Max(pr, Math.Abs(r)); sl += l * (double)l; sr += r * (double)r;
        }
        Meter = new(pl, pr, got == 0 ? 0 : (float)Math.Sqrt(sl / (got / 2)), got == 0 ? 0 : (float)Math.Sqrt(sr / (got / 2)), _graph.Meter.Clipped);
        return got * 4;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
